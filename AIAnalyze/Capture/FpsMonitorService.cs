using AIAnalyze.Analysis;
using AIAnalyze.Storage;
using Microsoft.Extensions.Options;

namespace AIAnalyze.Capture;

/// <summary>
/// Runs PresentMon, groups its frames into sessions, stores each session, and compares
/// it with the personal baseline for that game. Does nothing if PresentMon is not configured.
/// </summary>
public sealed class FpsMonitorService(
    ILogger<FpsMonitorService> logger,
    IOptions<PresentMonOptions> options) : BackgroundService
{
    // Settings profile is a placeholder until the app tracks graphics settings per game.
    private const string SettingsKey = "default";

    // A pause longer than this ends the session.
    private static readonly TimeSpan IdleGap = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PresentMonRunner(options.Value);
        if (!runner.IsConfigured)
        {
            logger.LogInformation("FPS capture disabled: PresentMon:ExecutablePath is not set.");
            return;
        }

        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        var lineParser = new PresentMonLineParser();
        var tracker = new SessionTracker(IdleGap);

        try
        {
            await foreach (var line in runner.ReadLinesAsync(stoppingToken))
            {
                if (!lineParser.TryParse(line, out var frame) || frame is null)
                {
                    continue;
                }

                var closed = tracker.AddFrame(frame, DateTimeOffset.UtcNow);
                if (closed is not null)
                {
                    Record(store, closed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FPS capture stopped unexpectedly");
        }

        var finished = tracker.Finish();
        if (finished is not null)
        {
            Record(store, finished);
        }
    }

    private void Record(SampleStore store, CompletedSession session)
    {
        // Compare against earlier sessions only, so the current session does not vote on its own baseline.
        var previous = store.GetSessionFps(session.Application, SettingsKey);
        var baseline = previous.Count == 0
            ? null
            : BaselineRules.BuildBaseline(session.Application, SettingsKey, previous);

        var result = BaselineRules.Evaluate(baseline, session.MedianFps);
        store.InsertSession(session, SettingsKey);

        if (result.Kind == DeviationKind.FpsDrop)
        {
            logger.LogWarning("{Message}", result.Message);
        }
        else
        {
            logger.LogInformation("Session saved. {Message}", result.Message);
        }
    }
}
