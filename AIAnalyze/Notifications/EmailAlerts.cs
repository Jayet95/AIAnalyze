using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIAnalyze.Account;
using AIAnalyze.Capture;
using AIAnalyze.Storage;

namespace AIAnalyze.Notifications;

/// <summary>An alert chosen for email, with the title and text the email shows.</summary>
public sealed record EmailAlert(long AlertId, string Key, string Severity, string Title, string Detail, DateTimeOffset AtUtc);

/// <summary>
/// Which alerts are worth an email: serious ones only. Critical problems of any kind (a failing drive,
/// blue screens, critical heat, a drive about to fill), and potential problems from trends, which matter
/// most when the user is away from the PC. The same problem is emailed at most once a day, and at most
/// <see cref="DailyLimit"/> emails go out a day.
/// </summary>
public static class EmailAlertRules
{
    public const int DailyLimit = 5;
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromDays(1);

    public static IReadOnlyList<EmailAlert> Select(
        IReadOnlyList<StoredAlert> alerts,
        IReadOnlyDictionary<string, DateTimeOffset> sent,
        DateTimeOffset now)
    {
        var sentToday = sent.Values.Count(at => now - at < TimeSpan.FromDays(1));
        var chosen = new List<EmailAlert>();
        var keys = new HashSet<string>();
        foreach (var a in alerts.OrderBy(a => a.Id))
        {
            if (sentToday + chosen.Count >= DailyLimit)
            {
                break;
            }
            if (!Serious(a))
            {
                continue;
            }
            var (title, detail) = Split(a);
            var key = $"{a.Kind.Split(':')[0]}|{title}".ToLowerInvariant();
            if (keys.Contains(key) || (sent.TryGetValue(key, out var at) && now - at < RepeatAfter))
            {
                continue;
            }
            keys.Add(key);
            chosen.Add(new EmailAlert(a.Id, key, a.Severity, title, detail, a.AtUtc));
        }
        return chosen;
    }

    public static bool Serious(StoredAlert a) =>
        a.Kind != "Feedback" && (a.Severity == "Critical" || (a.Kind == "Trend" && a.Severity == "Warning"));

    /// <summary>Most alerts are "Title: detail". Temperature alerts are a single sentence.</summary>
    public static (string Title, string Detail) Split(StoredAlert a)
    {
        if (a.Kind == "Temperature")
        {
            return ("Your PC is running hot", a.Message);
        }
        var cut = a.Message.IndexOf(": ", StringComparison.Ordinal);
        return cut is > 0 and <= 80 ? (a.Message[..cut], a.Message[(cut + 2)..]) : ("AIAnalyze found a problem", a.Message);
    }
}

/// <summary>The user's choice, kept on this PC. Off until the user turns it on.</summary>
public sealed class EmailAlertSettings
{
    private const string EnabledKey = "email_alerts";

    public bool Enabled
    {
        get
        {
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            return store.GetSetting(EnabledKey) == "on";
        }
        set
        {
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            store.SetSetting(EnabledKey, value ? "on" : "off");
            // Start from now: turning it on never emails old alerts.
            store.SetSetting(EmailAlertService.AfterKey, store.GetAlerts(0, 1).FirstOrDefault()?.Id.ToString() ?? "0");
        }
    }
}

/// <summary>
/// Emails serious problems to the signed-in user, when they turned it on. Only the alert's title and
/// text go to the AIAnalyze server, which sends the email to the account's address; nothing else from
/// the PC is sent. Runs every minute and retries later when the server can't be reached.
/// </summary>
public sealed class EmailAlertService(
    ILogger<EmailAlertService> logger,
    EmailAlertSettings settings,
    CloudOptions cloud,
    AccountService account,
    IHttpClientFactory httpFactory) : BackgroundService
{
    public const string AfterKey = "email_alert_after";
    private const string SentKey = "email_alert_sent";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Available => cloud.ApiConfigured && account.SignedIn;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SendPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Email alerts skipped; they will retry later.");
            }
        }
    }

    public async Task SendPendingAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled || !Available)
        {
            return;
        }

        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        var after = long.TryParse(store.GetSetting(AfterKey), out var id) ? id : 0;
        var alerts = store.GetAlerts(after, 50);
        if (alerts.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var sent = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(store.GetSetting(SentKey) ?? "{}") ?? [];
        foreach (var email in EmailAlertRules.Select(alerts, sent, now))
        {
            var result = await SendAsync(email.Severity, email.Title, email.Detail, email.AtUtc, cancellationToken);
            if (result == SendResult.Retry)
            {
                return; // keep the watermark; try again next minute
            }
            if (result == SendResult.Sent)
            {
                sent[email.Key] = now;
                logger.LogInformation("Emailed alert: {Title}", email.Title);
            }
        }

        store.SetSetting(SentKey, JsonSerializer.Serialize(sent.Where(kv => now - kv.Value < TimeSpan.FromDays(2)).ToDictionary()));
        store.SetSetting(AfterKey, alerts.Max(a => a.Id).ToString());
    }

    public enum SendResult { Sent, Rejected, Retry }

    /// <summary>Asks the server to email the signed-in user.</summary>
    public async Task<SendResult> SendAsync(string severity, string title, string detail, DateTimeOffset at, CancellationToken cancellationToken, bool test = false)
    {
        var token = await account.GetAccessTokenAsync(cancellationToken);
        if (token is null)
        {
            return SendResult.Rejected;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{cloud.ApiBaseUrl.TrimEnd('/')}/v1/notify")
        {
            Content = JsonContent.Create(new { severity, title = Cut(title, 120), detail = Cut(detail, 1000), atUtc = at, test }, options: Json),
        };
        request.Headers.Authorization = new("Bearer", token);
        try
        {
            using var response = await httpFactory.CreateClient("cloud").SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return SendResult.Sent;
            }
            logger.LogInformation("The server did not send the email alert ({Status}).", (int)response.StatusCode);
            // A server error is retried; "not set up" (503), the daily limit (429) and bad input are not.
            var status = (int)response.StatusCode;
            return status >= 500 && status != 503 ? SendResult.Retry : SendResult.Rejected;
        }
        catch (HttpRequestException)
        {
            return SendResult.Retry;
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
