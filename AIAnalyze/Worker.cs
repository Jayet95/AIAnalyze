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
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIAnalyze",
            "telemetry.db");

        using var reader = new SensorReader();
        using var store = new SampleStore(dbPath);
        logger.LogInformation("Sampling every {Interval} into {Db}", SampleInterval, dbPath);

        var nextCleanup = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(SampleInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var samples = reader.ReadOnce();
                store.WriteTick(now, samples);

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
}
