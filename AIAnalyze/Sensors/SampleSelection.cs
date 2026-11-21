using AIAnalyze.Analysis;

namespace AIAnalyze.Sensors;

/// <summary>
/// Decides which sensors are stored on each tick. Storing every sensor every second wrote about
/// 136 rows per second (1.4 GB per day). Temperatures and the sensors the timeline and findings use
/// are stored every second; every sensor is stored every <see cref="FullEvery"/> seconds.
/// </summary>
public static class SampleSelection
{
    public const int FullEvery = 10;

    private static readonly HashSet<string> KeyNames = new(SessionTimeline.SensorNames, StringComparer.Ordinal);

    public static bool IsKey(SensorSample sample) =>
        sample.SensorType == "Temperature" || KeyNames.Contains(sample.SensorName);

    public static IReadOnlyList<SensorSample> ForTick(IReadOnlyList<SensorSample> samples, long tick) =>
        tick % FullEvery == 0 ? samples : samples.Where(IsKey).ToList();
}
