using AIAnalyze.Analysis;
using AIAnalyze.Reports;
using AIAnalyze.Storage;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace AIAnalyze.Capture;

/// <summary>
/// Runs PresentMon, groups its frames into sessions per application, stores each session with its
/// findings, and logs what it found. Does nothing if PresentMon is not available.
/// If PresentMon fails or exits, it is started again after a delay.
/// </summary>
public sealed class FpsMonitorService(
    ILogger<FpsMonitorService> logger,
    IOptions<PresentMonOptions> options,
    CaptureStatus status,
    Chat.ClaudeChat chat) : BackgroundService
{
    // Automatic AI feedback spends the user's API credits, so it is rate-limited.
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastFeedback = DateTimeOffset.MinValue;

    // An application silent for longer than this ends its session.
    private static readonly TimeSpan IdleGap = TimeSpan.FromSeconds(30);

    // Shorter sessions are noise (a window flashing, a quick alt-tab) and are not saved.
    private const int MinFrames = 300;

    // Compositor and system presenters that are never a game.
    private static readonly string[] ExcludedApps = ["dwm.exe"];

    // Wait before restarting PresentMon, so a broken setup does not spin.
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(30);

    // How often to check for a logged-on user while waiting for one.
    private static readonly TimeSpan UserPollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Resolved(WindowsServiceHelpers.IsWindowsService());
        status.SetMode(settings.UserSession);

        var runner = new PresentMonRunner(settings);
        if (!runner.IsConfigured)
        {
            status.Set(CaptureState.Disabled, "PresentMon was not found. Put PresentMon.exe in the PresentMon folder next to AIAnalyze, or set PresentMon:ExecutablePath.");
            logger.LogWarning("FPS capture disabled: PresentMon was not found and PresentMon:ExecutablePath is not set.");
            return;
        }

        var etwSession = EtwSessions.SessionNameFrom(settings.Arguments);
        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        var tracker = new SessionTracker(IdleGap, MinFrames, ExcludedApps);

        // The tracker and the store are shared by the frame reader and the idle timer.
        var gate = new object();
        var idleTask = CloseIdleSessionsAsync(tracker, store, gate, stoppingToken);
        var loggedWaiting = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // At boot, or at the sign-in screen, nobody is logged on: wait quietly instead of failing.
                if (settings.UserSession && !NativeSession.TryGetLoggedOnConsoleSession(out _))
                {
                    if (!loggedWaiting)
                    {
                        logger.LogWarning("Waiting for a user to sign in before starting FPS capture.");
                        status.Set(CaptureState.WaitingForUser, "Waiting for a user to sign in.");
                        loggedWaiting = true;
                    }
                    if (!await DelayAsync(UserPollInterval, stoppingToken))
                    {
                        break;
                    }
                    continue;
                }
                loggedWaiting = false;

                if (EtwSessions.TryStop(etwSession))
                {
                    logger.LogInformation("Stopped a leftover ETW trace session named {Session}.", etwSession);
                }

                var lineParser = new PresentMonLineParser();
                var loggedPreHeaderLine = false;
                status.Set(CaptureState.Running, "Capturing frames.");

                try
                {
                    var lines = settings.UserSession
                        ? new UserSessionFrameSource(settings).ReadLinesAsync(
                            stoppingToken,
                            msg => logger.LogInformation("{Message}", msg),
                            err => logger.LogWarning("PresentMon: {Line}", err))
                        : runner.ReadLinesAsync(stoppingToken, err => logger.LogWarning("PresentMon: {Line}", err));

                    await foreach (var line in lines)
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

                        var now = DateTimeOffset.UtcNow;
                        status.FrameSeen(frame.Application, now);
                        lock (gate)
                        {
                            foreach (var closed in tracker.AddFrame(frame, now))
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

                status.Set(CaptureState.Restarting, $"PresentMon stopped; restarting in {RestartDelay.TotalSeconds:F0} seconds.");
                EtwSessions.TryStop(etwSession);

                // Save what was captured before the stop; the next run starts new sessions.
                lock (gate)
                {
                    foreach (var interrupted in tracker.Finish())
                    {
                        Record(store, interrupted);
                    }
                }

                if (!await DelayAsync(RestartDelay, stoppingToken))
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
            EtwSessions.TryStop(etwSession);
        }
    }

    /// <summary>Waits, returning false when the service is stopping.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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

    /// <summary>Saves a session, analyses it, stores the findings and logs them.</summary>
    private void Record(SampleStore store, CompletedSession session)
    {
        var id = store.InsertSession(session, SessionReports.SettingsKey);
        status.SessionSaved(DateTimeOffset.UtcNow);

        var stored = store.GetSession(id);
        if (stored is null)
        {
            return;
        }

        var report = SessionReports.Build(store, stored);
        store.SaveFindings(id, report.Findings);

        logger.LogInformation("Session {Id} saved: {App}, {Frames} frames, median {Fps:F0} FPS. {Baseline}",
            id, session.Application, session.FrameCount, session.MedianFps, report.Deviation.Message);
        foreach (var finding in report.Findings)
        {
            if (finding.Severity == FindingSeverity.Info)
            {
                logger.LogInformation("Session {Id}: {Message}", id, finding.Message);
            }
            else
            {
                logger.LogWarning("Session {Id}: {Message}", id, finding.Message);
                store.AddAlert(DateTimeOffset.UtcNow, finding.Kind, finding.Severity,
                    $"{session.Application}: {finding.Message}", id);
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (chat.AutoFeedbackEnabled
            && report.Findings.Any(f => f.Severity != FindingSeverity.Info)
            && now - _lastFeedback >= FeedbackInterval)
        {
            _lastFeedback = now;
            _ = Task.Run(() => WriteFeedbackAsync(id, session.Application));
        }
    }

    /// <summary>
    /// Asks the assistant for short feedback on a session that ended with warnings, stores it with the
    /// session and raises an alert so the user sees it. Runs off the capture loop; failures are only logged.
    /// </summary>
    private async Task WriteFeedbackAsync(long sessionId, string application)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            var briefing = Chat.DiagnosticContext.Build(store, status, sessionId, DateTimeOffset.UtcNow);
            var feedback = await chat.AnswerAsync(chat.FeedbackQuestion, briefing, timeout.Token);
            if (feedback.Length == 0)
            {
                return;
            }

            store.SaveFeedback(sessionId, feedback, DateTimeOffset.UtcNow);
            store.AddAlert(DateTimeOffset.UtcNow, "Feedback", FindingSeverity.Info,
                $"{application}: the assistant reviewed this session. Open it on the Sessions tab.", sessionId);
            logger.LogInformation("AI feedback saved for session {Id}.", sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not get AI feedback for session {Id}.", sessionId);
        }
    }
}
