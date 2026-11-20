using System.Text.Json;

namespace AIAnalyze.Sensors;

/// <summary>
/// Hardware and software identity of this PC. Deliberately excludes serial numbers,
/// user names and the machine name so a stored snapshot identifies the hardware, not the person.
/// </summary>
public sealed record SystemSpecs(
    string ComputerManufacturer,
    string ComputerModel,
    string OsName,
    string OsVersion,
    string OsBuild,
    string CpuName,
    int CpuCores,
    int CpuLogicalProcessors,
    int CpuMaxClockMhz,
    string MotherboardManufacturer,
    string MotherboardProduct,
    string BiosVersion,
    double RamTotalGb,
    IReadOnlyList<GpuSpec> Gpus)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static SystemSpecs? FromJson(string json) => JsonSerializer.Deserialize<SystemSpecs>(json);

    /// <summary>One-line description for the log.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary =>
        $"{ComputerManufacturer} {ComputerModel}, {CpuName} ({CpuCores} cores), {RamTotalGb:0.0} GB RAM, " +
        string.Join(", ", Gpus.Select(g => $"{g.Name} (driver {g.DriverVersion})")) +
        $", {OsName} {OsBuild}";
}

public sealed record GpuSpec(string Name, string DriverVersion, double VramGb);
