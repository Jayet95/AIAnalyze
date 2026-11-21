using AIAnalyze.Analysis;

namespace AIAnalyze.Health;

/// <summary>One Windows Event Log entry relevant to PC health.</summary>
public sealed record HealthEvent(DateTimeOffset AtUtc, string Log, string Provider, int EventId, string Level, string Message);

/// <summary>A fixed drive's space.</summary>
public sealed record DriveSpace(string Name, string Label, long TotalBytes, long FreeBytes)
{
    public double FreePercent => TotalBytes <= 0 ? 100 : FreeBytes * 100.0 / TotalBytes;
}

/// <summary>Health of a physical disk as Windows Storage reports it.</summary>
public sealed record PhysicalDiskHealth(string Name, string MediaType, string Health, string OperationalStatus);

/// <summary>Physical memory in use.</summary>
public sealed record MemoryUse(double TotalGb, double UsedGb)
{
    public double UsedPercent => TotalGb <= 0 ? 0 : UsedGb * 100 / TotalGb;
}

/// <summary>A process's CPU share (percent of all cores over the sampling window) and working set.</summary>
public sealed record ProcessUse(string Name, int ProcessId, double CpuPercent, double MemoryMb);

/// <summary>Laptop battery capacity in mWh.</summary>
public sealed record BatteryHealth(int DesignCapacity, int FullChargeCapacity)
{
    public double WearPercent => DesignCapacity <= 0 ? 0 : Math.Max(0, (1 - FullChargeCapacity / (double)DesignCapacity) * 100);
}

/// <summary>Everything collected for one health check run.</summary>
public sealed record HealthSnapshot(
    DateTimeOffset AtUtc,
    TimeSpan Uptime,
    IReadOnlyList<HealthEvent> Events,
    IReadOnlyList<DriveSpace> Drives,
    IReadOnlyList<PhysicalDiskHealth> Disks,
    MemoryUse? Memory,
    IReadOnlyList<ProcessUse> TopByCpu,
    IReadOnlyList<ProcessUse> TopByMemory,
    BatteryHealth? Battery);

/// <summary>
/// The result of one check. Severity null means the check passed. Evidence holds the specific events,
/// drives or programs behind the summary, so the user and the assistant can see why.
/// </summary>
public sealed record HealthCheckResult(
    string Id,
    string Title,
    FindingSeverity? Severity,
    string Summary,
    IReadOnlyList<string> Evidence);

/// <summary>A complete health report: the checks plus the numbers the UI and the assistant show.</summary>
public sealed record HealthReport(
    DateTimeOffset AtUtc,
    TimeSpan Uptime,
    IReadOnlyList<HealthCheckResult> Checks,
    MemoryUse? Memory,
    IReadOnlyList<DriveSpace> Drives,
    IReadOnlyList<ProcessUse> TopByCpu,
    IReadOnlyList<ProcessUse> TopByMemory)
{
    /// <summary>The most severe result, or null when every check passed.</summary>
    public FindingSeverity? Worst => Checks.Where(c => c.Severity is not null).Select(c => c.Severity).Max();
}
