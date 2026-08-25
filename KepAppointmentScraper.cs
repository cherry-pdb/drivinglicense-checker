using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrivingLicenseReminder;

public sealed class KepAppointmentScraper
{
    private const int ClientId = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly WatcherOptions _options;
    private readonly ILogger<KepAppointmentScraper> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int? _sitePointId;
    private string? _sitePointName;
    private List<TrackedService> _services = [];

    public KepAppointmentScraper(IHttpClientFactory httpFactory, IOptions<WatcherOptions> options, ILogger<KepAppointmentScraper> log)
    {
        _http = httpFactory.CreateClient("kep");
        _options = options.Value;
        _log = log;
    }

    public async Task<SlotCheckResult> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _log.LogInformation("Начинаю проверку слотов");
            await EnsureCatalogAsync(ct);
            if (_sitePointId is null || _services.Count == 0)
            {
                _log.LogWarning(
                    "Каталог загружен, но офис={OfficeId}, услуг={ServiceCount}",
                    _sitePointId, _services.Count);
                return Fail(
                    "Не нашёл Энгоми или услугу «Έκδοση Άδειας Οδήγησης».",
                    "Проверьте, что с этой машины открывается kep-kepo.gov.cy (кипрский IP / VPN).");
            }

            var slots = new List<FoundSlot>();
            foreach (var service in _services)
            {
                var months = TargetMonths();
                foreach (var (year, month) in months)
                {
                    _log.LogInformation("Запрашиваю календарь {Year}-{Month:D2}…", year, month);
                    var json = await GetAppointmentsAsync(_sitePointId.Value, service.Pipe, year, month, ct);
                    slots.AddRange(ParseAvailableSlots(json, service.Name));
                }
            }

            slots = slots
                .DistinctBy(s => $"{s.Date}|{s.Time}|{s.Service}")
                .OrderBy(s => s.Date)
                .ThenBy(s => s.Time)
                .ToList();

            _log.LogInformation(
                "Энгоми id={Id} ({Name}), услуг {SvcCount}, свободных слотов {SlotCount}",
                _sitePointId, _sitePointName, _services.Count, slots.Count);

            return new SlotCheckResult { Success = true, Slots = slots };
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Сайт КЕП недоступен");
            return Fail(
                "Сайт kep-kepo.gov.cy не отвечает. Нужен кипрский IP (VPN на этой машине).",
                ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Таймаут запроса к API КЕП");
            return Fail(
                "Сайт отвечает слишком долго (таймаут). Повторите через минуту.",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка проверки слотов");
            return Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureCatalogAsync(CancellationToken ct)
    {
        if (_sitePointId is not null && _services.Count > 0)
        {
            return;
        }

        if (_options.SitePointId is int configuredId &&
            !string.IsNullOrWhiteSpace(_options.ServicePipe))
        {
            _sitePointId = configuredId;
            _sitePointName = string.IsNullOrWhiteSpace(_options.SitePointName)
                ? $"site_point_id={configuredId}"
                : _options.SitePointName;
            _services =
            [
                new TrackedService(
                    _options.ServicePipe.Trim(),
                    string.IsNullOrWhiteSpace(_options.ServiceName)
                        ? "Issuance of Driving License"
                        : _options.ServiceName.Trim())
            ];
            _log.LogInformation(
                "Использую фиксированный офис: {Name} (id {Id}). Услуги: {Services}",
                _sitePointName,
                _sitePointId,
                string.Join("; ", _services.Select(s => $"{s.Name} [{s.Pipe}]")));
            return;
        }

        _log.LogInformation("Загружаю каталог офисов…");
        var global = await GetRedisJsonAsync($"CL_{ClientId}:GLOBAL", ct);
        var office = FindEngomi(global);
        if (office is null)
        {
            _log.LogWarning("Офис Энгоми не найден в каталоге");
            return;
        }

        _sitePointId = office.Value.Id;
        _sitePointName = office.Value.Name;

        _log.LogInformation("Загружаю услуги офиса {OfficeId}…", _sitePointId);
        var local = await GetRedisJsonAsync($"CL_{ClientId}:QL_{_sitePointId}:GLOBAL", ct);
        _services = FindLicenseServices(local);
        _log.LogInformation("Офис: {Name} (id {Id}). Услуги: {Services}",
            _sitePointName, _sitePointId, string.Join("; ", _services.Select(s => $"{s.Name} [{s.Pipe}]")));
    }

    private async Task<JsonElement> GetRedisJsonAsync(string key, CancellationToken ct)
    {
        var url = $"https://kep-kepo.gov.cy/http_fallback/get.php?request=get&key={Uri.EscapeDataString(key)}";
        using var doc = JsonDocument.Parse(await GetStringWithRetryAsync(url, ct, key));
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException($"http_fallback rejected {key}");
        }

        var payload = root.TryGetProperty("data", out var data) ? data.GetString() : root.GetRawText();
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException($"Пустой ответ для {key}");
        }

        using var inner = JsonDocument.Parse(payload);
        return inner.RootElement.Clone();
    }

    private async Task<JsonElement> GetAppointmentsAsync(int sitePointId, string services, int year, int month, CancellationToken ct)
    {
        var url =
            $"https://kep-kepo.gov.cy/api/api.php?action=get_appointments_info" +
            $"&client_id={ClientId}&site_point_id={sitePointId}" +
            $"&services={Uri.EscapeDataString(services)}&year={year}&month={month}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-RQE-CSRF-PROTECTION", "1");
        using var response = await SendWithRetryAsync(request, ct, $"appointments {year}-{month:D2}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        _log.LogInformation("Календарь {Year}-{Month:D2} получен ({Length} байт)", year, month, json.Length);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error_code", out var error))
        {
            throw new InvalidOperationException("API error: " + error.GetRawText());
        }

        return doc.RootElement.Clone();
    }

    private async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct, string label)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithRetryAsync(request, ct, label);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        string label)
    {
        var delays = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15) };
        Exception? last = null;

        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (attempt > 0)
            {
                _log.LogWarning(
                    "Повтор {Attempt}/{Max} для {Label} через {Delay} с",
                    attempt + 1, delays.Length, label, delays[attempt].TotalSeconds);
                await Task.Delay(delays[attempt], ct);
            }

            using var attemptRequest = CloneRequest(request);
            try
            {
                // Сначала только заголовки — тело 300+ КБ через VPN читаем отдельно.
                var response = await _http.SendAsync(
                    attemptRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                await EnsureContentBufferedAsync(response, ct, label);
                return response;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < delays.Length - 1)
            {
                last = ex;
                _log.LogWarning(ex, "Временная ошибка HTTP ({Label}), попытка {Attempt}/{Max}",
                    label, attempt + 1, delays.Length);
            }
        }

        throw last ?? new HttpRequestException($"Не удалось выполнить запрос ({label})");
    }

    private async Task EnsureContentBufferedAsync(HttpResponseMessage response, CancellationToken ct, string label)
    {
        _log.LogInformation("Читаю тело ответа ({Label})…", label);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            var mediaType = response.Content.Headers.ContentType;
            response.Content = new ByteArrayContent(bytes);
            if (mediaType is not null)
            {
                response.Content.Headers.ContentType = mediaType;
            }

            _log.LogInformation(
                "Тело ответа ({Label}) прочитано ({Length} байт), status={Status}",
                label,
                bytes.Length,
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response.Dispose();
            throw new HttpRequestException($"Таймаут чтения тела ответа ({label}) за 90 сек");
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException ||
        (ex is TaskCanceledException && ex.InnerException is TimeoutException or IOException);

    private (int Id, string Name)? FindEngomi(JsonElement global)
    {
        foreach (var office in global.EnumerateObject())
        {
            if (office.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = OfficeName(office.Value);
            if (!ContainsAny(name, _options.ServicePointKeywords) &&
                !ContainsAny(office.Name, ["EKGOMIS", "ENGOMI"]))
            {
                continue;
            }

            var id = office.Value.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            if (id > 0)
            {
                return (id, name);
            }
        }

        return null;
    }

    private List<TrackedService> FindLicenseServices(JsonElement local)
    {
        if (!local.TryGetProperty("services_hierarchy_appointment", out var tree))
        {
            return [];
        }

        var found = new List<TrackedService>();
        WalkServices(tree, found);
        return found;
    }

    private void WalkServices(JsonElement node, List<TrackedService> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray())
                {
                    WalkServices(child, found);
                }

                break;
            case JsonValueKind.Object:
                if (node.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "service")
                {
                    var name = LocalizedName(node);
                    if (IsWantedService(name) &&
                        node.TryGetProperty("service_group_id", out var group) &&
                        node.TryGetProperty("service_id", out var serviceId))
                    {
                        found.Add(new TrackedService($"{group.GetInt32()}|{serviceId.GetInt32()}|1", name));
                    }
                }

                if (node.TryGetProperty("children", out var children))
                {
                    WalkServices(children, found);
                }

                break;
        }
    }

    private bool IsWantedService(string name)
    {
        if (!ContainsAny(name, _options.ServiceKeywords))
        {
            return false;
        }

        var folded = Fold(name);
        if (folded.Contains("μαθητικ") || folded.Contains("learner") ||
            folded.Contains("διεθν") || folded.Contains("international") ||
            folded.Contains("προεξετ") || folded.Contains("pre-exam") ||
            folded.Contains("κυκλοφορ") || folded.Contains("road tax"))
        {
            return false;
        }

        return true;
    }

    private List<FoundSlot> ParseAvailableSlots(JsonElement data, string serviceName)
    {
        var wanted = _options.TargetDates.ToHashSet(StringComparer.Ordinal);
        var targetMonths = TargetMonths().ToHashSet();
        var slots = new List<FoundSlot>();
        foreach (var yearProp in data.EnumerateObject())
        {
            if (!int.TryParse(yearProp.Name, out var year) || yearProp.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var monthProp in yearProp.Value.EnumerateObject())
            {
                if (!int.TryParse(monthProp.Name, out var month) || monthProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!targetMonths.Contains((year, month)))
                {
                    continue;
                }

                foreach (var dayProp in monthProp.Value.EnumerateObject())
                {
                    if (!int.TryParse(dayProp.Name, out var day) || dayProp.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var date = new DateOnly(year, month, day).ToString("yyyy-MM-dd");
                    if (!wanted.Contains(date))
                    {
                        continue;
                    }

                    if (!dayProp.Value.TryGetProperty("slots", out var times) || times.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var timeProp in times.EnumerateObject())
                    {
                        if (timeProp.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var availability = timeProp.Value.TryGetProperty("availability", out var av)
                            ? av.GetInt32()
                            : 0;
                        if (availability == 0)
                        {
                            continue;
                        }

                        slots.Add(new FoundSlot(date, timeProp.Name, serviceName));
                    }
                }
            }
        }

        return slots;
    }

    private List<(int Year, int Month)> TargetMonths()
    {
        var months = new HashSet<(int, int)>();
        foreach (var raw in _options.TargetDates)
        {
            if (DateOnly.TryParse(raw, out var date))
            {
                months.Add((date.Year, date.Month));
            }
        }

        return months.OrderBy(x => x).ToList();
    }

    private static string OfficeName(JsonElement office)
    {
        if (office.TryGetProperty("external_data", out var ext) &&
            ext.TryGetProperty("external_name", out var names))
        {
            return LocalizedNameFromMap(names) + " " + (office.TryGetProperty("internal_name", out var inner) ? inner.GetString() : "");
        }

        return office.TryGetProperty("internal_name", out var name) ? name.GetString() ?? "" : "";
    }

    private static string LocalizedName(JsonElement service)
    {
        if (service.TryGetProperty("name", out var names))
        {
            return LocalizedNameFromMap(names);
        }

        return service.TryGetProperty("internal_name", out var inner) ? inner.GetString() ?? "" : "";
    }

    private static string LocalizedNameFromMap(JsonElement names)
    {
        foreach (var lang in new[] { "gb", "gr" })
        {
            if (names.TryGetProperty(lang, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        return names.ToString();
    }

    private static bool ContainsAny(string text, IEnumerable<string> keywords)
    {
        var hay = Fold(text);
        return keywords.Any(k => hay.Contains(Fold(k), StringComparison.Ordinal));
    }

    private static string Fold(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = decomposed.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);
        return string.Concat(chars).Normalize(NormalizationForm.FormC);
    }

    private static SlotCheckResult Fail(string error, string? diagnostic = null) =>
        new() { Success = false, Error = error, Diagnostic = diagnostic };

    private sealed record TrackedService(string Pipe, string Name);
}
