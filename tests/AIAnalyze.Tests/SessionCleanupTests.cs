using AIAnalyze.Analysis;
using AIAnalyze.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIAnalyze.Tests;

public class SessionCleanupTests
{
    private static readonly DateTimeOffset Junk = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Real = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    private static CompletedSession Session(string app, DateTimeOffset start) => new(
        app,
        start,
        start.AddSeconds(10),
        FrameCount: 600,
        MedianFps: 60,
        Seconds: [new FpsSecond(start.ToUnixTimeSeconds(), 60)]);

    [Fact]
    public void Deleting_by_program_removes_only_that_programs_sessions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-byapp-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SampleStore(path);
            var junk = store.InsertSession(Session("Surfshark.exe", Junk), "default");
            store.InsertSession(Session("surfshark.exe", Junk.AddMinutes(1)), "default");
            store.InsertSession(Session("game.exe", Real), "default");
            store.SaveFeedback(junk, "x", Junk);

            Assert.Equal(2, store.DeleteSessionsOf("SURFSHARK.EXE"));
            Assert.Equal("game.exe", Assert.Single(store.ListSessions(10)).Application);
            Assert.Empty(store.GetSessionSeconds(junk));
            Assert.Null(store.GetFeedback(junk));
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

    [Fact]
    public void Pruning_drops_old_alerts_and_per_second_fps_but_keeps_sessions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-prune-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        try
        {
            using var store = new SampleStore(path);
            var old = store.InsertSession(Session("game.exe", now.AddDays(-40)), "default");
            var recent = store.InsertSession(Session("game.exe", now.AddDays(-1)), "default");
            store.AddAlert(now.AddDays(-40), "Temperature", FindingSeverity.Warning, "old");
            store.AddAlert(now.AddDays(-1), "Temperature", FindingSeverity.Warning, "recent");

            store.PruneHistory(now);

            Assert.Equal(2, store.ListSessions(10).Count);
            Assert.Empty(store.GetSessionSeconds(old));
            Assert.Single(store.GetSessionSeconds(recent));
            Assert.Equal("recent", Assert.Single(store.GetAlerts(0, 10)).Message);
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

    [Fact]
    public void Delete_removes_sessions_in_range_and_their_fps_rows_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-cleanup-{Guid.NewGuid():N}.db");
        try
        {
            long from, to;
            using (var store = new SampleStore(path))
            {
                store.InsertSession(Session("furmark.exe", Junk), "default");
                store.InsertSession(Session("furmark.exe", Junk.AddSeconds(5)), "default");
                store.InsertSession(Session("game.exe", Real), "default");

                from = Junk.AddMinutes(-1).ToUnixTimeMilliseconds();
                to = Junk.AddMinutes(1).ToUnixTimeMilliseconds();

                Assert.Equal(2, store.CountSessionsBetween(from, to));
                Assert.Equal(2, store.DeleteSessionsBetween(from, to));
                Assert.Equal(0, store.CountSessionsBetween(from, to));
                Assert.Single(store.ListSessions(10));
            }

            // The kept session's per-second rows must survive the delete.
            using var store2 = new SampleStore(path);
            var kept = Assert.Single(store2.ListSessions(10));
            Assert.Single(store2.GetSessionSeconds(kept.Id));
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
