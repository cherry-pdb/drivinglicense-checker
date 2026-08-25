namespace DrivingLicenseReminder;

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = "";
    public long? ChatId { get; set; }
}

public sealed class WatcherOptions
{
    public int IntervalSeconds { get; set; } = 60;
    public string AppointmentsUrl { get; set; } = "https://kep-kepo.gov.cy/appointments";
    public int ReminderMinutes { get; set; } = 15;
    public List<string> TargetDates { get; set; } = [];
    public List<string> ServicePointKeywords { get; set; } = [];
    public List<string> ServiceKeywords { get; set; } = [];
    public List<string> DepartmentKeywords { get; set; } = [];

    /// <summary>Известный id КЕП Энгоми. Если задан вместе с ServicePipe — каталог не качаем.</summary>
    public int? SitePointId { get; set; }

    public string? SitePointName { get; set; }

    /// <summary>Pipe услуги, например 15|184|1 для Issuance of Driving License.</summary>
    public string? ServicePipe { get; set; }

    public string? ServiceName { get; set; }
}

public sealed record FoundSlot(string Date, string Time, string? Service);

public sealed class SlotCheckResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Diagnostic { get; init; }
    public IReadOnlyList<FoundSlot> Slots { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public string SlotKey() =>
        string.Join("|", Slots.Select(s => $"{s.Date} {s.Time} {s.Service}").OrderBy(x => x));
}
