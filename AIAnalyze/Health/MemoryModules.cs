using System.Management;
using System.Text.RegularExpressions;
using AIAnalyze.Analysis;

namespace AIAnalyze.Health;

/// <summary>
/// One installed RAM stick as Windows reports it (SMBIOS through WMI). Serial numbers are not read.
/// RatedSpeedMts is the module's reported speed; it is often the standard (JEDEC) speed, not the XMP/EXPO
/// profile printed on the box, which Windows cannot see.
/// </summary>
public sealed record MemoryModule(
    string Slot,
    string Bank,
    string? Channel,
    double CapacityGb,
    string Type,
    int? RatedSpeedMts,
    int? ConfiguredSpeedMts,
    string Manufacturer,
    string PartNumber);

public static partial class MemoryModules
{
    /// <summary>Installed sticks and the number of slots on the board (0 when unknown).</summary>
    public static (IReadOnlyList<MemoryModule> Modules, int TotalSlots) Read()
    {
        var modules = new List<MemoryModule>();
        var slots = 0;
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceLocator, BankLabel, Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
            {
                foreach (var m in searcher.Get().Cast<ManagementBaseObject>())
                {
                    var slot = Text(m["DeviceLocator"]);
                    var bank = Text(m["BankLabel"]);
                    modules.Add(new MemoryModule(
                        slot,
                        bank,
                        ChannelOf(slot, bank),
                        Math.Round(Convert.ToDouble(m["Capacity"] ?? 0) / 1_073_741_824d, 1),
                        TypeName(Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0)),
                        PositiveOrNull(m["Speed"]),
                        PositiveOrNull(m["ConfiguredClockSpeed"]),
                        Text(m["Manufacturer"]),
                        Text(m["PartNumber"])));
                }
            }

            using var arrays = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            slots = arrays.Get().Cast<ManagementBaseObject>().Sum(a => Convert.ToInt32(a["MemoryDevices"] ?? 0));
        }
        catch (ManagementException)
        {
        }
        return (modules.OrderBy(m => m.Slot, StringComparer.OrdinalIgnoreCase).ToList(), slots);
    }

    /// <summary>
    /// The memory channel from the slot or bank name. Boards name slots many ways: "DIMM_A1", "A2",
    /// "ChannelB-DIMM0", "Controller0-ChannelA-DIMM1", "P0 CHANNEL A". Returns the letter, or null.
    /// </summary>
    public static string? ChannelOf(string slot, string bank)
    {
        foreach (var text in new[] { slot, bank })
        {
            var channel = ChannelWord().Match(text);
            if (channel.Success)
            {
                // Intel names slots per memory controller ("Controller1-ChannelA-DIMM0"): each controller is its
                // own channel, so "Controller0 A" and "Controller1 A" are different channels.
                var controller = Controller().Match(text);
                var letter = channel.Groups[1].Value.ToUpperInvariant();
                return controller.Success ? $"{controller.Groups[1].Value}{letter}" : letter;
            }
        }
        var dimm = DimmLetter().Match(slot);
        return dimm.Success ? dimm.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>The "Memory setup" health check.</summary>
    public static HealthCheckResult Check(IReadOnlyList<MemoryModule> modules, int totalSlots)
    {
        const string id = "memory-config";
        const string title = "Memory setup";
        if (modules.Count == 0)
        {
            return new(id, title, null, "Memory modules could not be read on this PC.", []);
        }

        var evidence = modules.Select(Describe).ToList();
        if (totalSlots > modules.Count)
        {
            evidence.Add($"{totalSlots - modules.Count} of {totalSlots} slots are empty");
        }

        var totalGb = modules.Sum(m => m.CapacityGb);
        var summary = $"{totalGb:0.#} GB in {modules.Count} stick(s)" + (totalSlots > 0 ? $", {modules.Count} of {totalSlots} slots used." : ".");

        // Two or more sticks that all sit on one channel run in single-channel mode: half the bandwidth.
        var channels = modules.Select(m => m.Channel).Distinct().ToList();
        if (modules.Count >= 2 && channels.Count == 1 && channels[0] is not null && totalSlots >= 4)
        {
            return new(id, title, FindingSeverity.Warning,
                $"All sticks are in channel {channels[0]} ({string.Join(", ", modules.Select(m => m.Slot))}), so memory runs in single-channel mode with half the bandwidth. Move one stick to the other channel; most boards want slots A2 and B2 for two sticks (check the motherboard manual).",
                evidence);
        }

        var slower = modules.Where(m => m.ConfiguredSpeedMts < m.RatedSpeedMts).ToList();
        if (slower.Count > 0)
        {
            var s = slower[0];
            return new(id, title, FindingSeverity.Info,
                $"Memory runs at {s.ConfiguredSpeedMts} MT/s, below the {s.RatedSpeedMts} MT/s the stick reports. If you didn't lower it on purpose, enable the XMP or EXPO profile in the BIOS.",
                evidence);
        }

        if (modules.Count == 1 && totalSlots >= 2)
        {
            return new(id, title, FindingSeverity.Info,
                $"{summary} With one stick the memory uses a single channel. Adding a matching second stick in the other channel doubles memory bandwidth, which helps games and integrated graphics.",
                evidence);
        }

        var mixed = modules.Select(m => (m.CapacityGb, m.PartNumber)).Distinct().Count() > 1;
        if (mixed)
        {
            return new(id, title, FindingSeverity.Info,
                $"{summary} The sticks are not identical (different size or model). Mixed sticks usually work but can force lower speeds or cause instability with XMP/EXPO.",
                evidence);
        }

        return new(id, title, null, summary + " Configuration looks right.", evidence);
    }

    private static string Describe(MemoryModule m)
    {
        var speed = m.ConfiguredSpeedMts is int c
            ? m.RatedSpeedMts is int r && r != c ? $"{c} MT/s (reports {r})" : $"{c} MT/s"
            : m.RatedSpeedMts is int rated ? $"{rated} MT/s" : "speed unknown";
        var model = string.Join(" ", new[] { m.Manufacturer, m.PartNumber }.Where(x => x.Length > 0));
        return $"{m.Slot}: {m.CapacityGb:0.#} GB {m.Type}, {speed}{(model.Length > 0 ? $", {model}" : "")}";
    }

    private static string TypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "RAM",
    };

    private static int? PositiveOrNull(object? value)
    {
        var n = value is null ? 0 : Convert.ToInt32(value);
        return n > 0 ? n : null;
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? "";

    [GeneratedRegex(@"CHANNEL\s*-?\s*([A-H])(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"CONTROLLER\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Controller();

    // "DIMM_A1", "DIMM A1", "A1_DIMM", "A2"
    [GeneratedRegex(@"(?:^|[\s_-])(?:DIMM[\s_-]?)?([A-H])\d(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DimmLetter();
}
