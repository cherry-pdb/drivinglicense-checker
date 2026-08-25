using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DrivingLicenseReminder;

/// <summary>Сериализует все исходящие сообщения в Telegram, чтобы не конфликтовать с polling.</summary>
public sealed class TelegramSender
{
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<TelegramSender> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TelegramSender(ITelegramBotClient bot, ILogger<TelegramSender> log)
    {
        _bot = bot;
        _log = log;
    }

    public async Task<Message?> SendAsync(
        long chatId,
        string text,
        CancellationToken ct,
        ParseMode? parseMode = null,
        int? replyToMessageId = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var message = await _bot.SendMessage(
                chatId,
                text,
                parseMode: parseMode ?? default,
                replyParameters: replyToMessageId is int id
                    ? new ReplyParameters { MessageId = id }
                    : null,
                cancellationToken: ct);

            _log.LogInformation(
                "Telegram OK chat={ChatId} message_id={MessageId} len={Length}: {Preview}",
                chatId,
                message.MessageId,
                text.Length,
                text.Length <= 80 ? text : text[..80] + "…");
            return message;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Telegram FAIL chat={ChatId} len={Length}", chatId, text.Length);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
