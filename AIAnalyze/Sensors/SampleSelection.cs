using AIAnalyze.Analysis;

namespace AIAnalyze.Sensors;

/// <summary>
/// Decides which sensor readings are stored. Only the sensors the timeline, findings, live view and
/// assistant read are kept (CPU temperature, load and power; GPU temperature, load, clock and memory size).
/// Hundreds of other readings (per-core clocks, voltages) are used live for temperature warnings but never
/// read back, so they are not written. Storing everything wrote about 136 rows per second (1.4 GB per day).
/// </summary>
public static class SampleSelection
{
    private static readonly HashSet<string> KeyNames = new(SessionTimeline.SensorNames, StringComparer.Ordinal);

    public static bool IsKept(SensorSample sample) => KeyNames.Contains(sample.SensorName);

    public static IReadOnlyList<SensorSample> ToStore(IReadOnlyList<SensorSample> samples) =>
        samples.Where(IsKept).ToList();
}
