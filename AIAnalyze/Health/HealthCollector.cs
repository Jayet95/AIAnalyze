using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;

namespace AIAnalyze.Health;

/// <summary>
/// Reads what the health checks need from Windows: the Event Log, drives, physical disk health, memory,
/// the busiest processes and the battery. Each part fails on its own: a part that can't be read is left
/// empty instead of stopping the whole collection.
/// </summary>
public static class HealthCollector
{
    public static readonly TimeSpan EventWindow = TimeSpan.FromDays(7);
    private const int MaxEventsPerLog = 500;

    public static async Task<HealthSnapshot> CollectAsync(TimeSpan cpuWindow, CancellationToken cancellationToken)
    {
        var (byCpu, byMemory) = await SampleProcessesAsync(cpuWindow, cancellationToken);
        return new HealthSnapshot(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            ReadEvents(),
            ReadDrives(),
            ReadPhysicalDisks(),
            ReadMemory(),
            byCpu,
            byMemory,
            ReadBattery());
    }

    /// <summary>Events from the watched providers in the last 7 days, newest first.</summary>
    public static IReadOnlyList<HealthEvent> ReadEvents()
    {
        var events = new List<HealthEvent>();
        foreach (var (log, providers) in HealthAnalyzer.WatchedProviders)
        {
            var providerFilter = string.Join(" or ", providers.Select(p => $"@Name='{p}'"));
            var query = $"*[System[Provider[{providerFilter}] and TimeCreated[timediff(@SystemTime) <= {(long)EventWindow.TotalMilliseconds}]]]";
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
                for (var i = 0; i < MaxEventsPerLog; i++)
                {
                    using var record = reader.ReadEvent();
                    if (record is null)
                    {
                        break;
                    }
                    events.Add(new HealthEvent(
                        record.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow,
                        log,
                        record.ProviderName ?? "",
                        record.Id,
                        LevelName(record.Level),
                        SafeDescription(record)));
                }
            }
            catch (EventLogException)
            {
                // Log missing or not readable; leave it out.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return events;
    }

    public static IReadOnlyList<DriveSpace> ReadDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new DriveSpace(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static IReadOnlyList<PhysicalDiskHealth> ReadPhysicalDisks()
    {
        var disks = new List<PhysicalDiskHealth>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk");
            foreach (var disk in searcher.Get().Cast<ManagementBaseObject>())
            {
                disks.Add(new PhysicalDiskHealth(
                    disk["FriendlyName"]?.ToString()?.Trim() ?? "Disk",
                    Convert.ToInt32(disk["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "drive" },
                    Convert.ToInt32(disk["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    disk["OperationalStatus"] is ushort[] status && status.Length > 0
                        ? string.Join(", ", status.Select(OperationalStatusName))
                        : "Unknown"));
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return disks;
    }

    public static MemoryUse? ReadMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var os in searcher.Get().Cast<ManagementBaseObject>())
            {
                var totalKb = Convert.ToDouble(os["TotalVisibleMemorySize"]);
                var freeKb = Convert.ToDouble(os["FreePhysicalMemory"]);
                return new MemoryUse(totalKb / 1_048_576, (totalKb - freeKb) / 1_048_576);
            }
        }
        catch (ManagementException)
        {
        }
        return null;
    }

    public static BatteryHealth? ReadBattery()
    {
        try
        {
            var design = FirstInt(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
            var full = FirstInt(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            return design > 0 && full > 0 ? new BatteryHealth(design, full) : null;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    /// <summary>
    /// CPU share of each process over <paramref name="window"/> (percent of all cores) and its working set.
    /// Processes the service may not inspect, and the idle process, are skipped.
    /// </summary>
    public static async Task<(IReadOnlyList<ProcessUse> ByCpu, IReadOnlyList<ProcessUse> ByMemory)> SampleProcessesAsync(
        TimeSpan window, CancellationToken cancellationToken)
    {
        var before = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (TryCpu(p, out var cpu))
                {
                    before[p.Id] = (p.ProcessName, cpu);
                }
            }
        }

        await Task.Delay(window, cancellationToken);

        var uses = new List<ProcessUse>();
        var cores = Environment.ProcessorCount;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0 || !before.TryGetValue(p.Id, out var start) || !TryCpu(p, out var cpu))
                {
                    continue;
                }
                double memoryMb;
                try
                {
                    memoryMb = p.WorkingSet64 / 1_048_576.0;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                var percent = (cpu - start.Cpu).TotalMilliseconds / (window.TotalMilliseconds * cores) * 100;
                uses.Add(new ProcessUse(start.Name, p.Id, Math.Max(0, percent), memoryMb));
            }
        }

        // Several processes can share a name (browsers); add them up so "chrome" shows its real total.
        var grouped = uses
            .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUse(g.Key, g.First().ProcessId, g.Sum(u => u.CpuPercent), g.Sum(u => u.MemoryMb)))
            .ToList();

        return (
            grouped.OrderByDescending(u => u.CpuPercent).Take(8).ToList(),
            grouped.OrderByDescending(u => u.MemoryMb).Take(8).ToList());
    }

    private static bool TryCpu(Process p, out TimeSpan cpu)
    {
        try
        {
            cpu = p.TotalProcessorTime;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            cpu = default;
            return false;
        }
    }

    private static int FirstInt(string scope, string query, string property)
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        foreach (var row in searcher.Get().Cast<ManagementBaseObject>())
        {
            return Convert.ToInt32(row[property] ?? 0);
        }
        return 0;
    }

    private static string SafeDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? $"Event {record.Id}";
        }
        catch (EventLogException)
        {
            return $"Event {record.Id}";
        }
    }

    private static string LevelName(byte? level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        _ => "Information",
    };

    private static string OperationalStatusName(ushort code) => code switch
    {
        2 => "OK",
        3 => "Degraded",
        5 => "Predictive failure",
        6 => "Error",
        10 => "Stopped",
        53264 => "Lost communication",
        _ => $"status {code}",
    };
}
