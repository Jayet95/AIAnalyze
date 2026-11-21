using System.Text.Json;
using System.Text.Json.Serialization;
using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Storage;

namespace AIAnalyze.Health;

/// <summary>JSON settings shared by the health report store and the API: camelCase, enums as names.</summary>
public static class HealthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>The latest health report, and a way for the UI to ask for a check right now.</summary>
public sealed class HealthState
{
    private readonly SemaphoreSlim _runNow = new(0, 1);

    public HealthReport? Latest { get; set; }
    public bool Running { get; set; }

    /// <summary>Asks the monitor to check now. Extra requests while one is pending are ignored.</summary>
    public void RequestRun()
    {
        if (_runNow.CurrentCount == 0)
        {
            try
            {
                _runNow.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public Task<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _runNow.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Runs the PC health checks every 10 minutes (and on request), stores each report, and raises an alert
/// when a check gets worse, so the same problem does not alert again every run.
/// </summary>
public sealed class HealthMonitorService(ILogger<HealthMonitorService> logger, HealthState state) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var store = new SampleStore(TelemetryPaths.DatabasePath))
        {
            state.Latest = store.GetLatestHealthReport();
        }

        var wait = FirstRunDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await state.WaitForRequestAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            wait = Interval;

            try
            {
                state.Running = true;
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health check failed");
            }
            finally
            {
                state.Running = false;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var snapshot = await HealthCollector.CollectAsync(CpuWindow, stoppingToken);
        var report = HealthAnalyzer.Analyze(snapshot);
        var previous = state.Latest;

        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        store.SaveHealthReport(report);
        state.Latest = report;

        foreach (var check in Worsened(previous, report))
        {
            store.AddAlert(report.AtUtc, $"Health:{check.Id}", check.Severity!.Value, $"{check.Title}: {check.Summary}");
            logger.LogWarning("{Title}: {Summary}", check.Title, check.Summary);
        }
    }

    /// <summary>Checks at Warning or above whose severity is higher than in the previous report.</summary>
    public static IEnumerable<HealthCheckResult> Worsened(HealthReport? previous, HealthReport current)
    {
        foreach (var check in current.Checks.Where(c => c.Severity >= FindingSeverity.Warning))
        {
            var before = previous?.Checks.FirstOrDefault(c => c.Id == check.Id)?.Severity;
            if (before is null || check.Severity > before)
            {
                yield return check;
            }
        }
    }
}
