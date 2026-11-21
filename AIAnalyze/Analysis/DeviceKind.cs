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

    /// <summary>
    /// The device part of a sensor identifier: "/gpu-nvidia/0/load/0" gives "/gpu-nvidia/0".
    /// Two GPUs of different vendors, or two of the same vendor, get different keys.
    /// </summary>
    public static string DeviceKey(string sensorId)
    {
        var parts = sensorId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"/{parts[0]}/{parts[1]}" : sensorId;
    }
}
