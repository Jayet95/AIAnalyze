using AIAnalyze.Analysis;
using AIAnalyze.Health;
using AIAnalyze.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIAnalyze.Tests;

public class HealthAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static HealthSnapshot Snapshot(
        IEnumerable<HealthEvent>? events = null,
        IEnumerable<DriveSpace>? drives = null,
        IEnumerable<PhysicalDiskHealth>? disks = null,
        MemoryUse? memory = null,
        IEnumerable<ProcessUse>? cpu = null,
        BatteryHealth? battery = null,
        double uptimeDays = 1) =>
        new(Now, TimeSpan.FromDays(uptimeDays), (events ?? []).ToList(), (drives ?? []).ToList(), (disks ?? []).ToList(),
            memory ?? new MemoryUse(32, 10), (cpu ?? []).ToList(), [], battery);

    private static HealthEvent Event(string provider, int id, string message = "", string level = "Error", string log = "System", int hoursAgo = 1) =>
        new(Now.AddHours(-hoursAgo), log, provider, id, level, message);

    private static HealthCheckResult Check(HealthSnapshot s, string id) =>
        HealthAnalyzer.Analyze(s).Checks.Single(c => c.Id == id);

    [Fact]
    public void A_clean_pc_passes_every_check()
    {
        var report = HealthAnalyzer.Analyze(Snapshot(drives: [new DriveSpace("C:", "", 1000, 500)]));

        Assert.All(report.Checks, c => Assert.Null(c.Severity));
        Assert.Null(report.Worst);
    }

    [Fact]
    public void Blue_screen_is_critical_and_unexpected_shutdown_is_a_warning()
    {
        Assert.Equal(FindingSeverity.Critical,
            Check(Snapshot([Event(HealthAnalyzer.ProviderBugCheck, 1001, "The computer has rebooted from a bugcheck.")]), "crashes").Severity);
        Assert.Equal(FindingSeverity.Warning,
            Check(Snapshot([Event(HealthAnalyzer.ProviderKernelPower, 41, "The system has rebooted without cleanly shutting down first.", "Critical")]), "crashes").Severity);
    }

    [Fact]
    public void Corrected_hardware_errors_warn_and_uncorrected_ones_are_critical()
    {
        Assert.Equal(FindingSeverity.Warning,
            Check(Snapshot([Event(HealthAnalyzer.ProviderWhea, 19, "A corrected hardware error has occurred.", "Warning")]), "hardware-errors").Severity);
        Assert.Equal(FindingSeverity.Critical,
            Check(Snapshot([Event(HealthAnalyzer.ProviderWhea, 18, "A fatal hardware error has occurred.", "Error")]), "hardware-errors").Severity);
    }

    [Fact]
    public void Repeated_graphics_driver_resets_are_critical()
    {
        var one = Snapshot([Event(HealthAnalyzer.ProviderDisplay, 4101, "Display driver nvlddmkm stopped responding and has successfully recovered.", "Warning")]);
        var three = Snapshot(Enumerable.Range(1, 3).Select(h => Event(HealthAnalyzer.ProviderDisplay, 4101, "Display driver stopped responding", "Warning", hoursAgo: h)));

        Assert.Equal(FindingSeverity.Warning, Check(one, "display-driver").Severity);
        Assert.Equal(FindingSeverity.Critical, Check(three, "display-driver").Severity);
    }

    [Fact]
    public void Program_crashes_are_grouped_by_program_and_only_repeats_warn()
    {
        var events = Enumerable.Range(1, 3)
            .Select(h => Event(HealthAnalyzer.ProviderAppError, 1000, "Faulting application name: game.exe, version: 1.0.0.0", log: "Application", hoursAgo: h))
            .Append(Event(HealthAnalyzer.ProviderAppHang, 1002, "The program notepad.exe version 10.0 stopped interacting with Windows", log: "Application"));

        var check = Check(Snapshot(events), "app-crashes");

        Assert.Equal(FindingSeverity.Warning, check.Severity);
        Assert.Contains("game.exe", check.Summary);
        Assert.Contains(check.Evidence, e => e.StartsWith("notepad.exe: 1 crash(es), 1 of them hangs"));
    }

    [Fact]
    public void Occasional_service_failures_pass_but_a_repeating_one_warns()
    {
        var twice = Enumerable.Range(1, 2).Select(h => Event(HealthAnalyzer.ProviderServices, 7000, "The Group Policy Client service failed to start.", hoursAgo: h));
        var thrice = Enumerable.Range(1, 3).Select(h => Event(HealthAnalyzer.ProviderServices, 7031, "The Backup Agent service terminated unexpectedly.", hoursAgo: h));

        Assert.Null(Check(Snapshot(twice), "services").Severity);
        var check = Check(Snapshot(thrice), "services");
        Assert.Equal(FindingSeverity.Warning, check.Severity);
        Assert.Contains("Backup Agent", check.Summary);
    }

    [Fact]
    public void Failed_store_app_update_is_named_with_the_meaning_of_its_error()
    {
        var events = Enumerable.Range(1, 4).Select(h => Event(HealthAnalyzer.ProviderWindowsUpdate, 20,
            "Installation Failure: Windows failed to install the following update with error 0x80073D02: 9N0866FS04W8-DolbyLaboratories.DolbyAccess.", hoursAgo: h));

        var check = Check(Snapshot(events), "updates");

        Assert.Equal(FindingSeverity.Warning, check.Severity);
        Assert.StartsWith("DolbyAccess (Microsoft Store app) failed to install 4 times", check.Summary);
        Assert.Contains("app was running", check.Summary);
    }

    [Theory]
    [InlineData(50, null)]
    [InlineData(8, FindingSeverity.Warning)]
    [InlineData(3, FindingSeverity.Critical)]
    public void Disk_space_thresholds(long freePercent, FindingSeverity? expected)
    {
        var check = Check(Snapshot(drives: [new DriveSpace("C:", "Windows", 1000, freePercent * 10)]), "disk-space");

        Assert.Equal(expected, check.Severity);
    }

    [Fact]
    public void Unhealthy_drive_is_critical()
    {
        var check = Check(Snapshot(disks: [new PhysicalDiskHealth("Old HDD", "HDD", "Unhealthy", "Predictive failure")]), "disk-health");

        Assert.Equal(FindingSeverity.Critical, check.Severity);
        Assert.Contains("Back up", check.Summary);
    }

    [Theory]
    [InlineData(20, null)]
    [InlineData(29, FindingSeverity.Warning)]
    [InlineData(31, FindingSeverity.Critical)]
    public void Memory_thresholds(double usedGb, FindingSeverity? expected)
    {
        Assert.Equal(expected, Check(Snapshot(memory: new MemoryUse(32, usedGb)), "memory").Severity);
    }

    [Fact]
    public void A_program_using_much_of_the_cpu_is_named()
    {
        var check = Check(Snapshot(cpu: [new ProcessUse("miner", 42, 65, 300)]), "cpu-hogs");

        Assert.Equal(FindingSeverity.Warning, check.Severity);
        Assert.Contains("miner", check.Summary);
    }

    [Fact]
    public void Long_uptime_suggests_a_restart_and_worn_battery_warns()
    {
        Assert.Equal(FindingSeverity.Info, Check(Snapshot(uptimeDays: 20), "uptime").Severity);
        Assert.Equal(FindingSeverity.Warning, Check(Snapshot(battery: new BatteryHealth(90000, 45000)), "battery").Severity);
        Assert.DoesNotContain(HealthAnalyzer.Analyze(Snapshot()).Checks, c => c.Id == "battery");
    }

    [Fact]
    public void Alerts_fire_only_when_a_check_gets_worse()
    {
        var calm = HealthAnalyzer.Analyze(Snapshot());
        var warning = HealthAnalyzer.Analyze(Snapshot(memory: new MemoryUse(32, 29)));
        var critical = HealthAnalyzer.Analyze(Snapshot(memory: new MemoryUse(32, 31)));

        Assert.Equal(["memory"], HealthMonitorService.Worsened(calm, warning).Select(c => c.Id));
        Assert.Empty(HealthMonitorService.Worsened(warning, warning));
        Assert.Equal(["memory"], HealthMonitorService.Worsened(warning, critical).Select(c => c.Id));
        Assert.Empty(HealthMonitorService.Worsened(critical, warning));
    }

    [Fact]
    public void Health_report_round_trips_through_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-health-{Guid.NewGuid():N}.db");
        try
        {
            var report = HealthAnalyzer.Analyze(Snapshot(memory: new MemoryUse(32, 31), uptimeDays: 20));
            using (var store = new SampleStore(path))
            {
                store.SaveHealthReport(report);
                var loaded = store.GetLatestHealthReport();

                Assert.NotNull(loaded);
                Assert.Equal(FindingSeverity.Critical, loaded!.Checks.Single(c => c.Id == "memory").Severity);
                Assert.Equal(report.Checks.Count, loaded.Checks.Count);
                Assert.Equal(20, loaded.Uptime.TotalDays, 3);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
