namespace AIAnalyze.Analysis;

/// <summary>
/// Baseline for one game with one settings profile. A new driver or hardware change
/// should start a new baseline, so callers pass the settings key in.
/// </summary>
public sealed record Baseline(string Application, string SettingsKey, double MedianFps, int SessionCount);

public enum DeviationKind
{
    None,
    FpsDrop,
    InsufficientBaseline,
}

public sealed record DeviationResult(DeviationKind Kind, double SessionFps, double? BaselineFps, string Message);

public static class BaselineRules
{
    /// <summary>Default drop threshold from the design doc (20% below the personal baseline).</summary>
    public const double DefaultDropThreshold = 0.20;

    /// <summary>Minimum sessions before a baseline is trusted (design doc: first baseline needs repeated sessions).</summary>
    public const int MinSessionsForBaseline = 3;

    /// <summary>
    /// Session FPS is the median of the instantaneous FPS (1000 / frame time), so a few
    /// stutter frames do not skew the result. Returns null when there are no valid frames.
    /// </summary>
    public static double? SessionFps(IEnumerable<FrameRecord> frames)
    {
        var fps = frames
            .Where(f => f.MsBetweenPresents > 0)
            .Select(f => 1000.0 / f.MsBetweenPresents)
            .ToList();
        return fps.Count == 0 ? null : Stats.Median(fps);
    }

    public static Baseline BuildBaseline(string application, string settingsKey, IReadOnlyList<double> sessionFpsValues) =>
        new(application, settingsKey, Stats.Median(sessionFpsValues), sessionFpsValues.Count);

    public static DeviationResult Evaluate(
        Baseline? baseline,
        double sessionFps,
        double dropThreshold = DefaultDropThreshold)
    {
        if (baseline is null || baseline.SessionCount < MinSessionsForBaseline)
        {
            return new DeviationResult(
                DeviationKind.InsufficientBaseline,
                sessionFps,
                baseline?.MedianFps,
                "Not enough sessions yet to compare against a baseline.");
        }

        var floor = baseline.MedianFps * (1 - dropThreshold);
        if (sessionFps < floor)
        {
            var dropPct = (1 - sessionFps / baseline.MedianFps) * 100;
            return new DeviationResult(
                DeviationKind.FpsDrop,
                sessionFps,
                baseline.MedianFps,
                $"{baseline.Application}: median FPS {sessionFps:F0} is {dropPct:F0}% below your baseline of {baseline.MedianFps:F0}.");
        }

        return new DeviationResult(
            DeviationKind.None,
            sessionFps,
            baseline.MedianFps,
            $"{baseline.Application}: median FPS {sessionFps:F0} is within your baseline of {baseline.MedianFps:F0}.");
    }
}
