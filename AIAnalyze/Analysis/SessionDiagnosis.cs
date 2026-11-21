namespace AIAnalyze.Analysis;

public enum FindingSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>One explained observation about a session, with the numbers that support it.</summary>
public sealed record SessionFinding(string Kind, FindingSeverity Severity, string Message);

/// <summary>
/// Turns a session timeline into plain-language findings: what limited FPS, whether anything ran
/// hot or throttled, and whether frame rate was unstable. Every rule states its evidence, and
/// rules whose data is missing (for example no GPU load sensor) are skipped rather than guessed.
/// </summary>
public static class SessionDiagnosis
{
    /// <summary>Fewer seconds than this is too little to judge.</summary>
    public const int MinSeconds = 10;

    private static readonly int[] CommonCaps = [30, 60, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 280, 360];

    public static IReadOnlyList<SessionFinding> Diagnose(
        IReadOnlyList<TimelineRow> rows,
        ThermalLimits limits,
        DeviationResult? deviation = null)
    {
        var findings = new List<SessionFinding>();
        if (rows.Count < MinSeconds)
        {
            return findings;
        }

        var fps = rows.Select(r => (double)r.Fps).ToList();
        var medianFps = Stats.Median(fps);
        var gpuLoad = Column(rows, SessionTimeline.GpuLoad);
        var cpuLoad = Column(rows, SessionTimeline.CpuLoad);
        var cpuTemp = Column(rows, SessionTimeline.CpuTemp);
        var gpuTemp = Column(rows, SessionTimeline.GpuTemp);

        if (deviation is { Kind: DeviationKind.FpsDrop })
        {
            findings.Add(new SessionFinding("FpsDrop", FindingSeverity.Warning, deviation.Message));
        }

        AddHeat(findings, "CpuHot", "CPU", cpuTemp, limits.CpuWarningC, limits.CpuCriticalC,
            "Check the CPU cooler, its fan curve and the case airflow.");
        AddHeat(findings, "GpuHot", "GPU", gpuTemp, limits.GpuWarningC, limits.GpuCriticalC,
            "Check the GPU fans, dust in the heatsink and the case airflow.");
        AddGpuClockDrop(findings, rows, gpuTemp, limits);
        AddLimiter(findings, fps, medianFps, gpuLoad, cpuLoad);
        AddStutter(findings, fps, medianFps);

        return findings;
    }

    private static void AddHeat(
        List<SessionFinding> findings,
        string kind,
        string device,
        IReadOnlyList<double> temps,
        float warning,
        float critical,
        string advice)
    {
        if (temps.Count == 0)
        {
            return;
        }

        var max = temps.Max();
        if (max < warning)
        {
            return;
        }

        var share = temps.Count(t => t >= warning) / (double)temps.Count;
        var severity = max >= critical ? FindingSeverity.Critical : FindingSeverity.Warning;
        findings.Add(new SessionFinding(kind, severity,
            $"{device} reached {max:F0} °C and was at or above {warning:F0} °C for {share:P0} of the session. {advice}"));
    }

    /// <summary>
    /// A GPU that is fully loaded but whose clock falls well below its own high point is being held
    /// back. When it is also hot, that is thermal throttling; otherwise it is usually the power limit.
    /// </summary>
    private static void AddGpuClockDrop(
        List<SessionFinding> findings,
        IReadOnlyList<TimelineRow> rows,
        IReadOnlyList<double> gpuTemp,
        ThermalLimits limits)
    {
        var busyClocks = rows
            .Where(r => r.Metrics[SessionTimeline.GpuLoad] >= 90 && r.Metrics[SessionTimeline.GpuClock] is not null)
            .Select(r => r.Metrics[SessionTimeline.GpuClock]!.Value)
            .ToList();
        if (busyClocks.Count < MinSeconds)
        {
            return;
        }

        var high = Percentile(busyClocks, 0.9);
        var low = Percentile(busyClocks, 0.1);
        if (high <= 0 || low >= high * 0.85)
        {
            return;
        }

        var maxTemp = gpuTemp.Count > 0 ? gpuTemp.Max() : (double?)null;
        if (maxTemp >= limits.GpuWarningC - 5)
        {
            findings.Add(new SessionFinding("GpuThrottling", FindingSeverity.Warning,
                $"While the GPU was fully loaded, its clock dropped from {high:F0} to {low:F0} MHz at up to {maxTemp:F0} °C. " +
                "That pattern is thermal throttling: better GPU cooling or airflow should keep the clock up."));
        }
        else
        {
            findings.Add(new SessionFinding("GpuClockVaries", FindingSeverity.Info,
                $"While the GPU was fully loaded, its clock varied from {high:F0} to {low:F0} MHz with normal temperatures. " +
                "That is usually the card reaching its power limit, which is normal behaviour."));
        }
    }

    /// <summary>Says what most likely limited FPS: a frame cap, the GPU, or something other than the GPU.</summary>
    private static void AddLimiter(
        List<SessionFinding> findings,
        IReadOnlyList<double> fps,
        double medianFps,
        IReadOnlyList<double> gpuLoad,
        IReadOnlyList<double> cpuLoad)
    {
        var gpuMedian = gpuLoad.Count > 0 ? Stats.Median(gpuLoad) : (double?)null;
        var p10 = Percentile(fps, 0.1);
        var p90 = Percentile(fps, 0.9);

        // A steady rate at a common value only means a cap if the GPU had room to go faster;
        // without GPU load data, a GPU-bound game at a round number would look the same.
        var cap = CommonCaps.FirstOrDefault(c => Math.Abs(medianFps - c) <= c * 0.02);
        var steady = p10 >= medianFps * 0.9;
        if (cap > 0 && steady && gpuMedian is not null && gpuMedian < 90)
        {
            findings.Add(new SessionFinding("FpsCapped", FindingSeverity.Info,
                $"FPS stayed at about {cap} (most seconds between {p10:F0} and {p90:F0}), which matches a frame cap: " +
                "V-Sync, an in-game limiter, or the monitor's refresh rate. The hardware was not the limit."));
            return;
        }

        if (gpuMedian is null)
        {
            return;
        }

        if (gpuMedian >= 95)
        {
            findings.Add(new SessionFinding("GpuBound", FindingSeverity.Info,
                $"The GPU was {gpuMedian:F0}% busy (median), so the graphics card set the frame rate. " +
                "Lower resolution, upscaling (DLSS or FSR) or lower graphics settings will raise FPS; a faster CPU will not."));
        }
        else if (gpuMedian < 75)
        {
            var cpuClause = cpuLoad.Count > 0
                ? $" (one busy core can hold a game back even when total CPU load is {Stats.Median(cpuLoad):F0}%)"
                : "";
            findings.Add(new SessionFinding("NotGpuBound", FindingSeverity.Info,
                $"The GPU was only {gpuMedian:F0}% busy (median), so something else held FPS back: usually the CPU{cpuClause}, " +
                "background programs, or the game engine itself. Higher graphics settings would cost little FPS here."));
        }
    }

    private static void AddStutter(List<SessionFinding> findings, IReadOnlyList<double> fps, double medianFps)
    {
        if (medianFps < 20)
        {
            return;
        }

        var slow = fps.Count(f => f < medianFps * 0.5);
        var share = slow / (double)fps.Count;
        if (share < 0.05)
        {
            return;
        }

        findings.Add(new SessionFinding("Stutter", FindingSeverity.Warning,
            $"{share:P0} of seconds fell below half the median FPS ({medianFps:F0}). Drops this frequent feel like stutter; " +
            "common causes are shader compilation, background disk or CPU activity, and running low on memory."));
    }

    private static IReadOnlyList<double> Column(IReadOnlyList<TimelineRow> rows, string label) =>
        rows.Select(r => r.Metrics[label]).Where(v => v is not null).Select(v => v!.Value).ToList();

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }
}
