using System.Management;

namespace AIAnalyze.Sensors;

/// <summary>
/// Reads the PC's hardware and OS identity from WMI. Read-only queries, no admin rights needed.
/// Any single query that fails leaves its fields empty instead of failing the whole collection.
/// </summary>
public static class SystemSpecCollector
{
    public static SystemSpecs Collect()
    {
        var system = FirstRow("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        var os = FirstRow("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
        var cpu = FirstRow("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        var board = FirstRow("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var bios = FirstRow("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");

        // Total installed RAM, summed from the physical modules.
        var ramBytes = Query("SELECT Capacity FROM Win32_PhysicalMemory")
            .Sum(m => ToDouble(m["Capacity"]));

        var gpus = Query("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController")
            .Select(g => new GpuSpec(
                Text(g["Name"]),
                Text(g["DriverVersion"]),
                // AdapterRAM is a 32-bit field in WMI and saturates at 4 GB, so this can under-report large cards.
                ToDouble(g["AdapterRAM"]) / 1_073_741_824d))
            .Where(g => g.Name.Length > 0)
            .ToList();

        return new SystemSpecs(
            ComputerManufacturer: Text(system["Manufacturer"]),
            ComputerModel: Text(system["Model"]),
            OsName: Text(os["Caption"]),
            OsVersion: Text(os["Version"]),
            OsBuild: Text(os["BuildNumber"]),
            CpuName: Text(cpu["Name"]),
            CpuCores: (int)ToDouble(cpu["NumberOfCores"]),
            CpuLogicalProcessors: (int)ToDouble(cpu["NumberOfLogicalProcessors"]),
            CpuMaxClockMhz: (int)ToDouble(cpu["MaxClockSpeed"]),
            MotherboardManufacturer: Text(board["Manufacturer"]),
            MotherboardProduct: Text(board["Product"]),
            BiosVersion: Text(bios["SMBIOSBIOSVersion"]),
            RamTotalGb: ramBytes / 1_073_741_824d,
            Gpus: gpus);
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (ManagementException)
        {
            return [];
        }
    }

    /// <summary>First row of a single-instance query, copied into a dictionary. Missing or failed queries give an empty dictionary.</summary>
    private sealed class Row : Dictionary<string, object?>
    {
        // Missing properties read as null instead of throwing.
        public new object? this[string key] => TryGetValue(key, out var value) ? value : null;
    }

    private static Row FirstRow(string wql)
    {
        var values = new Row();
        var first = Query(wql).FirstOrDefault();
        if (first is not null)
        {
            foreach (var property in first.Properties)
            {
                values.Add(property.Name, property.Value);
            }
        }
        return values;
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? "";

    private static double ToDouble(object? value) =>
        value is null ? 0 : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
}
