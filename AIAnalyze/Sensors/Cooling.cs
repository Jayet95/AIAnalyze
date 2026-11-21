using AIAnalyze.Analysis;

namespace AIAnalyze.Sensors;

/// <summary>The latest full set of sensor readings, shared from the sampler to the API (not stored).</summary>
public sealed class LatestSensors
{
    private volatile Snapshot? _current;

    public sealed record Snapshot(DateTimeOffset AtUtc, IReadOnlyList<SensorSample> Samples);

    public Snapshot? Current => _current;

    public void Set(DateTimeOffset at, IReadOnlyList<SensorSample> samples) => _current = new Snapshot(at, samples);
}

/// <summary>One fan or pump: speed in RPM and, when the hardware reports it, the duty in percent.</summary>
public sealed record FanReading(string Name, double? Rpm, double? Percent);

/// <summary>A graphics card's fans.</summary>
public sealed record GpuCooling(string Name, int FanCount, IReadOnlyList<FanReading> Fans, bool ZeroRpm);

/// <summary>
/// What the PC reports about its cooling. The cooling type is inferred: no component states it, so a
/// running pump means liquid cooling and fans without a pump most likely mean air.
/// </summary>
public sealed record CoolingReport(
    DateTimeOffset AtUtc,
    IReadOnlyList<GpuCooling> Gpus,
    IReadOnlyList<FanReading> SystemFans,
    IReadOnlyList<FanReading> Pumps,
    string CoolingType,
    string CoolingTypeReason);

public static class CoolingSummary
{
    public const string Liquid = "Liquid";
    public const string Air = "Air";
    public const string Unknown = "Unknown";

    public static CoolingReport Build(DateTimeOffset at, IReadOnlyList<SensorSample> samples)
    {
        var gpus = samples
            .Where(s => DeviceKinds.Of(s.SensorId) == DeviceKind.Gpu && IsFanSensor(s))
            .GroupBy(s => DeviceKinds.DeviceKey(s.SensorId))
            .Select(g =>
            {
                var fans = Fans(g);
                return new GpuCooling(g.First().HardwareName, fans.Count, fans,
                    ZeroRpm: fans.Count > 0 && fans.All(f => f.Rpm is 0));
            })
            .Where(g => g.FanCount > 0)
            .ToList();

        // Motherboard headers and liquid coolers. Headers with nothing connected read 0 RPM and are left out.
        var other = Fans(samples.Where(s => DeviceKinds.Of(s.SensorId) is null && IsFanSensor(s)))
            .Where(f => f.Rpm > 0)
            .ToList();
        var pumps = other.Where(f => f.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)).ToList();
        var systemFans = other.Except(pumps).ToList();

        var (type, reason) = pumps.Count > 0
            ? (Liquid, $"A pump is running at {pumps.Max(p => p.Rpm):0} RPM, so this PC uses liquid cooling (an AIO or a custom loop).")
            : systemFans.Count > 0
                ? (Air, $"{systemFans.Count} fan(s) report their speed and no pump does, so this is most likely air cooling. Some liquid coolers run their pump without reporting it.")
                : (Unknown, "This PC doesn't report its CPU or case fans. Laptops and some motherboards hide them, or the PawnIO driver is missing.");

        return new CoolingReport(at, gpus, systemFans, pumps, type, reason);
    }

    private static bool IsFanSensor(SensorSample s) =>
        s.SensorType == "Fan" || (s.SensorType == "Control" && s.SensorName.Contains("Fan", StringComparison.OrdinalIgnoreCase))
        || (s.SensorType == "Control" && s.SensorName.Contains("Pump", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pairs each fan's RPM ("Fan" sensor) with its duty ("Control" sensor of the same name).</summary>
    private static List<FanReading> Fans(IEnumerable<SensorSample> sensors) =>
        sensors
            .GroupBy(s => s.SensorName)
            .Select(g => new FanReading(
                g.Key,
                g.FirstOrDefault(s => s.SensorType == "Fan")?.Value is float rpm ? Math.Round(rpm) : null,
                g.FirstOrDefault(s => s.SensorType == "Control")?.Value is float pct ? Math.Round(pct) : null))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
