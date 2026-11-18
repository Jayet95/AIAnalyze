using AIAnalyze.Analysis;
using Xunit;

namespace AIAnalyze.Tests;

public class SessionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);

    private static FrameRecord Frame(string app = "game.exe") => new(app, 0, 16.6);

    [Fact]
    public void Frames_within_the_gap_stay_in_one_session()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));

        Assert.Null(tracker.AddFrame(Frame(), T0));
        Assert.Null(tracker.AddFrame(Frame(), T0.AddSeconds(5)));
        var finished = tracker.Finish();

        Assert.NotNull(finished);
        Assert.Equal(2, finished!.FrameCount);
    }

    [Fact]
    public void A_pause_longer_than_the_gap_closes_the_session()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));
        tracker.AddFrame(Frame(), T0);
        tracker.AddFrame(Frame(), T0.AddSeconds(1));

        var closed = tracker.AddFrame(Frame(), T0.AddMinutes(5));

        Assert.NotNull(closed);
        Assert.Equal(2, closed!.FrameCount);
        Assert.Equal(T0.AddSeconds(1), closed.EndUtc);
    }

    [Fact]
    public void Switching_application_closes_the_previous_session()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));
        tracker.AddFrame(Frame("game.exe"), T0);

        var closed = tracker.AddFrame(Frame("other.exe"), T0.AddSeconds(1));

        Assert.NotNull(closed);
        Assert.Equal("game.exe", closed!.Application);
    }

    [Fact]
    public void Finish_with_no_frames_returns_nothing()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));

        Assert.Null(tracker.Finish());
    }

    [Fact]
    public void Line_parser_skips_header_and_bad_rows()
    {
        var parser = new PresentMonLineParser();
        var rows = new[]
        {
            "Application,ProcessID,TimeInSeconds,MsBetweenPresents",
            "game.exe,100,1.0,16.6",
            "game.exe,100,oops,16.6",
            "game.exe,100,2.0,0",
        };

        var parsed = rows.Select(r => parser.TryParse(r, out var f) ? f : null).Where(f => f is not null).ToList();

        Assert.Single(parsed);
    }
}
