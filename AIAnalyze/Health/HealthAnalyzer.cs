using System.Globalization;
using System.Text.RegularExpressions;
using AIAnalyze.Analysis;

namespace AIAnalyze.Health;

/// <summary>
/// Turns a <see cref="HealthSnapshot"/> into checks that each pass, warn or fail, with the evidence
/// behind them and advice a non-expert can act on. Every check returns a result, so the UI can show
/// what was checked as well as what is wrong.
/// </summary>
public static partial class HealthAnalyzer
{
    public const string ProviderBugCheck = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string ProviderKernelPower = "Microsoft-Windows-Kernel-Power";
    public const string ProviderWhea = "Microsoft-Windows-WHEA-Logger";
    public const string ProviderDisplay = "Display";
    public const string ProviderAppError = "Application Error";
    public const string ProviderAppHang = "Application Hang";
    public const string ProviderServices = "Service Control Manager";
    public const string ProviderWindowsUpdate = "Microsoft-Windows-WindowsUpdateClient";

    /// <summary>Event Log providers each log is queried for (the collector uses this list).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> WatchedProviders = new Dictionary<string, string[]>
    {
        ["System"] = [ProviderBugCheck, ProviderKernelPower, ProviderWhea, ProviderDisplay, ProviderServices, ProviderWindowsUpdate, "BugCheck"],
        ["Application"] = [ProviderAppError, ProviderAppHang],
    };

    private static readonly int[] ServiceFailureIds = [7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034];

    public static HealthReport Analyze(HealthSnapshot s)
    {
        var checks = new List<HealthCheckResult>
        {
            Crashes(s),
            HardwareErrors(s),
            DisplayDriver(s),
            AppCrashes(s),
            Services(s),
            Updates(s),
            DiskSpace(s),
            DiskHealth(s),
            Memory(s),
            CpuHogs(s),
            Uptime(s),
        };
        if (s.Battery is not null)
        {
            checks.Add(Battery(s.Battery));
        }

        return new HealthReport(s.AtUtc, s.Uptime, checks, s.Memory, s.Drives, s.TopByCpu, s.TopByMemory);
    }

    private static HealthCheckResult Crashes(HealthSnapshot s)
    {
        var bugChecks = s.Events.Where(e => e.EventId == 1001 && e.Provider is ProviderBugCheck or "BugCheck").ToList();
        var unexpected = s.Events.Where(e => e.Provider == ProviderKernelPower && e.EventId == 41).ToList();

        if (bugChecks.Count > 0)
        {
            return new("crashes", "System crashes", FindingSeverity.Critical,
                $"Windows crashed with a blue screen {Times(bugChecks.Count)} in the last 7 days. Repeated blue screens usually come from a faulty driver, unstable overclock or RAM settings, overheating, or failing hardware.",
                Evidence(bugChecks.Concat(unexpected)));
        }
        if (unexpected.Count > 0)
        {
            return new("crashes", "System crashes", FindingSeverity.Warning,
                $"The PC shut down or restarted without warning {Times(unexpected.Count)} in the last 7 days. That happens on power loss, a held power button, or a freeze; if you didn't cause it, check the power supply, temperatures and drivers.",
                Evidence(unexpected));
        }
        return Ok("crashes", "System crashes", "No blue screens or unexpected shutdowns in the last 7 days.");
    }

    private static HealthCheckResult HardwareErrors(HealthSnapshot s)
    {
        var whea = s.Events.Where(e => e.Provider == ProviderWhea).ToList();
        if (whea.Count == 0)
        {
            return Ok("hardware-errors", "Hardware errors", "The processor, memory and buses reported no hardware errors in the last 7 days.");
        }

        var fatal = whea.Any(e => e.Level is "Critical" or "Error");
        return new("hardware-errors", "Hardware errors", fatal ? FindingSeverity.Critical : FindingSeverity.Warning,
            fatal
                ? $"The hardware reported {whea.Count} error(s) in the last 7 days (WHEA), including at least one it could not correct. Common causes: an unstable CPU or memory overclock (including XMP/EXPO), overheating, or a failing component."
                : $"The hardware reported {whea.Count} corrected error(s) in the last 7 days (WHEA). Windows recovered, but frequent corrected errors point to unstable overclock or memory settings, or a component starting to fail.",
            Evidence(whea));
    }

    private static HealthCheckResult DisplayDriver(HealthSnapshot s)
    {
        var resets = s.Events.Where(e => e.Provider == ProviderDisplay && e.EventId == 4101).ToList();
        if (resets.Count == 0)
        {
            return Ok("display-driver", "Graphics driver", "The graphics driver didn't stop responding in the last 7 days.");
        }

        return new("display-driver", "Graphics driver", resets.Count >= 3 ? FindingSeverity.Critical : FindingSeverity.Warning,
            $"The graphics driver stopped responding and was restarted {Times(resets.Count)} in the last 7 days (screen goes black or a game crashes). Try a clean install of the latest driver from the GPU maker, or go back to the previous one if this began after an update; also check GPU temperatures and any GPU overclock.",
            Evidence(resets));
    }

    private static HealthCheckResult AppCrashes(HealthSnapshot s)
    {
        var crashes = s.Events
            .Where(e => e.Provider is ProviderAppError or ProviderAppHang)
            .Select(e => (Event: e, App: AppName(e)))
            .Where(x => x.App is not null)
            .GroupBy(x => x.App!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.Key, Count: g.Count(), Hangs: g.Count(x => x.Event.Provider == ProviderAppHang)))
            .OrderByDescending(g => g.Count)
            .ToList();

        var repeated = crashes.Where(c => c.Count >= 3).ToList();
        var evidence = crashes.Take(5).Select(c => $"{c.App}: {c.Count} crash(es){(c.Hangs > 0 ? $", {c.Hangs} of them hangs" : "")}").ToList();
        if (repeated.Count == 0)
        {
            return Ok("app-crashes", "Program crashes",
                crashes.Count == 0 ? "No program crashed in the last 7 days." : "A few programs crashed once or twice in the last 7 days, which is normal.",
                evidence);
        }

        return new("app-crashes", "Program crashes", FindingSeverity.Warning,
            $"{string.Join(", ", repeated.Take(3).Select(c => c.App))} crashed or hung repeatedly in the last 7 days. Update or reinstall the program; if several unrelated programs crash, suspect memory, drivers or overclocking.",
            evidence);
    }

    private static HealthCheckResult Services(HealthSnapshot s)
    {
        var failures = s.Events.Where(e => e.Provider == ProviderServices && ServiceFailureIds.Contains(e.EventId)).ToList();
        var byService = failures
            .Select(e => ServiceName().Match(e.Message))
            .Where(m => m.Success)
            .GroupBy(m => m.Groups[1].Value)
            .OrderByDescending(g => g.Count())
            .ToList();
        var names = byService.Select(g => $"{g.Key}: {g.Count()} failure(s)").ToList();

        // An occasional failure is normal (for example on a slow network at sign-in). Warn when one service keeps
        // failing, or when many fail.
        var repeating = byService.Where(g => g.Count() >= 3).Select(g => g.Key).ToList();
        if (repeating.Count == 0 && failures.Count < 6)
        {
            return Ok("services", "Background services",
                failures.Count == 0 ? "Windows services started and ran normally." : "A few services failed once or twice, which is usually harmless.",
                names);
        }

        var who = repeating.Count > 0 ? string.Join(", ", repeating.Take(3)) : "Several services";
        return new("services", "Background services", FindingSeverity.Warning,
            $"{who} failed to start or stopped unexpectedly repeatedly in the last 7 days. A failing service can cause slow startup or missing features; it often belongs to a program that was uninstalled badly or needs an update.",
            names.Take(5).ToList());
    }

    /// <summary>Plain-language meaning of the update error codes users hit most.</summary>
    private static readonly Dictionary<string, string> UpdateErrorHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x80073D02"] = "the app was running while it was being updated; close it or restart, then update again",
        ["0x80070070"] = "there was not enough free disk space",
        ["0x80070005"] = "access was denied; run Windows Update again after a restart",
        ["0x8024402C"] = "Windows could not reach the update server; check the internet connection or VPN",
        ["0x800F0922"] = "Windows could not reach the update server or the system reserved partition is full",
        ["0x80240034"] = "the download failed; run Windows Update again",
    };

    private static HealthCheckResult Updates(HealthSnapshot s)
    {
        var failed = s.Events.Where(e => e.Provider == ProviderWindowsUpdate && e.EventId == 20).ToList();
        if (failed.Count == 0)
        {
            return Ok("updates", "Windows Update", "No failed Windows updates in the last 7 days.");
        }

        // "Windows failed to install the following update with error 0x80073D02: 9N0866FS04W8-DolbyLaboratories.DolbyAccess."
        var byUpdate = failed
            .Select(e => UpdateFailure().Match(e.Message))
            .Where(m => m.Success)
            .GroupBy(m => (Error: m.Groups[1].Value, Name: CleanUpdateName(m.Groups[2].Value)))
            .OrderByDescending(g => g.Count())
            .ToList();

        var evidence = byUpdate
            .Select(g => $"{g.Key.Name}: failed {Times(g.Count())} with error {g.Key.Error}" +
                         (UpdateErrorHints.TryGetValue(g.Key.Error, out var hint) ? $" ({hint})" : ""))
            .Take(5)
            .ToList();

        var top = byUpdate.FirstOrDefault();
        var summary = top is null
            ? $"Windows updates failed to install {Times(failed.Count)} in the last 7 days. Restart and run Windows Update again."
            : $"{top.Key.Name} failed to install {Times(top.Count())} in the last 7 days" +
              (UpdateErrorHints.TryGetValue(top.Key.Error, out var topHint) ? $": {topHint}." : $" (error {top.Key.Error}). Restart and run Windows Update again.") +
              (byUpdate.Count > 1 ? $" {byUpdate.Count - 1} other update(s) also failed." : "");

        return new("updates", "Windows Update", FindingSeverity.Warning, summary, evidence.Count > 0 ? evidence : Evidence(failed));
    }

    /// <summary>"9N0866FS04W8-DolbyLaboratories.DolbyAccess" becomes "DolbyAccess (Microsoft Store app)".</summary>
    private static string CleanUpdateName(string raw)
    {
        var name = raw.Trim().TrimEnd('.');
        var store = StoreProduct().Match(name);
        return store.Success ? $"{store.Groups[1].Value.Split('.').Last()} (Microsoft Store app)" : name;
    }

    private static HealthCheckResult DiskSpace(HealthSnapshot s)
    {
        var evidence = s.Drives.Select(d => $"{d.Name} {d.Label}: {Gb(d.FreeBytes)} free of {Gb(d.TotalBytes)} ({d.FreePercent:0}%)".Replace("  ", " ")).ToList();
        var critical = s.Drives.Where(d => d.FreePercent < 5).ToList();
        var low = s.Drives.Where(d => d.FreePercent < 10).ToList();

        if (critical.Count > 0)
        {
            return new("disk-space", "Disk space", FindingSeverity.Critical,
                $"{string.Join(", ", critical.Select(d => d.Name))} is almost full. Windows slows down, updates fail and programs can crash when a drive is this full. Free space with Storage settings > Cleanup recommendations, or move large files.",
                evidence);
        }
        if (low.Count > 0)
        {
            return new("disk-space", "Disk space", FindingSeverity.Warning,
                $"{string.Join(", ", low.Select(d => d.Name))} has less than 10% free. Free some space before it starts to slow Windows and break updates.",
                evidence);
        }
        return Ok("disk-space", "Disk space", "Every drive has enough free space.", evidence);
    }

    private static HealthCheckResult DiskHealth(HealthSnapshot s)
    {
        var evidence = s.Disks.Select(d => $"{d.Name} ({d.MediaType}): {d.Health}, {d.OperationalStatus}").ToList();
        var unhealthy = s.Disks.Where(d => d.Health == "Unhealthy").ToList();
        var warning = s.Disks.Where(d => d.Health == "Warning").ToList();

        if (unhealthy.Count > 0)
        {
            return new("disk-health", "Drive health", FindingSeverity.Critical,
                $"Windows reports {string.Join(", ", unhealthy.Select(d => d.Name))} as unhealthy. Back up your files now and plan to replace the drive.",
                evidence);
        }
        if (warning.Count > 0)
        {
            return new("disk-health", "Drive health", FindingSeverity.Warning,
                $"Windows reports a warning for {string.Join(", ", warning.Select(d => d.Name))}. Back up important files and keep an eye on it; the drive's own tool (for example from Samsung, WD or Crucial) can show details.",
                evidence);
        }
        return Ok("disk-health", "Drive health",
            s.Disks.Count == 0 ? "Drive health is not available on this PC." : "Every drive reports healthy.", evidence);
    }

    private static HealthCheckResult Memory(HealthSnapshot s)
    {
        if (s.Memory is not { } m)
        {
            return Ok("memory", "Memory", "Memory use could not be read.");
        }

        var evidence = s.TopByMemory.Take(5).Select(p => $"{p.Name}: {p.MemoryMb:0} MB").ToList();
        var summary = $"{m.UsedGb:0.0} of {m.TotalGb:0.0} GB in use ({m.UsedPercent:0}%).";
        if (m.UsedPercent >= 95)
        {
            return new("memory", "Memory", FindingSeverity.Critical,
                $"Memory is nearly full: {summary} Windows is moving data to disk, which makes everything slow. Close the programs using the most memory, below; if this is normal for you, more RAM would help.",
                evidence);
        }
        if (m.UsedPercent >= 90)
        {
            return new("memory", "Memory", FindingSeverity.Warning,
                $"Memory is running low: {summary} Closing a few of the programs below will keep the PC responsive.",
                evidence);
        }
        return Ok("memory", "Memory", summary, evidence);
    }

    private static HealthCheckResult CpuHogs(HealthSnapshot s)
    {
        var evidence = s.TopByCpu.Take(5).Select(p => $"{p.Name}: {p.CpuPercent:0}% CPU").ToList();
        var hog = s.TopByCpu.FirstOrDefault();
        if (hog is not null && hog.CpuPercent >= 40)
        {
            return new("cpu-hogs", "Programs using the CPU", FindingSeverity.Warning,
                $"{hog.Name} was using {hog.CpuPercent:0}% of the whole CPU when checked. If you are not using it right now, it may be stuck, updating, or unwanted software; it slows down everything else.",
                evidence);
        }
        return Ok("cpu-hogs", "Programs using the CPU", "No single program was using a large share of the CPU.", evidence);
    }

    private static HealthCheckResult Uptime(HealthSnapshot s)
    {
        var days = s.Uptime.TotalDays;
        if (days >= 14)
        {
            return new("uptime", "Restart", FindingSeverity.Info,
                $"The PC has been running for {days:0} days without a restart. A restart applies pending updates and clears slowdowns that build up over time.",
                []);
        }
        return Ok("uptime", "Restart", $"Last restart {Describe(s.Uptime)} ago.");
    }

    private static HealthCheckResult Battery(BatteryHealth b)
    {
        var evidence = new List<string> { $"Holds {b.FullChargeCapacity:N0} of its original {b.DesignCapacity:N0} mWh" };
        if (b.WearPercent >= 40)
        {
            return new("battery", "Battery", FindingSeverity.Warning,
                $"The battery has lost {b.WearPercent:0}% of its original capacity, so it runs out much sooner than when new. Consider replacing it.",
                evidence);
        }
        if (b.WearPercent >= 20)
        {
            return new("battery", "Battery", FindingSeverity.Info,
                $"The battery has lost {b.WearPercent:0}% of its original capacity, which is normal wear for its age.",
                evidence);
        }
        return Ok("battery", "Battery", $"The battery holds {100 - b.WearPercent:0}% of its original capacity.", evidence);
    }

    // ------------------------------------------------------------------ helpers

    private static HealthCheckResult Ok(string id, string title, string summary, IReadOnlyList<string>? evidence = null) =>
        new(id, title, null, summary, evidence ?? []);

    /// <summary>The newest events as short lines: local time and the first line of the message.</summary>
    private static IReadOnlyList<string> Evidence(IEnumerable<HealthEvent> events) =>
        events
            .OrderByDescending(e => e.AtUtc)
            .Take(5)
            .Select(e => $"{e.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: {FirstLine(e.Message)}")
            .ToList();

    /// <summary>The program named in an Application Error (1000) or Application Hang (1002) event.</summary>
    public static string? AppName(HealthEvent e)
    {
        var match = e.Provider == ProviderAppHang ? HangApp().Match(e.Message) : FaultingApp().Match(e.Message);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    private static string Times(int count) =>
        count switch
        {
            1 => "once",
            2 => "twice",
            _ => $"{count} times",
        };

    private static string Gb(long bytes) => $"{bytes / 1_073_741_824.0:0} GB";

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0} day(s)" : t.TotalHours >= 1 ? $"{t.TotalHours:0} hour(s)" : $"{t.TotalMinutes:0} minute(s)";

    [GeneratedRegex(@"Faulting application name:\s*([^,\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FaultingApp();

    [GeneratedRegex(@"The program\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex HangApp();

    [GeneratedRegex(@"The (.+?) service", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"error\s+(0x[0-9A-Fa-f]{8}):\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateFailure();

    // Store product ids are 12 characters starting with 9 (for example 9N0866FS04W8), then "-Publisher.App".
    [GeneratedRegex(@"^9[A-Z0-9]{11}-(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreProduct();
}
