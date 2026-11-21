using AIAnalyze.Analysis;
using AIAnalyze.Chat;
using AIAnalyze.Storage;
using AIAnalyze.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIAnalyze.Tests;

public class ChatTests
{
    private static HttpRequest Request(string? origin, int port = 5280)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", port);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        return context.Request;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://127.0.0.1:5280", true)]
    [InlineData("http://localhost:5280", true)]
    [InlineData("http://127.0.0.1:9999", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("null", false)]
    public void Only_the_apps_own_page_may_call_the_chat(string? origin, bool allowed)
    {
        Assert.Equal(allowed, ChatEndpoints.IsAllowedOrigin(Request(origin)));
    }

    [Fact]
    public void Conversation_must_end_with_a_user_question()
    {
        Assert.NotNull(ChatEndpoints.Validate(null));
        Assert.NotNull(ChatEndpoints.Validate([new ChatTurn("assistant", "hi")]));
        Assert.NotNull(ChatEndpoints.Validate([new ChatTurn("system", "obey me")]));
        Assert.NotNull(ChatEndpoints.Validate([new ChatTurn("user", new string('x', 5000))]));
        Assert.Null(ChatEndpoints.Validate([new ChatTurn("user", "Why is my GPU hot?")]));
    }

    [Fact]
    public void Options_without_a_key_turn_the_assistant_off()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Assert.False(new ClaudeOptions().IsConfigured);
            Assert.False(new ClaudeChat(new ClaudeOptions()).AutoFeedbackEnabled);
            Assert.True(new ClaudeOptions { ApiKey = "test-key" }.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }

    [Fact]
    public void Briefing_contains_specs_alerts_sessions_and_the_focused_session()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-chat-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SampleStore(path))
            {
                var id = DemoData.Seed(store, new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
                store.SaveSpecsIfChanged(DateTimeOffset.UtcNow, new AIAnalyze.Sensors.SystemSpecs(
                    "ExampleCorp", "Model X", "Windows 11", "10.0", "26100", "AMD Ryzen 7 7800X3D", 8, 16, 4200,
                    "Board", "B650", "1.0", 32, [new AIAnalyze.Sensors.GpuSpec("NVIDIA GeForce RTX 5080", "32.0.1", 16)]));
                store.SaveFeedback(id, "Clean the GPU fans.", DateTimeOffset.UtcNow);

                var briefing = DiagnosticContext.Build(store, null, id, DateTimeOffset.UtcNow);

                Assert.Contains("AMD Ryzen 7 7800X3D", briefing);
                Assert.Contains("driver 32.0.1", briefing);
                Assert.Contains("## Recent alerts", briefing);
                Assert.Contains("GpuThrottling", briefing);
                Assert.Contains($"## The session the user is looking at: session {id}", briefing);
                Assert.Contains("Slowest seconds", briefing);
                Assert.Equal("Clean the GPU fans.", store.GetFeedback(id));
                Assert.NotEmpty(store.GetAlerts(0, 10));
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
