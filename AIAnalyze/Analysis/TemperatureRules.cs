using AIAnalyze.Sensors;

namespace AIAnalyze.Analysis;

public enum TemperatureSeverity
{
    Ok,
    Warning,
    Critical,
}

/// <summary>
/// Hard limits in °C. Defaults are conservative starting points, not manufacturer specs;
/// they are meant to be overridden from configuration.
/// </summary>
public sealed record ThermalLimits(
    float CpuWarningC = 85f,
    float CpuCriticalC = 95f,
    float GpuWarningC = 83f,
    float GpuCriticalC = 93f);

public sealed record TemperatureFinding(
    string SensorId,
    string SensorName,
    float Value,
    TemperatureSeverity Severity,
    string Message);

public static class TemperatureRules
{
    /// <summary>
    /// Checks every temperature sample against the hard limits for its device kind.
    /// Samples with no value or of another sensor type are ignored. Returns only findings above Ok.
    /// </summary>
    public static IReadOnlyList<TemperatureFinding> Evaluate(IEnumerable<SensorSample> samples, ThermalLimits limits)
    {
        var findings = new List<TemperatureFinding>();

        foreach (var sample in samples)
        {
            if (sample.SensorType != "Temperature" || sample.Value is not float value)
            {
                continue;
            }

            var kind = KindOf(sample.SensorId);
            if (kind is null)
            {
                continue;
            }

            var (warning, critical) = kind == DeviceKind.Cpu
                ? (limits.CpuWarningC, limits.CpuCriticalC)
                : (limits.GpuWarningC, limits.GpuCriticalC);

            var severity = value >= critical ? TemperatureSeverity.Critical
                : value >= warning ? TemperatureSeverity.Warning
                : TemperatureSeverity.Ok;

            if (severity == TemperatureSeverity.Ok)
            {
                continue;
            }

            var threshold = severity == TemperatureSeverity.Critical ? critical : warning;
            findings.Add(new TemperatureFinding(
                sample.SensorId,
                $"{sample.HardwareName} {sample.SensorName}",
                value,
                severity,
                $"{sample.HardwareName} {sample.SensorName} is {value:0.0} °C, at or above the {severity.ToString().ToLowerInvariant()} limit of {threshold:0} °C."));
        }

        return findings;
    }

    private enum DeviceKind
    {
        Cpu,
        Gpu,
    }

    // LibreHardwareMonitor identifiers start with the device kind, e.g. "/amdcpu/0/temperature/2".
    private static DeviceKind? KindOf(string sensorId)
    {
        if (sensorId.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase)
            || sensorId.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase)
            || sensorId.StartsWith("/cpu", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.Cpu;
        }

        if (sensorId.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.Gpu;
        }

        return null;
    }
}
