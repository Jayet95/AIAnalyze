using LibreHardwareMonitor.Hardware;

namespace AIAnalyze.Sensors;

/// <summary>
/// One reading from one sensor. Value is null when the sensor is present but
/// could not be read; it is never replaced with 0 (see design doc: "not available" != 0).
/// </summary>
public sealed record SensorSample(
    string SensorId,
    string HardwareName,
    string SensorName,
    string SensorType,
    float? Value);

/// <summary>
/// Reads CPU and GPU sensors through LibreHardwareMonitorLib.
/// Stores every sensor on those devices. Needs administrator rights for full CPU telemetry.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    public SensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
        };
        _computer.Open();
    }

    public IReadOnlyList<SensorSample> ReadOnce()
    {
        _computer.Accept(_visitor);

        var samples = new List<SensorSample>();
        foreach (var hardware in _computer.Hardware)
        {
            if (!IsTrackedHardware(hardware.HardwareType))
            {
                continue;
            }
            CollectFrom(hardware, samples);
        }
        return samples;
    }

    /// <summary>
    /// Diagnostic dump: every sensor the library sees on every hardware item, with no filtering.
    /// Used by --dump-sensors to find out which sensor names this machine actually exposes.
    /// </summary>
    public IReadOnlyList<string> DumpAll()
    {
        _computer.Accept(_visitor);
        var lines = new List<string>();
        foreach (var hardware in _computer.Hardware)
        {
            DumpFrom(hardware, "", lines);
        }
        return lines;
    }

    private static void DumpFrom(IHardware hardware, string indent, List<string> lines)
    {
        lines.Add($"{indent}[{hardware.HardwareType}] {hardware.Name}");
        foreach (var sensor in hardware.Sensors)
        {
            lines.Add($"{indent}  {sensor.SensorType,-12} {sensor.Name,-40} {(sensor.Value.HasValue ? sensor.Value.Value.ToString("F1") : "None")}");
        }
        foreach (var sub in hardware.SubHardware)
        {
            DumpFrom(sub, indent + "  ", lines);
        }
    }

    private static void CollectFrom(IHardware hardware, List<SensorSample> samples)
    {
        foreach (var sensor in hardware.Sensors)
        {
            // Every sensor the library reports is stored (temps, loads, clocks, power, voltages, fans, ...).
            samples.Add(new SensorSample(
                SensorId: sensor.Identifier.ToString(),
                HardwareName: hardware.Name,
                SensorName: sensor.Name,
                SensorType: sensor.SensorType.ToString(),
                Value: ToReadingOrNull(sensor)));
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectFrom(sub, samples);
        }
    }

    /// <summary>
    /// A running chip never reports exactly 0 °C, so a 0 temperature means the sensor
    /// was not actually read (seen on AMD CPUs without elevated rights). Store it as unreadable.
    /// </summary>
    private static float? ToReadingOrNull(ISensor sensor)
    {
        if (sensor.Value is null)
        {
            return null;
        }
        if (sensor.SensorType == SensorType.Temperature && sensor.Value.Value == 0f)
        {
            return null;
        }
        return sensor.Value;
    }

    private static bool IsTrackedHardware(HardwareType type) =>
        type is HardwareType.Cpu
            or HardwareType.GpuNvidia
            or HardwareType.GpuAmd
            or HardwareType.GpuIntel;

    public void Dispose() => _computer.Close();

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
