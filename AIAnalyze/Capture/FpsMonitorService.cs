using AIAnalyze.Analysis;
using AIAnalyze.Storage;
using Microsoft.Extensions.Options;

namespace AIAnalyze.Capture;

/// <summary>
/// Runs PresentMon, groups its frames into sessions per application, stores each session, and
/// compares it with the personal baseline for that game. Does nothing if PresentMon is not configured.
/// If PresentMon fails or exits, it is started again after a delay.
/// </summary>
public sealed class FpsMonitorService(
    ILogger<FpsMonitorService> logger,
    IOptions<PresentMonOptions> options) : BackgroundService
{
    // Settings profile is a placeholder until the app tracks graphics settings per game.
    private const string SettingsKey = "default";

    // An application silent for longer than this ends its session.
    private static readonly TimeSpan IdleGap = TimeSpan.FromSeconds(30);

    // Shorter sessions are noise (a window flashing, a quick alt-tab) and are not saved.
    private const int MinFrames = 300;

    // Compositor and system presenters that are never a game.
    private static readonly string[] ExcludedApps = ["dwm.exe"];

    // Wait before restarting PresentMon, so a broken setup does not spin.
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PresentMonRunner(options.Value);
        if (!runner.IsConfigured)
        {
            logger.LogInformation("FPS capture disabled: PresentMon:ExecutablePath is not set.");
            return;
        }

        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        var tracker = new SessionTracker(IdleGap, MinFrames, ExcludedApps);

        // The tracker and the store are shared by the frame reader and the idle timer.
        var gate = new object();
        var idleTask = CloseIdleSessionsAsync(tracker, store, gate, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var lineParser = new PresentMonLineParser();
                var loggedPreHeaderLine = false;

                try
                {
                    await foreach (var line in runner.ReadLinesAsync(
                        stoppingToken,
                        err => logger.LogWarning("PresentMon: {Line}", err)))
                    {
                        if (!lineParser.TryParse(line, out var frame) || frame is null)
                        {
                            // Show the first line before the header once, so unexpected PresentMon output can be diagnosed.
                            if (!lineParser.HeaderFound && !loggedPreHeaderLine && line.Trim().Length > 0)
                            {
                                logger.LogWarning("PresentMon output before header (skipped): {Line}", line.Trim());
                                loggedPreHeaderLine = true;
                            }
                            continue;
                        }

                        lock (gate)
                        {
                            foreach (var closed in tracker.AddFrame(frame, DateTimeOffset.UtcNow))
                            {
                                Record(store, closed);
                            }
                        }
                    }

                    logger.LogWarning("PresentMon exited. Restarting in {Delay}.", RestartDelay);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "FPS capture stopped unexpectedly. Restarting in {Delay}.", RestartDelay);
                }

                // Save what was captured before the stop; the next run starts new sessions.
                lock (gate)
                {
                    foreach (var interrupted in tracker.Finish())
                    {
                        Record(store, interrupted);
                    }
                }

                try
                {
                    await Task.Delay(RestartDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                await idleTask;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }

            lock (gate)
            {
                foreach (var finished in tracker.Finish())
                {
                    Record(store, finished);
                }
            }
        }
    }

    /// <summary>
    /// Ends sessions whose application has gone quiet. Needed because a game that exits sends
    /// no more frames, so nothing else would close its session.
    /// </summary>
    private async Task CloseIdleSessionsAsync(
        SessionTracker tracker,
        SampleStore store,
        object gate,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                lock (gate)
                {
                    foreach (var closed in tracker.CloseIdle(DateTimeOffset.UtcNow))
                    {
                        Record(store, closed);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Closing idle FPS sessions failed");
            }
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
