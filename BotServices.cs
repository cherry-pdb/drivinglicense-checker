using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DrivingLicenseReminder;

public sealed class SubscriberStore
{
    private readonly string _path;
    private readonly ConcurrentDictionary<long, byte> _ids = new();

    public SubscriberStore(IOptions<TelegramOptions> telegram)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "subscribers.json");
        if (File.Exists(_path))
        {
            var json = File.ReadAllText(_path);
            var ids = JsonSerializer.Deserialize<long[]>(json) ?? [];
            foreach (var id in ids)
            {
                _ids[id] = 0;
            }
        }

        if (telegram.Value.ChatId is > 0)
        {
            _ids.TryAdd(telegram.Value.ChatId.Value, 0);
        }
    }

    public IReadOnlyCollection<long> All => _ids.Keys.ToArray();

    public bool Add(long chatId)
    {
        var added = _ids.TryAdd(chatId, 0);
        Persist();
        return added;
    }

    public void Remove(long chatId)
    {
        _ids.TryRemove(chatId, out _);
        Persist();
    }

    private void Persist()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_ids.Keys.ToArray()));
    }
}

public sealed class TelegramBotService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly TelegramSender _sender;
    private readonly SubscriberStore _subscribers;
    private readonly SlotWatcherService _watcher;
    private readonly ILogger<TelegramBotService> _log;

    public TelegramBotService(
        ITelegramBotClient bot,
        TelegramSender sender,
        SubscriberStore subscribers,
        SlotWatcherService watcher,
        ILogger<TelegramBotService> log)
    {
        _bot = bot;
        _sender = sender;
        _subscribers = subscribers;
        _watcher = watcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _log.LogInformation("Бот @{User} запущен. Напишите ему /start в Telegram.", me.Username);

        var options = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _bot.ReceiveAsync(HandleUpdate, HandleError, options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Сбой Telegram polling, повтор через 5 секунд");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleUpdate(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text, Chat.Id: var chatId, MessageId: var messageId })
        {
            return;
        }

        var command = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "/start":
                    _subscribers.Add(chatId);
                    await _sender.SendAsync(chatId, $"""
                        Слежу за слотами КЕП Энгоми (Никосия) на <b>{_watcher.DatesHtml}</b> — любое время.

                        Каждую минуту открываю https://kep-kepo.gov.cy/appointments
                        Как только появится слот на водительские права, сразу напишу сюда.

                        Команды:
                        /status — последняя проверка
                        /check — проверить сейчас
                        /ping — проверка Telegram
                        /stop — отписаться
                        """, ct, ParseMode.Html);
                    break;
                case "/stop":
                    _subscribers.Remove(chatId);
                    await _sender.SendAsync(
                        chatId,
                        "Отписался. Чтобы снова получать уведомления, напишите /start.",
                        ct);
                    break;
                case "/status":
                    await _sender.SendAsync(chatId, _watcher.FormatStatus(), ct, ParseMode.Html);
                    break;
                case "/check":
                    _subscribers.Add(chatId);
                    await _sender.SendAsync(
                        chatId,
                        "Проверяю сайт сейчас… (до 2 минут через VPN)",
                        ct,
                        replyToMessageId: messageId);
                    _ = RunManualCheckAsync(chatId, messageId);
                    break;
                case "/ping":
                    await _sender.SendAsync(chatId, "pong — Telegram работает.", ct);
                    break;
                default:
                    await _sender.SendAsync(
                        chatId,
                        "Напишите /start, /status, /check, /ping или /stop.",
                        ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка обработки команды {Command}", command);
            await _sender.SendAsync(chatId, "Не удалось выполнить команду: " + ex.Message, CancellationToken.None);
        }
    }

    private async Task RunManualCheckAsync(long chatId, int requestMessageId)
    {
        try
        {
            var result = await _watcher.RunOnceAsync(
                notifyEvenIfEmpty: false,
                CancellationToken.None,
                manualReply: true);
            var text = _watcher.FormatCheckReply(result);
            await _sender.SendAsync(
                chatId,
                text,
                CancellationToken.None,
                parseMode: text.Contains('<') ? ParseMode.Html : null,
                replyToMessageId: requestMessageId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка /check для чата {ChatId}", chatId);
            await _sender.SendAsync(chatId, "Ошибка проверки: " + ex.Message, CancellationToken.None);
        }
    }

    private Task HandleError(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        _log.LogWarning(exception, "Ошибка Telegram polling");
        return Task.CompletedTask;
    }
}

public sealed class SlotWatcherService : BackgroundService
{
    private readonly KepAppointmentScraper _scraper;
    private readonly TelegramSender _sender;
    private readonly SubscriberStore _subscribers;
    private readonly WatcherOptions _options;
    private readonly ILogger<SlotWatcherService> _log;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private SlotCheckResult? _last;
    private string _lastNotifiedKey = "";
    private string _lastErrorKey = "";
    private DateTimeOffset _lastErrorSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReminderSentAt = DateTimeOffset.MinValue;

    public SlotWatcherService(
        KepAppointmentScraper scraper,
        TelegramSender sender,
        SubscriberStore subscribers,
        IOptions<WatcherOptions> options,
        ILogger<SlotWatcherService> log)
    {
        _scraper = scraper;
        _sender = sender;
        _subscribers = subscribers;
        _options = options.Value;
        _log = log;
    }

    public string FormatStatus()
    {
        if (_last is null)
        {
            return "Проверок ещё не было — подождите около минуты или напишите /check.";
        }

        var when = _last.CheckedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
        if (!_last.Success)
        {
            return $"Последняя проверка: {when}\nОшибка: {Html(_last.Error ?? "неизвестно")}";
        }

        if (_last.Slots.Count == 0)
        {
            return $"Последняя проверка: {when}\nСлотов на {DatesPlain} пока нет.";
        }

        return $"Последняя проверка: {when}\n{FormatSlots(_last)}";
    }

    public string DatesPlain => FormatDatesLabel(_options.TargetDates, html: false);
    public string DatesHtml => FormatDatesLabel(_options.TargetDates, html: true);

    public string FormatCheckReply(SlotCheckResult result)
    {
        if (!result.Success)
        {
            var text = "Не удалось проверить сайт: " + (result.Error ?? "неизвестная ошибка");
            if (!string.IsNullOrWhiteSpace(result.Diagnostic))
            {
                text += "\n\n" + result.Diagnostic;
            }

            return text;
        }

        if (result.Slots.Count == 0)
        {
            return $"Проверил сайт: свободных слотов на {DatesPlain} пока нет.";
        }

        return FormatSlots(result);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(notifyEvenIfEmpty: false, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _options.IntervalSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    public async Task<SlotCheckResult> RunOnceAsync(bool notifyEvenIfEmpty, CancellationToken ct, bool manualReply = false)
    {
        await _runGate.WaitAsync(ct);

        try
        {
            _log.LogInformation(
                "Старт цикла проверки (notify={Notify}, manual={Manual}, subscribers={Count})",
                notifyEvenIfEmpty, manualReply, _subscribers.All.Count);
            var result = await _scraper.CheckAsync(ct);
            _last = result;
            _log.LogInformation(
                "Проверка завершена: success={Success}, slots={SlotCount}, manual={Manual}",
                result.Success, result.Slots.Count, manualReply);

            if (!result.Success)
            {
                if (!manualReply)
                {
                    await NotifyErrorAsync(result, ct, force: notifyEvenIfEmpty);
                }

                return result;
            }

            if (result.Slots.Count == 0)
            {
                if (!string.IsNullOrEmpty(_lastNotifiedKey))
                {
                    await NotifyAsync($"Слоты на {DatesPlain} в Энгоми только что исчезли. Продолжаю следить.", ct);
                }

                _lastNotifiedKey = "";
                if (notifyEvenIfEmpty && !manualReply)
                {
                    await NotifyAsync($"Проверил сайт: свободных слотов на {DatesPlain} пока нет.", ct);
                }

                if (!string.IsNullOrWhiteSpace(result.Diagnostic) && notifyEvenIfEmpty && !manualReply)
                {
                    await NotifyAsync("Диагностика: " + Html(result.Diagnostic), ct, ParseMode.Html);
                }

                return result;
            }

            var key = result.SlotKey();
            var reminderDue = DateTimeOffset.Now - _lastReminderSentAt >=
                              TimeSpan.FromMinutes(Math.Max(5, _options.ReminderMinutes));
            if (key == _lastNotifiedKey && !notifyEvenIfEmpty && !manualReply && !reminderDue)
            {
                return result;
            }

            _lastNotifiedKey = key;
            _lastReminderSentAt = DateTimeOffset.Now;
            await NotifyAsync(FormatSlots(result), ct, ParseMode.Html);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Сбой цикла проверки");
            var failed = new SlotCheckResult { Success = false, Error = ex.Message };
            _last = failed;
            if (!manualReply)
            {
                await NotifyErrorAsync(failed, ct, force: notifyEvenIfEmpty);
            }

            return failed;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task NotifyErrorAsync(SlotCheckResult result, CancellationToken ct, bool force = false)
    {
        var key = (result.Error ?? "") + (result.Diagnostic ?? "");
        var now = DateTimeOffset.Now;
        if (!force && key == _lastErrorKey && now - _lastErrorSentAt < TimeSpan.FromMinutes(30))
        {
            return;
        }

        _lastErrorKey = key;
        _lastErrorSentAt = now;
        var text = "Не удалось проверить сайт: " + (result.Error ?? "неизвестная ошибка");
        if (!string.IsNullOrWhiteSpace(result.Diagnostic))
        {
            text += "\n\n" + result.Diagnostic;
        }

        await NotifyAsync(text, ct);
    }

    private async Task NotifyAsync(string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        if (_subscribers.All.Count == 0)
        {
            _log.LogWarning("Некому отправить уведомление — напишите боту /start");
            return;
        }

        foreach (var chatId in _subscribers.All)
        {
            await _sender.SendAsync(chatId, text, ct, parseMode);
        }
    }

    private static string FormatSlots(SlotCheckResult result)
    {
        var groups = result.Slots.GroupBy(s => s.Date).OrderBy(g => g.Key);
        var lines = new List<string>
        {
            "🟢 <b>Появились слоты на водительские права!</b>",
            "📍 КЕП Энгоми, Никосия"
        };

        foreach (var group in groups)
        {
            if (!DateOnly.TryParse(group.Key, out var date))
            {
                continue;
            }

            var times = string.Join(", ", group.Select(s => s.Time).Distinct());
            lines.Add($"📅 {date:dd.MM.yyyy}: {Html(times)}");
            var service = group.Select(s => s.Service).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(service) && service != "(выбранная услуга)")
            {
                lines.Add($"📄 {Html(service)}");
            }
        }

        lines.Add("");
        lines.Add("🔗 Записаться: https://kep-kepo.gov.cy/appointments");
        return string.Join("\n", lines);
    }

    private static string FormatDatesLabel(IEnumerable<string> rawDates, bool html)
    {
        var dates = rawDates
            .Select(raw => DateOnly.TryParse(raw, out var d) ? d : (DateOnly?)null)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .OrderBy(d => d)
            .ToList();

        if (dates.Count == 0)
        {
            return "выбранные даты";
        }

        var days = string.Join(", ", dates.Select(d => d.Day.ToString()));
        if (dates.Count >= 2)
        {
            var lastComma = days.LastIndexOf(", ", StringComparison.Ordinal);
            if (lastComma >= 0)
            {
                days = days[..lastComma] + " и " + days[(lastComma + 2)..];
            }
        }

        var year = dates[0].Year;
        var month = dates[0].ToString("MMMM", new System.Globalization.CultureInfo("ru-RU"));
        var label = $"{days} {month} {year}";
        return html ? Html(label) : label;
    }

    private static string Html(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
