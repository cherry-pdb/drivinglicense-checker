using System.Net;
using DrivingLicenseReminder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<WatcherOptions>(builder.Configuration.GetSection("Watcher"));
builder.Services.AddHttpClient("kep", client =>
{
    client.Timeout = TimeSpan.FromSeconds(180);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-CY,el;q=0.9,en;q=0.8");
    client.DefaultRequestHeaders.Referrer = new Uri("https://kep-kepo.gov.cy/appointments/");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    var proxyUrl = Environment.GetEnvironmentVariable("KEP_HTTP_PROXY")?.Trim();
    if (!string.IsNullOrWhiteSpace(proxyUrl))
    {
        handler.Proxy = new WebProxy(proxyUrl);
        handler.UseProxy = true;
        Console.WriteLine($"KEP HTTP через прокси: {proxyUrl}");
    }

    return handler;
});
builder.Services.AddSingleton<KepAppointmentScraper>();

var checkOnce = args.Any(a => a.Equals("--check-once", StringComparison.OrdinalIgnoreCase));
if (checkOnce)
{
    var probe = builder.Build();
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var result = await probe.Services.GetRequiredService<KepAppointmentScraper>().CheckAsync(CancellationToken.None);
    if (!result.Success)
    {
        Console.WriteLine("Ошибка: " + result.Error);
        if (!string.IsNullOrWhiteSpace(result.Diagnostic))
        {
            Console.WriteLine(result.Diagnostic);
        }

        Environment.ExitCode = 1;
        return;
    }

    if (result.Slots.Count == 0)
    {
        Console.WriteLine("Сайт открылся. Свободных слотов на 7–9 сентября в Энгоми пока нет.");
        return;
    }

    Console.WriteLine($"Найдено слотов: {result.Slots.Count}");
    foreach (var slot in result.Slots)
    {
        Console.WriteLine($"{slot.Date} {slot.Time}  {slot.Service}");
    }

    return;
}

var token = builder.Configuration["Telegram:BotToken"]?.Trim();
if (string.IsNullOrWhiteSpace(token))
{
    token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim() ?? "";
}

if (string.IsNullOrWhiteSpace(token) || token.Contains("PUT_TOKEN", StringComparison.OrdinalIgnoreCase) || token is "PASTE_TOKEN_HERE")
{
    Console.WriteLine("""
        Нужен токен Telegram-бота.

        1. Откройте Telegram и найдите @BotFather
        2. Отправьте /newbot и следуйте инструкциям
        3. Скопируйте токен

        Windows:
          $env:TELEGRAM_BOT_TOKEN = "123456:ABC..."
          .\run.ps1

        Linux:
          export TELEGRAM_BOT_TOKEN="123456:ABC..."
          ./install-linux.sh

        Проверка сайта без бота:
          dotnet run -- --check-once
        """);
    return;
}

builder.Services.AddSingleton<ITelegramBotClient>(_ =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    return new TelegramBotClient(token, httpClient: http);
});
builder.Services.AddSingleton<TelegramSender>();
builder.Services.AddSingleton<SubscriberStore>();
builder.Services.AddSingleton<SlotWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SlotWatcherService>());
builder.Services.AddHostedService<TelegramBotService>();

var host = builder.Build();
var options = host.Services.GetRequiredService<IOptions<WatcherOptions>>().Value;
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("Монитор слотов КЕП Энгоми запущен.");
Console.WriteLine($"Сайт: {options.AppointmentsUrl}");
Console.WriteLine($"Даты: {string.Join(", ", options.TargetDates)}");
Console.WriteLine($"Интервал: {options.IntervalSeconds} сек.");
Console.WriteLine("Напишите боту /start в Telegram — иначе уведомления некуда слать.");
Console.WriteLine("Остановка: Ctrl+C");
await host.RunAsync();
