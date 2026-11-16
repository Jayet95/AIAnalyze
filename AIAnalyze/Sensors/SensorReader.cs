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
/// Only temperature and load sensors are collected in this first phase.
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

    private static void CollectFrom(IHardware hardware, List<SensorSample> samples)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType is not (SensorType.Temperature or SensorType.Load))
            {
                continue;
            }
            samples.Add(new SensorSample(
                SensorId: sensor.Identifier.ToString(),
                HardwareName: hardware.Name,
                SensorName: sensor.Name,
                SensorType: sensor.SensorType.ToString(),
                Value: sensor.Value));
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectFrom(sub, samples);
        }
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
