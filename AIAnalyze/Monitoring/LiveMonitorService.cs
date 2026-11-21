using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Health;
using AIAnalyze.Reports;
using AIAnalyze.Sensors;
using AIAnalyze.Storage;

namespace AIAnalyze.Monitoring;

/// <summary>The key readings of one sensor snapshot, by metric label (CPU °C, GPU %, ...).</summary>
public static class LiveMetrics
{
    public static IReadOnlyDictionary<string, double?> From(LatestSensors.Snapshot snapshot)
    {
        var readings = snapshot.Samples
            .Select(s => new StoredReading(snapshot.AtUtc.ToUnixTimeMilliseconds(), s.SensorId, s.HardwareName, s.SensorName, s.SensorType, s.Value))
            .ToList();
        var rows = SessionTimeline.Build([new FpsSecond(snapshot.AtUtc.ToUnixTimeSeconds(), 0)], readings);
        return rows[0].Metrics;
    }
}

/// <summary>
/// The live part of the assistant: every 5 seconds it reads the sensors, the game being played and its
/// FPS, and memory, and runs <see cref="LiveIssueEngine"/>. New issues become alerts, which the AIAnalyze
/// window shows as Windows notifications, so the user hears about a problem while it happens.
/// It only uses readings the app already has, plus a short process sample when the CPU is busy.
/// </summary>
public sealed class LiveMonitorService(
    ILogger<LiveMonitorService> logger,
    LiveIssueEngine engine,
    LatestSensors latest,
    CaptureStatus capture) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BaselineInterval = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (DateTimeOffset At, double? Fps)> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private MemoryUse? _memory;
    private DateTimeOffset _memoryAt = DateTimeOffset.MinValue;
    private string? _topCpu;
    private DateTimeOffset _topCpuAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var reading = await ReadAsync(stoppingToken);
                if (reading is null)
                {
                    continue;
                }
                var raised = engine.Evaluate(reading);
                if (raised.Count == 0)
                {
                    continue;
                }

                using var store = new SampleStore(TelemetryPaths.DatabasePath);
                foreach (var issue in raised)
                {
                    logger.LogInformation("Live issue: {Title}: {Detail}", issue.Title, issue.Detail);
                    if (LiveIssueEngine.Notifies(issue.Id))
                    {
                        store.AddAlert(issue.StartedUtc, "Live", issue.Severity, $"{issue.Title}: {issue.Detail}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Live monitor tick failed");
            }
        }
    }

    private async Task<LiveReading?> ReadAsync(CancellationToken cancellationToken)
    {
        if (latest.Current is not { } snapshot || DateTimeOffset.UtcNow - snapshot.AtUtc > TimeSpan.FromSeconds(15))
        {
            return null;
        }
        var now = snapshot.AtUtc;
        var m = LiveMetrics.From(snapshot);

        var apps = capture.Applications;
        var game = apps.FirstOrDefault(a => !a.Excluded);
        var other = apps.FirstOrDefault(a => a.Excluded && a.Fps >= 20);

        if (now - _memoryAt >= MemoryInterval)
        {
            _memory = HealthCollector.ReadMemoryFast();
            _memoryAt = now;
        }

        var cpuLoad = m[SessionTimeline.CpuLoad];
        if (cpuLoad >= 80 && now - _topCpuAt >= ProcessInterval)
        {
            _topCpuAt = now;
            var (byCpu, _) = await HealthCollector.SampleProcessesAsync(TimeSpan.FromSeconds(2), cancellationToken);
            _topCpu = byCpu
                .Where(p => game is null || !string.Equals(Path.GetFileNameWithoutExtension(game.Name), p.Name, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.Name} ({p.CpuPercent:0}% of the CPU)")
                .FirstOrDefault();
        }
        else if (cpuLoad < 60)
        {
            _topCpu = null;
        }

        return new LiveReading(
            now,
            m[SessionTimeline.CpuTemp],
            cpuLoad,
            m[SessionTimeline.GpuTemp],
            m[SessionTimeline.GpuLoad],
            m[SessionTimeline.GpuClock],
            game?.Name,
            game?.Fps,
            game is null ? null : Baseline(game.Name, now),
            _memory?.UsedPercent,
            _memory?.TotalGb,
            _topCpu,
            other?.Name);
    }

    /// <summary>The game's usual FPS: median of its saved sessions, once it has at least three.</summary>
    private double? Baseline(string application, DateTimeOffset now)
    {
        if (_baselines.TryGetValue(application, out var cached) && now - cached.At < BaselineInterval)
        {
            return cached.Fps;
        }
        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        var fps = store.GetSessionFps(application, SessionReports.SettingsKey).Order().ToList();
        double? baseline = fps.Count >= 3 ? fps[fps.Count / 2] : null;
        _baselines[application] = (now, baseline);
        return baseline;
    }
}
