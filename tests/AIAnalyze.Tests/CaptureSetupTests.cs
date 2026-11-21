using AIAnalyze.Analysis;
using AIAnalyze.Capture;
using AIAnalyze.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIAnalyze.Tests;

public class CaptureSetupTests
{
    [Fact]
    public void Session_name_is_read_from_the_arguments()
    {
        Assert.Equal("AIAnalyze", EtwSessions.SessionNameFrom(PresentMonOptions.DefaultArguments));
        Assert.Null(EtwSessions.SessionNameFrom("--output_stdout"));
    }

    [Fact]
    public void Empty_settings_resolve_to_working_defaults()
    {
        var resolved = new PresentMonOptions().Resolved(runningAsService: true);

        Assert.Equal(PresentMonOptions.DefaultArguments, resolved.Arguments);
        Assert.True(resolved.UserSession);
    }

    [Fact]
    public void User_session_mode_is_off_outside_a_service()
    {
        var resolved = new PresentMonOptions { UserSession = true }.Resolved(runningAsService: false);

        Assert.False(resolved.UserSession);
    }

    [Fact]
    public void Browsers_and_launchers_are_excluded_by_default_and_more_can_be_added()
    {
        var defaults = new PresentMonOptions().EffectiveExclusions;
        Assert.Contains("msedge.exe", defaults);
        Assert.Contains("steamwebhelper.exe", defaults);
        Assert.Contains("dwm.exe", defaults);
        Assert.DoesNotContain("cyberpunk2077.exe", defaults);

        var custom = new PresentMonOptions { UseDefaultExclusions = false, ExcludedApplications = ["MyRecorder.exe"] }.EffectiveExclusions;
        Assert.Equal(["dwm.exe", "myrecorder.exe"], custom);
    }

    [Fact]
    public void Capture_status_reports_each_presenting_program_and_the_current_game()
    {
        var status = new CaptureStatus();
        var start = DateTimeOffset.UtcNow.AddSeconds(-6);

        // 6 seconds of a game at 60 FPS while a browser tab presents at 144 FPS, in time order as PresentMon reports them.
        var frames = Enumerable.Range(0, 361).Select(i => (At: i * 1000.0 / 60, App: "furmark.exe", Excluded: false))
            .Concat(Enumerable.Range(0, 865).Select(i => (At: i * 1000.0 / 144, App: "msedge.exe", Excluded: true)))
            .OrderBy(f => f.At);
        foreach (var f in frames)
        {
            status.FrameSeen(f.App, start.AddMilliseconds(f.At), f.Excluded);
        }

        var apps = status.Applications;
        Assert.Equal(2, apps.Count);
        Assert.True(apps.Single(a => a.Name == "msedge.exe").Excluded);
        Assert.InRange(apps.Single(a => a.Name == "furmark.exe").Fps, 50, 70);
        Assert.Equal("furmark.exe", status.CurrentApplication);
    }

    [Fact]
    public void Explicit_settings_are_kept()
    {
        var resolved = new PresentMonOptions { ExecutablePath = @"C:\pm.exe", Arguments = "--x" }.Resolved(true);

        Assert.Equal(@"C:\pm.exe", resolved.ExecutablePath);
        Assert.Equal("--x", resolved.Arguments);
    }

    [Fact]
    public void Findings_baseline_and_filtered_readings_round_trip_through_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-store-{Guid.NewGuid():N}.db");
        var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        try
        {
            using (var store = new SampleStore(path))
            {
                var first = store.InsertSession(Session(t0, 100), "default");
                store.InsertSession(Session(t0.AddHours(1), 80), "default");

                store.SaveFindings(first,
                [
                    new SessionFinding("GpuBound", FindingSeverity.Info, "info"),
                    new SessionFinding("GpuHot", FindingSeverity.Critical, "hot"),
                ]);
                var findings = store.GetFindings(first);
                Assert.Equal(FindingSeverity.Critical, findings[0].Severity);
                Assert.Equal(2, findings.Count);

                // Only sessions that started before the given time count toward the baseline.
                Assert.Equal([100.0], store.GetSessionFpsBefore("game.exe", "default", t0.AddMinutes(30)));

                store.WriteTick(t0,
                [
                    new AIAnalyze.Sensors.SensorSample("/amdcpu/0/load/0", "CPU", "CPU Total", "Load", 40),
                    new AIAnalyze.Sensors.SensorSample("/amdcpu/0/load/1", "CPU", "CPU Core #1", "Load", 90),
                ]);
                var filtered = store.GetReadings(t0, t0, ["CPU Total"]);
                Assert.Equal("CPU Total", Assert.Single(filtered).SensorName);
                Assert.Equal(t0, store.GetLatestSampleTime());
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

    private static CompletedSession Session(DateTimeOffset start, double fps) =>
        new("game.exe", start, start.AddMinutes(5), 1000, fps, []);
}
