namespace AIAnalyze.Analysis;

public enum DeviceKind
{
    Cpu,
    Gpu,
}

public static class DeviceKinds
{
    /// <summary>
    /// LibreHardwareMonitor identifiers start with the device kind, e.g. "/amdcpu/0/temperature/2".
    /// Returns null for devices this app does not classify.
    /// </summary>
    public static DeviceKind? Of(string sensorId)
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
