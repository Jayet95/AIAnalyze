using System.Globalization;
using System.Text;
using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Reports;
using AIAnalyze.Storage;

namespace AIAnalyze.Chat;

/// <summary>
/// Turns what the app has measured into a compact text briefing for the chat model: the PC, the
/// current readings, recent alerts, recent sessions with their findings, and (when the user is
/// looking at one) a summary of a single session. Summaries, not raw rows, keep each question cheap.
/// </summary>
public static class DiagnosticContext
{
    private const int RecentSessions = 8;
    private const int RecentAlerts = 10;

    public static string Build(
        SampleStore store,
        CaptureStatus? capture,
        long? focusSessionId,
        DateTimeOffset now,
        Health.HealthReport? health = null,
        Sensors.CoolingReport? cooling = null,
        IReadOnlyList<Monitoring.LiveIssue>? live = null,
        Monitoring.TrendState? trends = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Current local time: {Local(now)}.");
        sb.AppendLine();

        AppendSpecs(sb, store);
        AppendLiveAndTrends(sb, live ?? [], trends);
        AppendHealth(sb, health ?? store.GetLatestHealthReport());
        AppendNow(sb, store, capture);
        AppendCooling(sb, cooling);
        AppendAlerts(sb, store);
        AppendSessions(sb, store);

        if (focusSessionId is long id && store.GetSession(id) is { } session)
        {
            AppendFocus(sb, SessionReports.Build(store, session));
        }

        return sb.ToString();
    }

    internal static void AppendSpecs(StringBuilder sb, SampleStore store)
    {
        sb.AppendLine("## This PC");
        if (store.GetLatestSpecs() is not { } s)
        {
            sb.AppendLine("Not collected yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"- Computer: {s.ComputerManufacturer} {s.ComputerModel}");
        sb.AppendLine($"- CPU: {s.CpuName}, {s.CpuCores} cores / {s.CpuLogicalProcessors} threads");
        sb.AppendLine($"- RAM: {s.RamTotalGb:0} GB");
        foreach (var gpu in s.Gpus)
        {
            sb.AppendLine($"- GPU: {gpu.Name}, driver {gpu.DriverVersion}");
        }
        sb.AppendLine($"- Motherboard: {s.MotherboardManufacturer} {s.MotherboardProduct}, BIOS {s.BiosVersion}");
        sb.AppendLine($"- Windows: {s.OsName}, build {s.OsBuild}");
        sb.AppendLine();
    }

    private static void AppendHealth(StringBuilder sb, Health.HealthReport? report)
    {
        if (report is null)
        {
            sb.AppendLine("## System health");
            sb.AppendLine("Not checked yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"## System health (checked {Local(report.AtUtc)}; Event Log covers the last 7 days)");
        foreach (var check in report.Checks.OrderByDescending(c => c.Severity ?? (FindingSeverity)(-1)))
        {
            sb.AppendLine($"- [{check.Severity?.ToString() ?? "OK"}] {check.Title}: {check.Summary}");
            foreach (var line in check.Evidence.Take(5))
            {
                sb.AppendLine($"  - {line}");
            }
        }
        if (report.MemoryModules is { Count: > 0 } modules)
        {
            sb.AppendLine($"- RAM sticks ({modules.Count} of {(report.MemorySlots > 0 ? report.MemorySlots.ToString() : "?")} slots): " +
                string.Join("; ", modules.Select(m =>
                    $"{m.Slot} (channel {m.Channel ?? "?"}) {m.CapacityGb:0.#} GB {m.Type} at {m.ConfiguredSpeedMts?.ToString() ?? "?"} MT/s, reports {m.RatedSpeedMts?.ToString() ?? "?"}, {m.Manufacturer} {m.PartNumber}".TrimEnd())));
        }
        sb.AppendLine($"- Uptime: {report.Uptime.TotalDays:0.#} days");
        if (report.TopByCpu.Count > 0)
        {
            sb.AppendLine("- Busiest programs by CPU (share of the whole CPU): " +
                string.Join(", ", report.TopByCpu.Take(5).Select(p => $"{p.Name} {p.CpuPercent:0}%")));
        }
        if (report.TopByMemory.Count > 0)
        {
            sb.AppendLine("- Biggest programs by memory: " +
                string.Join(", ", report.TopByMemory.Take(5).Select(p => $"{p.Name} {p.MemoryMb:0} MB")));
        }
        sb.AppendLine();
    }

    private static void AppendNow(StringBuilder sb, SampleStore store, CaptureStatus? capture)
    {
        sb.AppendLine("## Right now");
        if (capture is not null)
        {
            sb.AppendLine($"- FPS capture: {capture.State} ({capture.Message})");
            if (capture.CurrentApplication is { } app && capture.LastFrameUtc is { } frame)
            {
                sb.AppendLine($"- Last frame seen from: {app} at {Local(frame)}");
            }
        }

        var latest = store.GetLatestSampleTime();
        if (latest is null)
        {
            sb.AppendLine("- No sensor readings yet.");
            sb.AppendLine();
            return;
        }

        var readings = store.GetReadings(latest.Value.AddMinutes(-2), latest.Value, SessionTimeline.SensorNames);
        var seconds = readings.Select(r => r.TimestampMs / 1000).Distinct().Order().Select(x => new FpsSecond(x, 0)).ToList();
        var rows = SessionTimeline.Build(seconds, readings);
        var gpu = SessionTimeline.SelectGpu(readings);
        if (rows.Count > 0)
        {
            var last = rows[^1].Metrics;
            sb.AppendLine($"- Latest readings ({Local(latest.Value)}): " +
                string.Join(", ", SessionTimeline.Metrics.Select(m => $"{m.Label} {Format(last[m.Label])}")) +
                (gpu is null ? "" : $" (GPU readings from {gpu.Name})"));
            sb.AppendLine("- Last 2 minutes: " + string.Join("; ", SessionTimeline.Metrics
                .Select(m => SummarizeColumn(rows, m.Label))
                .Where(x => x is not null)));
        }
        sb.AppendLine();
    }

    internal static void AppendLiveAndTrends(StringBuilder sb, IReadOnlyList<Monitoring.LiveIssue> live, Monitoring.TrendState? trends)
    {
        sb.AppendLine("## Live issues (noticed by the live monitor in the last hour)");
        if (live.Count == 0)
        {
            sb.AppendLine("None.");
        }
        foreach (var i in live)
        {
            var state = i.Active ? $"ongoing since {Local(i.StartedUtc)}" : $"from {Local(i.StartedUtc)} to {Local(i.LastSeenUtc)}";
            sb.AppendLine($"- [{i.Severity}] {i.Title} ({state}): {i.Detail}");
        }
        sb.AppendLine();

        sb.AppendLine(trends?.AtUtc is { } at
            ? $"## Potential problems from trends (checked {Local(at)})"
            : "## Potential problems from trends");
        if (trends is null || trends.Problems.Count == 0)
        {
            sb.AppendLine(trends?.AtUtc is null ? "Not analyzed yet." : "None found.");
        }
        foreach (var p in trends?.Problems ?? [])
        {
            sb.AppendLine($"- [{p.Severity}] {p.Title}: {p.Detail}");
        }
        sb.AppendLine();
    }

    internal static void AppendCooling(StringBuilder sb, Sensors.CoolingReport? cooling)
    {
        if (cooling is null)
        {
            return;
        }

        string Fan(Sensors.FanReading f) =>
            $"{f.Name} {(f.Rpm is null ? "n/a" : $"{f.Rpm:0} RPM")}{(f.Percent is null ? "" : $" ({f.Percent:0}%)")}";

        sb.AppendLine("## Cooling (right now)");
        sb.AppendLine($"- Cooling type (inferred, not reported by hardware): {cooling.CoolingType}. {cooling.CoolingTypeReason}");
        foreach (var gpu in cooling.Gpus)
        {
            sb.AppendLine($"- {gpu.Name}: {gpu.FanCount} fan(s){(gpu.ZeroRpm ? ", all stopped (zero-RPM mode at low temperature)" : "")}: " +
                string.Join(", ", gpu.Fans.Select(Fan)));
        }
        if (cooling.Pumps.Count > 0)
        {
            sb.AppendLine("- Pumps: " + string.Join(", ", cooling.Pumps.Select(Fan)));
        }
        if (cooling.SystemFans.Count > 0)
        {
            sb.AppendLine("- CPU and case fans: " + string.Join(", ", cooling.SystemFans.Select(Fan)));
        }
        sb.AppendLine();
    }

    private static void AppendAlerts(StringBuilder sb, SampleStore store)
    {
        var alerts = store.GetAlerts(0, RecentAlerts);
        sb.AppendLine("## Recent alerts (newest first)");
        if (alerts.Count == 0)
        {
            sb.AppendLine("None.");
        }
        foreach (var a in alerts)
        {
            var session = a.SessionId is long id ? $" [session {id}]" : "";
            sb.AppendLine($"- {Local(a.AtUtc)} {a.Severity}{session}: {a.Message}");
        }
        sb.AppendLine();
    }

    private static void AppendSessions(StringBuilder sb, SampleStore store)
    {
        var sessions = store.ListSessions(RecentSessions);
        sb.AppendLine("## Recent game sessions (newest first)");
        if (sessions.Count == 0)
        {
            sb.AppendLine("None saved yet.");
        }
        foreach (var s in sessions)
        {
            sb.AppendLine($"- Session {s.Id}: {s.Application}, {Local(s.StartUtc)}, {Duration(s.EndUtc - s.StartUtc)}, median {s.MedianFps:0} FPS");
            foreach (var f in store.GetFindings(s.Id))
            {
                sb.AppendLine($"  - {f.Severity} {f.Kind}: {f.Message}");
            }
        }
        sb.AppendLine();
    }

    internal static void AppendFocus(StringBuilder sb, SessionReport report)
    {
        var s = report.Session;
        sb.AppendLine($"## The session the user is looking at: session {s.Id}");
        sb.AppendLine($"- {s.Application}, {Local(s.StartUtc)} to {Local(s.EndUtc)} ({Duration(s.EndUtc - s.StartUtc)}), {s.FrameCount} frames, median {s.MedianFps:0.0} FPS");
        sb.AppendLine($"- Baseline: {report.Deviation.Message}");
        if (report.Gpu is not null)
        {
            sb.AppendLine($"- GPU readings from: {report.Gpu.Name}");
        }

        var rows = report.Rows;
        if (rows.Count > 0)
        {
            var fps = rows.Select(r => (double)r.Fps).OrderBy(v => v).ToList();
            sb.AppendLine($"- Per-second FPS: min {fps[0]:0}, 10th percentile {Pct(fps, 0.1):0}, median {Pct(fps, 0.5):0}, 90th percentile {Pct(fps, 0.9):0}, max {fps[^1]:0}");
            foreach (var m in SessionTimeline.Metrics)
            {
                if (SummarizeColumn(rows, m.Label) is { } summary)
                {
                    sb.AppendLine($"- {summary}");
                }
            }

            sb.AppendLine("- Slowest seconds (time, FPS, readings):");
            foreach (var row in rows.Skip(1).Take(Math.Max(0, rows.Count - 2)).OrderBy(r => r.Fps).Take(5))
            {
                sb.AppendLine($"  - {Local(DateTimeOffset.FromUnixTimeSeconds(row.UnixSeconds))}: {row.Fps} FPS, " +
                    string.Join(", ", SessionTimeline.Metrics.Select(m => $"{m.Label} {Format(row.Metrics[m.Label])}")));
            }
        }

        sb.AppendLine("- Findings:");
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("  - None.");
        }
        foreach (var f in report.Findings)
        {
            sb.AppendLine($"  - {f.Severity} {f.Kind}: {f.Message}");
        }
        sb.AppendLine();
    }

    internal static string? SummarizeColumn(IReadOnlyList<TimelineRow> rows, string label)
    {
        var values = rows.Select(r => r.Metrics[label]).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
        return values.Count == 0
            ? null
            : $"{label} min {values[0]:0.#}, median {Pct(values, 0.5):0.#}, max {values[^1]:0.#}";
    }

    internal static double Pct(IReadOnlyList<double> sorted, double p) =>
        sorted[(int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

    internal static string Format(double? value) => value is null ? "n/a" : value.Value.ToString("0.#", CultureInfo.InvariantCulture);

    internal static string Local(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string Duration(TimeSpan d) =>
        d.TotalMinutes >= 60 ? $"{(int)d.TotalHours}h {d.Minutes}m" : d.TotalSeconds >= 60 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s";
}
