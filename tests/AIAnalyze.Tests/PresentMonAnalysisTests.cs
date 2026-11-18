using AIAnalyze.Analysis;
using Xunit;

namespace AIAnalyze.Tests;

public class PresentMonAnalysisTests
{
    private const string Header = "Application,ProcessID,TimeInSeconds,MsBetweenPresents";

    [Fact]
    public void Parser_reads_columns_by_header_name_not_position()
    {
        var lines = new[]
        {
            "TimeInSeconds,Application,MsBetweenPresents,ProcessID",
            "0.0,game.exe,16.6,100",
            "0.0167,game.exe,16.7,100",
        };

        var frames = PresentMonParser.Parse(lines);

        Assert.Equal(2, frames.Count);
        Assert.Equal("game.exe", frames[0].Application);
        Assert.Equal(16.6, frames[0].MsBetweenPresents, precision: 3);
    }

    [Fact]
    public void Parser_skips_bad_rows_and_fails_on_missing_columns()
    {
        var lines = new[]
        {
            Header,
            "game.exe,100,1.0,16.6",
            "game.exe,100,oops,16.6",
            "game.exe,100,2.0,",
        };

        Assert.Single(PresentMonParser.Parse(lines));
        Assert.Throws<FormatException>(() => PresentMonParser.Parse(new[] { "Application,ProcessID" }));
    }

    [Fact]
    public void Session_fps_is_median_so_one_stutter_does_not_skew_it()
    {
        // 59 frames at 60 FPS plus one 500 ms stutter frame.
        var frames = Enumerable.Range(0, 59)
            .Select(i => new FrameRecord("game.exe", i * 0.0167, 1000.0 / 60))
            .Append(new FrameRecord("game.exe", 1.0, 500))
            .ToList();

        var fps = BaselineRules.SessionFps(frames);

        Assert.NotNull(fps);
        Assert.InRange(fps!.Value, 59.9, 60.1);
    }

    [Fact]
    public void Median_handles_even_and_odd_counts()
    {
        Assert.Equal(2.0, Stats.Median(new[] { 3.0, 1.0, 2.0 }));
        Assert.Equal(2.5, Stats.Median(new[] { 1.0, 2.0, 3.0, 4.0 }));
    }

    [Fact]
    public void Drop_over_threshold_is_reported()
    {
        var baseline = new Baseline("game.exe", "high-1080p", MedianFps: 100, SessionCount: 5);

        var result = BaselineRules.Evaluate(baseline, sessionFps: 70);

        Assert.Equal(DeviationKind.FpsDrop, result.Kind);
        Assert.Contains("30% below", result.Message);
    }

    [Fact]
    public void Drop_within_threshold_is_not_reported()
    {
        var baseline = new Baseline("game.exe", "high-1080p", MedianFps: 100, SessionCount: 5);

        var result = BaselineRules.Evaluate(baseline, sessionFps: 85);

        Assert.Equal(DeviationKind.None, result.Kind);
    }

    [Fact]
    public void No_alert_until_baseline_has_enough_sessions()
    {
        var baseline = new Baseline("game.exe", "high-1080p", MedianFps: 100, SessionCount: 2);

        var result = BaselineRules.Evaluate(baseline, sessionFps: 40);

        Assert.Equal(DeviationKind.InsufficientBaseline, result.Kind);
    }

    [Fact]
    public void Baseline_is_median_of_session_fps()
    {
        var baseline = BaselineRules.BuildBaseline("game.exe", "high-1080p", new[] { 90.0, 100.0, 110.0 });

        Assert.Equal(100.0, baseline.MedianFps);
        Assert.Equal(3, baseline.SessionCount);
    }
}
