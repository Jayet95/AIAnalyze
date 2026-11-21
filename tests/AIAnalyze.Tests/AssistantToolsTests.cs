using System.Text.Json;
using AIAnalyze.Chat;
using AIAnalyze.Health;
using AIAnalyze.Storage;
using AIAnalyze.Web;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIAnalyze.Tests;

public class AssistantToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aianalyze-tools-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    private AssistantTools NewTools(IReadOnlyList<HealthEvent>? events = null) =>
        new(null, null, null, _path, _ => events ?? []);

    [Fact]
    public void Definitions_and_instructions_load_and_every_tool_has_a_handler()
    {
        Assert.Contains("AIAnalyze", AssistantTools.Instructions);
        Assert.Equal(7, AssistantTools.Definitions.Count);
        var tools = NewTools();
        foreach (var d in AssistantTools.Definitions)
        {
            Assert.Equal("object", d.InputSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            var result = tools.Run(d.Name, Input(new { session_id = 1, category = "all" }));
            Assert.DoesNotContain("unknown tool", result);
            Assert.DoesNotContain("the tool failed", result);
        }
        Assert.Contains("unknown tool", tools.Run("format_disk", Input(new { })));
    }

    [Fact]
    public void Event_log_search_filters_by_category_and_text_and_counts_per_day()
    {
        var events = new List<HealthEvent>
        {
            new(_now.AddDays(-1), "System", HealthAnalyzer.ProviderKernelPower, 41, "Critical", "The system has rebooted without cleanly shutting down first."),
            new(_now.AddDays(-3), "System", HealthAnalyzer.ProviderBugCheck, 1001, "Error", "The computer has rebooted from a bugcheck. 0x00000116"),
            new(_now.AddHours(-2), "Application", HealthAnalyzer.ProviderAppError, 1000, "Error", "Faulting application name: game.exe"),
            new(_now.AddHours(-1), "System", HealthAnalyzer.ProviderDisplay, 4101, "Warning", "Display driver nvlddmkm stopped responding and has successfully recovered."),
        };
        var tools = NewTools(events);

        var crashes = tools.Run("search_event_log", Input(new { category = "crashes", days = 7 }));
        Assert.StartsWith("2 crash and unexpected shutdown event(s)", crashes);
        Assert.Contains("0x00000116", crashes);
        Assert.DoesNotContain("game.exe", crashes);
        Assert.Contains("Per day:", crashes);

        var game = tools.Run("search_event_log", Input(new { category = "all", contains = "GAME.EXE" }));
        Assert.StartsWith("1 health event(s) containing", game);

        Assert.StartsWith("1 graphics driver", tools.Run("search_event_log", Input(new { category = "display_driver" })));
        Assert.StartsWith("0 hardware error", tools.Run("search_event_log", Input(new { category = "hardware_errors" })));
    }

    [Fact]
    public void Sensor_history_sessions_and_session_details_read_the_local_database()
    {
        long id;
        using (var store = new SampleStore(_path))
        {
            id = DemoData.Seed(store, _now.AddMinutes(-10));
        }
        var tools = NewTools();

        var history = tools.Run("get_sensor_history", Input(new { hours = 1, bucket_minutes = 5 }));
        Assert.Contains("GPU °C", history);
        Assert.Contains("Per bucket", history);
        Assert.Contains("NVIDIA", history);

        var old = tools.Run("get_sensor_history", Input(new { hours = 1, end_hours_ago = 30 }));
        Assert.StartsWith("No sensor readings between", old);

        var sessions = tools.Run("list_sessions", Input(new { application = "DEMO" }));
        Assert.Contains($"Session {id}: demo-game.exe", sessions);
        Assert.StartsWith("No sessions of", tools.Run("list_sessions", Input(new { application = "other.exe" })));

        var details = tools.Run("get_session_details", Input(new { session_id = id }));
        Assert.Contains($"## session {id}", details);
        Assert.Contains("Slowest seconds", details);
        Assert.Equal("There is no session 999999.", tools.Run("get_session_details", Input(new { session_id = 999999 })));
    }

    [Fact]
    public void Bad_input_is_clamped_or_reported_not_thrown()
    {
        var tools = NewTools();
        Assert.Equal("Error: session_id is required.", tools.Run("get_session_details", Input(new { session_id = "x" })));
        Assert.Contains("last 30 days", tools.Run("search_event_log", Input(new { category = "crashes", days = 9999 })));
        Assert.Equal("Searching the Event Log (crash and unexpected shutdown, last 30 days)",
            AssistantTools.Describe("search_event_log", Input(new { category = "crashes", days = 400 })));
        Assert.Equal("Analyzing session 7", AssistantTools.Describe("get_session_details", Input(new { session_id = 7 })));
    }

    [Fact]
    public void Results_are_capped_for_the_model()
    {
        var many = Enumerable.Range(0, 2000)
            .Select(i => new HealthEvent(_now.AddMinutes(-i), "Application", HealthAnalyzer.ProviderAppError, 1000, "Error", new string('x', 400)))
            .ToList();
        var result = NewTools(many).Run("search_event_log", Input(new { category = "app_crashes", days = 30, limit = 50 }));
        Assert.True(result.Length <= AssistantTools.MaxResultChars + 20);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
