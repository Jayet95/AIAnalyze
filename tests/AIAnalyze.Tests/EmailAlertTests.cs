using AIAnalyze.Notifications;
using AIAnalyze.Storage;
using Xunit;

namespace AIAnalyze.Tests;

public class EmailAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
    private static long _id;

    private static StoredAlert Alert(string kind, string severity, string message) =>
        new(++_id, Now.AddMinutes(-1), kind, severity, message, null);

    [Fact]
    public void Only_serious_alerts_are_emailed()
    {
        var alerts = new[]
        {
            Alert("Live", "Warning", "FPS drop in game.exe: 50 FPS, usually 120."),
            Alert("Health:disk-health", "Critical", "Drive health: Windows reports ST2000DM008 as unhealthy."),
            Alert("Trend", "Warning", "Drive C filling up: full in about 20 days at this pace."),
            Alert("Feedback", "Info", "Session reviewed."),
            Alert("Temperature", "Critical", "NVIDIA GeForce RTX 5080 GPU Core is 94.0 °C, at or above the critical limit of 93 °C."),
            Alert("Health:uptime", "Info", "Restart: 16 days."),
        };

        var emails = EmailAlertRules.Select(alerts, new Dictionary<string, DateTimeOffset>(), Now);

        Assert.Equal(["Drive health", "Drive C filling up", "Your PC is running hot"], emails.Select(e => e.Title));
        Assert.Equal("Windows reports ST2000DM008 as unhealthy.", emails[0].Detail);
        Assert.StartsWith("NVIDIA GeForce RTX 5080 GPU Core is 94.0", emails[2].Detail);
    }

    [Fact]
    public void The_same_problem_is_emailed_once_a_day()
    {
        var first = Alert("Temperature", "Critical", "GPU Core is 94 °C.");
        var again = Alert("Temperature", "Critical", "GPU Core is 95 °C.");

        var emails = EmailAlertRules.Select([first, again], new Dictionary<string, DateTimeOffset>(), Now);
        var key = Assert.Single(emails).Key;

        var sent = new Dictionary<string, DateTimeOffset> { [key] = Now.AddHours(-3) };
        Assert.Empty(EmailAlertRules.Select([Alert("Temperature", "Critical", "GPU Core is 96 °C.")], sent, Now));

        sent[key] = Now.AddHours(-25);
        Assert.Single(EmailAlertRules.Select([Alert("Temperature", "Critical", "GPU Core is 96 °C.")], sent, Now));
    }

    [Fact]
    public void At_most_five_emails_go_out_a_day()
    {
        var alerts = Enumerable.Range(0, 8).Select(i => Alert($"Health:check{i}", "Critical", $"Check {i}: broken.")).ToList();
        Assert.Equal(EmailAlertRules.DailyLimit, EmailAlertRules.Select(alerts, new Dictionary<string, DateTimeOffset>(), Now).Count);

        var sent = new Dictionary<string, DateTimeOffset> { ["a"] = Now.AddHours(-1), ["b"] = Now.AddHours(-2), ["c"] = Now.AddHours(-30) };
        Assert.Equal(3, EmailAlertRules.Select(alerts, sent, Now).Count);
    }
}
