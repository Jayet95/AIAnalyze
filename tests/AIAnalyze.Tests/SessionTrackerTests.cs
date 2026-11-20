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

        Assert.Empty(tracker.AddFrame(Frame(), T0));
        Assert.Empty(tracker.AddFrame(Frame(), T0.AddSeconds(5)));

        var session = Assert.Single(tracker.Finish());
        Assert.Equal(2, session.FrameCount);
    }

    [Fact]
    public void A_pause_longer_than_the_gap_closes_the_session()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));
        tracker.AddFrame(Frame(), T0);
        tracker.AddFrame(Frame(), T0.AddSeconds(1));

        var closed = Assert.Single(tracker.AddFrame(Frame(), T0.AddMinutes(5)));

        Assert.Equal(2, closed.FrameCount);
        Assert.Equal(T0.AddSeconds(1), closed.EndUtc);
    }

    [Fact]
    public void Frames_from_two_applications_make_two_sessions_not_fragments()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));

        // Interleaved, as PresentMon reports them when a game and a browser both present.
        for (var i = 0; i < 10; i++)
        {
            tracker.AddFrame(Frame("game.exe"), T0.AddMilliseconds(i * 20));
            tracker.AddFrame(Frame("browser.exe"), T0.AddMilliseconds(i * 20 + 5));
        }

        var sessions = tracker.Finish();

        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(10, s.FrameCount));
    }

    [Fact]
    public void Excluded_applications_produce_no_session()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30), excludedApps: ["dwm.exe"]);

        tracker.AddFrame(Frame("dwm.exe"), T0);
        tracker.AddFrame(Frame("DWM.EXE"), T0.AddSeconds(1));

        Assert.Empty(tracker.Finish());
    }

    [Fact]
    public void Sessions_shorter_than_the_minimum_are_dropped()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30), minFrames: 5);
        for (var i = 0; i < 4; i++)
        {
            tracker.AddFrame(Frame(), T0.AddMilliseconds(i * 16));
        }

        Assert.Empty(tracker.Finish());
    }

    [Fact]
    public void CloseIdle_ends_a_silent_application_without_any_new_frame()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));
        tracker.AddFrame(Frame("game.exe"), T0);
        tracker.AddFrame(Frame("game.exe"), T0.AddSeconds(1));

        var closed = tracker.CloseIdle(T0.AddMinutes(2));

        Assert.Equal(2, Assert.Single(closed).FrameCount);
        Assert.Empty(tracker.Finish());
    }

    [Fact]
    public void CloseIdle_keeps_sessions_that_are_still_active()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));
        tracker.AddFrame(Frame("game.exe"), T0);

        Assert.Empty(tracker.CloseIdle(T0.AddSeconds(10)));
        Assert.Single(tracker.Finish());
    }

    [Fact]
    public void Finish_with_no_frames_returns_nothing()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(30));

        Assert.Empty(tracker.Finish());
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
