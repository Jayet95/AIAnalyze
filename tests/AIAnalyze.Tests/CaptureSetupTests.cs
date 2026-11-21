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
