using AIAnalyze.Analysis;
using AIAnalyze.Storage;
using Xunit;

namespace AIAnalyze.Tests;

public class SessionTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);

    private static StoredReading Reading(DateTimeOffset at, string sensorId, string name, string type, double? value) =>
        new(at.ToUnixTimeMilliseconds(), sensorId, "Hardware", name, type, value);

    [Fact]
    public void Session_tracker_counts_frames_per_second()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));
        var frame = new FrameRecord("game.exe", 0, 16.6);

        tracker.AddFrame(frame, T0);
        tracker.AddFrame(frame, T0.AddMilliseconds(400));
        tracker.AddFrame(frame, T0.AddMilliseconds(900));
        tracker.AddFrame(frame, T0.AddSeconds(1));
        var session = Assert.Single(tracker.Finish());

        Assert.Equal(2, session.Seconds.Count);
        Assert.Equal(3, session.Seconds[0].Frames);
        Assert.Equal(1, session.Seconds[1].Frames);
    }

    [Fact]
    public void Build_pairs_fps_with_matching_cpu_and_gpu_readings()
    {
        var fps = new[] { new FpsSecond(T0.ToUnixTimeSeconds(), 60) };
        var readings = new[]
        {
            Reading(T0, "/amdcpu/0/temperature/0", "Core (Tctl/Tdie)", "Temperature", 72.5),
            Reading(T0, "/amdcpu/0/load/0", "CPU Total", "Load", 40.0),
            Reading(T0, "/gpu-nvidia/0/load/0", "GPU Core", "Load", 99.0),
            Reading(T0, "/gpu-nvidia/0/temperature/0", "GPU Core", "Temperature", 78.0),
        };

        var row = Assert.Single(SessionTimeline.Build(fps, readings));

        Assert.Equal(60, row.Fps);
        Assert.Equal(72.5, row.Metrics["CPU °C"]);
        Assert.Equal(40.0, row.Metrics["CPU %"]);
        Assert.Equal(99.0, row.Metrics["GPU %"]);
        Assert.Equal(78.0, row.Metrics["GPU °C"]);
    }

    [Fact]
    public void Missing_metrics_are_null_not_zero()
    {
        var fps = new[] { new FpsSecond(T0.ToUnixTimeSeconds(), 30) };

        var row = Assert.Single(SessionTimeline.Build(fps, []));

        Assert.Null(row.Metrics["GPU °C"]);
        Assert.Null(row.Metrics["CPU W"]);
    }

    [Fact]
    public void Readings_from_other_seconds_do_not_leak_into_a_row()
    {
        var fps = new[] { new FpsSecond(T0.ToUnixTimeSeconds(), 60) };
        var readings = new[] { Reading(T0.AddSeconds(5), "/amdcpu/0/load/0", "CPU Total", "Load", 99.0) };

        var row = Assert.Single(SessionTimeline.Build(fps, readings));

        Assert.Null(row.Metrics["CPU %"]);
    }

    [Fact]
    public void Format_prints_header_and_one_line_per_second()
    {
        var session = new StoredSession(7, "game.exe", T0, T0.AddSeconds(2), 120, 60.0);
        var fps = new[] { new FpsSecond(T0.ToUnixTimeSeconds(), 60), new FpsSecond(T0.AddSeconds(1).ToUnixTimeSeconds(), 58) };

        var text = SessionTimeline.Format(session, SessionTimeline.Build(fps, []));

        Assert.Contains("game.exe  id=7", text);
        Assert.Contains("2 seconds", text);
        Assert.Contains("median 60.0 FPS", text);
        // Header line, blank line, two data rows after the two header lines.
        Assert.Equal(6, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
