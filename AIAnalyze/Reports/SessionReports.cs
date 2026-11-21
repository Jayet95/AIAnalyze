using AIAnalyze.Analysis;
using AIAnalyze.Storage;

namespace AIAnalyze.Reports;

/// <summary>Everything known about one session: its timeline, the GPU shown, the baseline check and the findings.</summary>
public sealed record SessionReport(
    StoredSession Session,
    SelectedGpu? Gpu,
    IReadOnlyList<TimelineRow> Rows,
    DeviationResult Deviation,
    IReadOnlyList<SessionFinding> Findings);

/// <summary>
/// Builds a <see cref="SessionReport"/> from the database. The service, the command line and the web UI
/// all use this, so they always show the same analysis for a session.
/// </summary>
public static class SessionReports
{
    // Settings profile is a placeholder until the app tracks graphics settings per game.
    public const string SettingsKey = "default";

    public static SessionReport Build(SampleStore store, StoredSession session, ThermalLimits? limits = null)
    {
        var seconds = store.GetSessionSeconds(session.Id);
        var readings = store.GetReadings(
            session.StartUtc.AddSeconds(-1), session.EndUtc.AddSeconds(1), SessionTimeline.SensorNames);

        var gpu = SessionTimeline.SelectGpu(readings);
        var rows = SessionTimeline.Build(seconds, readings);

        // Baseline from earlier sessions only, so a session does not vote on its own baseline.
        var previous = store.GetSessionFpsBefore(session.Application, SettingsKey, session.StartUtc);
        var baseline = previous.Count == 0 ? null : BaselineRules.BuildBaseline(session.Application, SettingsKey, previous);
        var deviation = BaselineRules.Evaluate(baseline, session.MedianFps);

        var findings = SessionDiagnosis.Diagnose(rows, limits ?? new ThermalLimits(), deviation)
            .OrderByDescending(f => f.Severity)
            .ToList();

        return new SessionReport(session, gpu, rows, deviation, findings);
    }
}
