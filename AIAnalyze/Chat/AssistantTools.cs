using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Health;
using AIAnalyze.Reports;
using AIAnalyze.Sensors;
using AIAnalyze.Storage;

namespace AIAnalyze.Chat;

/// <summary>One tool definition in the Messages API wire format.</summary>
public sealed record AssistantToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// The assistant's tools: read-only queries over the data the app keeps on this PC. Every tool returns
/// plain text sized for the model. Nothing here changes the PC. The definitions live in
/// Chat/Assistant/tools.json, which the account server uses too, so both paths offer the same tools.
/// </summary>
public sealed class AssistantTools(
    CaptureStatus? capture,
    HealthState? health,
    LatestSensors? latest,
    string? databasePath = null,
    Func<TimeSpan, IReadOnlyList<HealthEvent>>? readEvents = null)
{
    public const int MaxResultChars = 16_000;

    public static string Instructions { get; } = ReadResource("AIAnalyze.Assistant.instructions.txt").Trim();

    public static string DefinitionsJson { get; } = ReadResource("AIAnalyze.Assistant.tools.json");

    public static IReadOnlyList<AssistantToolDefinition> Definitions { get; } = ParseDefinitions(DefinitionsJson);

    public static bool IsKnown(string name) => Definitions.Any(d => d.Name == name);

    private readonly Func<TimeSpan, IReadOnlyList<HealthEvent>> _readEvents = readEvents ?? (w => HealthCollector.ReadEvents(w));

    private string DatabasePath => databasePath ?? TelemetryPaths.DatabasePath;

    /// <summary>Runs a tool. Errors come back as text for the model, never as exceptions.</summary>
    public string Run(string name, JsonElement input)
    {
        try
        {
            var result = name switch
            {
                "get_health_checks" => HealthChecks(Bool(input, "only_problems") ?? false),
                "search_event_log" => SearchEventLog(
                    Str(input, "category") ?? "all",
                    Math.Clamp(Int(input, "days") ?? 7, 1, 30),
                    Str(input, "contains"),
                    Math.Clamp(Int(input, "limit") ?? 20, 1, 50)),
                "get_sensor_history" => SensorHistory(
                    Math.Clamp(Num(input, "hours") ?? 1, 0.1, 48),
                    Math.Clamp(Num(input, "end_hours_ago") ?? 0, 0, 48),
                    Int(input, "bucket_minutes")),
                "list_sessions" => ListSessions(Str(input, "application"), Math.Clamp(Int(input, "limit") ?? 15, 1, 50)),
                "get_session_details" => Int(input, "session_id") is int id ? SessionDetails(id) : "Error: session_id is required.",
                "get_live_status" => LiveStatus(),
                "get_system_info" => SystemInfo(),
                _ => $"Error: unknown tool {name}.",
            };
            return result.Length <= MaxResultChars ? result : result[..MaxResultChars] + "\n(truncated)";
        }
        catch (Exception ex)
        {
            return $"Error: the tool failed ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    /// <summary>A short line for the UI saying what the assistant is checking.</summary>
    public static string Describe(string name, JsonElement input) => name switch
    {
        "get_health_checks" => "Reading the health checks",
        "search_event_log" => $"Searching the Event Log ({CategoryLabel(Str(input, "category") ?? "all")}, last {Math.Clamp(Int(input, "days") ?? 7, 1, 30)} days)",
        "get_sensor_history" => $"Reading temperature and load history (last {FormatHours(Math.Clamp(Num(input, "hours") ?? 1, 0.1, 48))})",
        "list_sessions" => "Looking through game sessions",
        "get_session_details" => Int(input, "session_id") is int id ? $"Analyzing session {id}" : "Analyzing a session",
        "get_live_status" => "Reading live status",
        "get_system_info" => "Reading system details",
        _ => "Checking",
    };

    // ------------------------------------------------------------------ tools

    private string HealthChecks(bool onlyProblems)
    {
        using var store = Open();
        var report = health?.Latest ?? store.GetLatestHealthReport();
        if (report is null)
        {
            return "No health check has run yet.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Health report from {Local(report.AtUtc)} (Event Log window: last 7 days). Uptime {report.Uptime.TotalDays:0.#} days.");
        var checks = report.Checks
            .Where(c => !onlyProblems || c.Severity is not null)
            .OrderByDescending(c => c.Severity ?? (FindingSeverity)(-1));
        var any = false;
        foreach (var c in checks)
        {
            any = true;
            sb.AppendLine($"- [{c.Severity?.ToString() ?? "OK"}] {c.Title} ({c.Id}): {c.Summary}");
            foreach (var line in c.Evidence)
            {
                sb.AppendLine($"  - {line}");
            }
        }
        if (!any)
        {
            sb.AppendLine("Every check passed.");
        }
        return sb.ToString();
    }

    internal static bool InCategory(HealthEvent e, string category) => category switch
    {
        "crashes" => (e.EventId == 1001 && e.Provider is HealthAnalyzer.ProviderBugCheck or "BugCheck")
                     || (e.Provider == HealthAnalyzer.ProviderKernelPower && e.EventId == 41),
        "hardware_errors" => e.Provider == HealthAnalyzer.ProviderWhea,
        "display_driver" => e.Provider == HealthAnalyzer.ProviderDisplay,
        "app_crashes" => e.Provider is HealthAnalyzer.ProviderAppError or HealthAnalyzer.ProviderAppHang,
        "services" => e.Provider == HealthAnalyzer.ProviderServices,
        "updates" => e.Provider == HealthAnalyzer.ProviderWindowsUpdate,
        _ => true,
    };

    private string SearchEventLog(string category, int days, string? contains, int limit)
    {
        var events = _readEvents(TimeSpan.FromDays(days))
            .Where(e => InCategory(e, category))
            .Where(e => string.IsNullOrWhiteSpace(contains) || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)
                                                             || e.Provider.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.AtUtc)
            .ToList();

        var sb = new StringBuilder();
        var filter = string.IsNullOrWhiteSpace(contains) ? "" : $" containing \"{contains}\"";
        sb.AppendLine($"{events.Count} {CategoryLabel(category)} event(s){filter} in the last {days} days.");
        if (events.Count == 0)
        {
            return sb.ToString();
        }

        sb.AppendLine("Per day: " + string.Join(", ", events
            .GroupBy(e => e.AtUtc.ToLocalTime().Date)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key:yyyy-MM-dd} {g.Count()}")));
        sb.AppendLine($"Newest {Math.Min(limit, events.Count)}:");
        foreach (var e in events.Take(limit))
        {
            var message = OneLine(e.Message);
            sb.AppendLine($"- {Local(e.AtUtc)} {e.Level} {e.Provider} {e.EventId}: {(message.Length > 300 ? message[..300] + "…" : message)}");
        }
        return sb.ToString();
    }

    private string SensorHistory(double hours, double endHoursAgo, int? bucketMinutes)
    {
        using var store = Open();
        var newest = store.GetLatestSampleTime();
        if (newest is null)
        {
            return "No sensor readings are stored yet.";
        }

        var to = DateTimeOffset.UtcNow.AddHours(-endHoursAgo);
        var from = to.AddHours(-hours);
        var readings = store.GetReadings(from, to, SessionTimeline.SensorNames);
        if (readings.Count == 0)
        {
            return $"No sensor readings between {Local(from)} and {Local(to)}. Readings are kept for 2 days and only while the service runs; the newest is from {Local(newest.Value)}.";
        }

        var bucket = TimeSpan.FromMinutes(Math.Clamp(bucketMinutes ?? (int)Math.Max(1, Math.Round(hours * 60 / 24)), 1, 240));
        var seconds = readings.Select(r => r.TimestampMs / 1000).Distinct().Order().Select(s => new FpsSecond(s, 0)).ToList();
        var rows = SessionTimeline.Build(seconds, readings);
        var gpu = SessionTimeline.SelectGpu(readings);

        var sb = new StringBuilder();
        sb.AppendLine($"Readings from {Local(from)} to {Local(to)}, {rows.Count} seconds recorded, in buckets of {FormatMinutes(bucket.TotalMinutes)}" +
                      (gpu is null ? "." : $"; GPU readings from {gpu.Name}."));
        sb.AppendLine("Whole period: " + string.Join("; ", SessionTimeline.Metrics
            .Select(m => DiagnosticContext.SummarizeColumn(rows, m.Label)).Where(s => s is not null)));
        sb.AppendLine("Per bucket (start time: label min/avg/max):");

        var bucketSeconds = (long)bucket.TotalSeconds;
        foreach (var group in rows.GroupBy(r => r.UnixSeconds / bucketSeconds).OrderBy(g => g.Key))
        {
            var parts = new List<string>();
            foreach (var m in SessionTimeline.Metrics)
            {
                var values = group.Select(r => r.Metrics[m.Label]).Where(v => v is not null).Select(v => v!.Value).ToList();
                if (values.Count > 0)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{m.Label} {values.Min():0.#}/{values.Average():0.#}/{values.Max():0.#}"));
                }
            }
            sb.AppendLine($"- {Local(DateTimeOffset.FromUnixTimeSeconds(group.Key * bucketSeconds))}: {string.Join(", ", parts)}");
        }
        return sb.ToString();
    }

    private string ListSessions(string? application, int limit)
    {
        using var store = Open();
        var sessions = store.ListSessions(application is null ? limit : 500)
            .Where(s => application is null || s.Application.Contains(application, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
        if (sessions.Count == 0)
        {
            return application is null ? "No sessions saved yet." : $"No sessions of \"{application}\".";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{sessions.Count} session(s), newest first:");
        foreach (var s in sessions)
        {
            sb.AppendLine($"- Session {s.Id}: {s.Application}, {Local(s.StartUtc)}, {DiagnosticContext.Duration(s.EndUtc - s.StartUtc)}, median {s.MedianFps:0} FPS");
            foreach (var f in store.GetFindings(s.Id))
            {
                sb.AppendLine($"  - {f.Severity} {f.Kind}: {f.Message}");
            }
        }
        return sb.ToString();
    }

    private string SessionDetails(long id)
    {
        using var store = Open();
        if (store.GetSession(id) is not { } session)
        {
            return $"There is no session {id}.";
        }
        var sb = new StringBuilder();
        DiagnosticContext.AppendFocus(sb, SessionReports.Build(store, session));
        return sb.ToString().Replace("## The session the user is looking at: ", "## ");
    }

    private string LiveStatus()
    {
        var sb = new StringBuilder();
        if (latest?.Current is { } now)
        {
            var readings = now.Samples
                .Select(s => new StoredReading(now.AtUtc.ToUnixTimeMilliseconds(), s.SensorId, s.HardwareName, s.SensorName, s.SensorType, s.Value))
                .ToList();
            var rows = SessionTimeline.Build([new FpsSecond(now.AtUtc.ToUnixTimeSeconds(), 0)], readings);
            var gpu = SessionTimeline.SelectGpu(readings);
            sb.AppendLine($"Readings at {Local(now.AtUtc)}: " +
                          string.Join(", ", SessionTimeline.Metrics.Select(m => $"{m.Label} {DiagnosticContext.Format(rows[0].Metrics[m.Label])}")) +
                          (gpu is null ? "" : $" (GPU: {gpu.Name})"));
            sb.AppendLine();
            DiagnosticContext.AppendCooling(sb, CoolingSummary.Build(now.AtUtc, now.Samples));
        }
        else
        {
            sb.AppendLine("No live readings yet (the sensor reader has not reported).");
        }

        if (capture is not null)
        {
            sb.AppendLine($"FPS capture: {capture.State} ({capture.Message})");
            foreach (var app in capture.Applications)
            {
                sb.AppendLine($"- Drawing frames: {app.Name} {app.Fps:0} FPS{(app.Excluded ? " (not tracked as a session)" : "")}");
            }
        }

        using var store = Open();
        if ((health?.Latest ?? store.GetLatestHealthReport()) is { } report)
        {
            if (report.Memory is { } mem)
            {
                sb.AppendLine($"Memory in use at {Local(report.AtUtc)}: {mem.UsedGb:0.#} of {mem.TotalGb:0.#} GB ({mem.UsedPercent:0}%)");
            }
            if (report.TopByCpu.Count > 0)
            {
                sb.AppendLine("Busiest by CPU (share of the whole CPU): " + string.Join(", ", report.TopByCpu.Take(10).Select(p => $"{p.Name} {p.CpuPercent:0.#}%")));
            }
            if (report.TopByMemory.Count > 0)
            {
                sb.AppendLine("Biggest by memory: " + string.Join(", ", report.TopByMemory.Take(10).Select(p => $"{p.Name} {p.MemoryMb:0} MB")));
            }
        }
        return sb.ToString();
    }

    private string SystemInfo()
    {
        using var store = Open();
        var sb = new StringBuilder();
        DiagnosticContext.AppendSpecs(sb, store);
        if (store.GetLatestSpecs() is { } specs)
        {
            foreach (var gpu in specs.Gpus)
            {
                sb.AppendLine($"- {gpu.Name}: {gpu.VramGb:0.#} GB video memory");
            }
            sb.AppendLine($"- CPU max clock: {specs.CpuMaxClockMhz} MHz; Windows version {specs.OsVersion}");
        }

        var report = health?.Latest ?? store.GetLatestHealthReport();
        if (report?.MemoryModules is { Count: > 0 } modules)
        {
            sb.AppendLine($"RAM sticks ({modules.Count} of {(report.MemorySlots > 0 ? report.MemorySlots.ToString(CultureInfo.InvariantCulture) : "?")} slots):");
            foreach (var m in modules)
            {
                sb.AppendLine($"- {m.Slot} (bank {m.Bank}, channel {m.Channel ?? "?"}): {m.CapacityGb:0.#} GB {m.Type}, running {m.ConfiguredSpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "?"} MT/s, rated {m.RatedSpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "?"} MT/s, {m.Manufacturer} {m.PartNumber}".TrimEnd());
            }
        }
        if (report?.Drives is { Count: > 0 } drives)
        {
            sb.AppendLine("Drives:");
            foreach (var d in drives)
            {
                sb.AppendLine($"- {d.Name} {d.Label}: {d.FreeBytes / 1e9:0.#} GB free of {d.TotalBytes / 1e9:0.#} GB ({d.FreePercent:0}% free)".Replace("  ", " "));
            }
        }
        var disks = HealthCollector.ReadPhysicalDisks();
        if (disks.Count > 0)
        {
            sb.AppendLine("Physical disks: " + string.Join("; ", disks.Select(d => $"{d.Name} ({d.MediaType}) health {d.Health}, status {d.OperationalStatus}")));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private SampleStore Open() => new(DatabasePath);

    private static string Local(DateTimeOffset utc) => DiagnosticContext.Local(utc);

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string CategoryLabel(string category) => category switch
    {
        "crashes" => "crash and unexpected shutdown",
        "hardware_errors" => "hardware error",
        "display_driver" => "graphics driver",
        "app_crashes" => "program crash",
        "services" => "service",
        "updates" => "Windows Update",
        _ => "health",
    };

    private static string FormatHours(double hours) => hours >= 1 ? $"{hours:0.#} h" : $"{hours * 60:0} min";

    private static string FormatMinutes(double minutes) => minutes >= 60 ? $"{minutes / 60:0.#} h" : $"{minutes:0} min";

    private static JsonElement? Prop(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? Str(JsonElement input, string name) =>
        Prop(input, name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool? Bool(JsonElement input, string name) =>
        Prop(input, name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static double? Num(JsonElement input, string name) =>
        Prop(input, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;

    private static int? Int(JsonElement input, string name) =>
        Num(input, name) is double d && d is >= int.MinValue and <= int.MaxValue ? (int)Math.Round(d) : null;

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<AssistantToolDefinition> ParseDefinitions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(t => new AssistantToolDefinition(
                t.GetProperty("name").GetString()!,
                t.GetProperty("description").GetString()!,
                t.GetProperty("input_schema").Clone()))
            .ToList();
    }
}
