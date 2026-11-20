using AIAnalyze.Sensors;
using AIAnalyze.Storage;

namespace AIAnalyze;

/// <summary>
/// Samples CPU/GPU sensors once per second and stores them locally.
/// Phase 0: collection only, no diagnosis yet.
/// </summary>
public class Worker(ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ThermalLogCooldown = TimeSpan.FromMinutes(5);
    private static readonly Analysis.ThermalLimits ThermalLimits = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var reader = new SensorReader();
        using var store = new SampleStore(Capture.TelemetryPaths.DatabasePath);
        logger.LogInformation("Sampling every {Interval} into {Db}", SampleInterval, Capture.TelemetryPaths.DatabasePath);

        try
        {
            var specs = SystemSpecCollector.Collect();
            var changed = store.SaveSpecsIfChanged(DateTimeOffset.UtcNow, specs);
            logger.LogInformation("System: {Summary}{Changed}", specs.Summary, changed ? " (new snapshot saved)" : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read system specs");
        }

        var nextCleanup = DateTimeOffset.UtcNow;
        var lastLogged = new Dictionary<string, DateTimeOffset>();
        using var timer = new PeriodicTimer(SampleInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var samples = reader.ReadOnce();
                store.WriteTick(now, samples);
                LogThermalFindings(samples, now, lastLogged);

                if (now >= nextCleanup)
                {
                    var removed = store.DeleteOlderThan(Retention);
                    logger.LogDebug("Retention cleanup removed {Count} rows", removed);
                    nextCleanup = now.Add(CleanupInterval);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep sampling even if one tick fails (e.g. a sensor driver hiccup).
                logger.LogWarning(ex, "Sampling tick failed");
            }
        }
    }

    /// <summary>
    /// Logs temperature findings, at most once per sensor every <see cref="ThermalLogCooldown"/>,
    /// so a hot CPU does not flood the log every second.
    /// </summary>
    private void LogThermalFindings(
        IReadOnlyList<Sensors.SensorSample> samples,
        DateTimeOffset now,
        Dictionary<string, DateTimeOffset> lastLogged)
    {
        foreach (var finding in Analysis.TemperatureRules.Evaluate(samples, ThermalLimits))
        {
            if (lastLogged.TryGetValue(finding.SensorId, out var last) && now - last < ThermalLogCooldown)
            {
                continue;
            }

            lastLogged[finding.SensorId] = now;
            if (finding.Severity == Analysis.TemperatureSeverity.Critical)
            {
                logger.LogError("{Message}", finding.Message);
            }
            else
            {
                logger.LogWarning("{Message}", finding.Message);
            }
        }
    }
}
