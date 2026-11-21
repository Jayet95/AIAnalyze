using System.Text.Json;
using AIAnalyze.Account;
using AIAnalyze.Capture;
using AIAnalyze.Storage;
using AIAnalyze.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AIAnalyze.Web;

public sealed record PrivacyRequest(bool Share);

public sealed record FeedbackRequest(string? Target, string? Kind, bool? Helpful);

public sealed record NotificationsRequest(bool EmailAlerts);

/// <summary>The sharing choice, a preview of exactly what would be uploaded, and feedback votes.</summary>
public static class PrivacyEndpoints
{
    private static readonly string[] Targets = ["finding", "health", "answer"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapPrivacy(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/privacy", (PrivacySettings privacy, CloudOptions cloud, Health.HealthState health) =>
        {
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            var pending = store.GetPendingOutbox("feedback", 200)
                .Select(p => JsonSerializer.Deserialize<FeedbackVote>(p.Payload, Json))
                .ToList();
            return Results.Ok(new
            {
                consent = privacy.Consent,
                serverConfigured = cloud.ApiConfigured,
                preview = new
                {
                    deviceId = privacy.DeviceId,
                    snapshot = TelemetryBuilder.Build(store, health.Latest, DateTimeOffset.UtcNow),
                    feedback = pending,
                },
            });
        });

        api.MapPost("/privacy", (HttpRequest http, PrivacyRequest request, PrivacySettings privacy) =>
        {
            if (!ChatEndpoints.IsAllowedOrigin(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            privacy.SetConsent(request.Share);
            return Results.Ok(new { consent = request.Share });
        });

        // Email alerts for serious problems: off until the user turns them on; needs a signed-in account.
        api.MapGet("/notifications", (Notifications.EmailAlertSettings settings, Notifications.EmailAlertService email, AccountService account, CloudOptions cloud) =>
            Results.Ok(new
            {
                emailAlerts = settings.Enabled,
                available = email.Available,
                email = account.Status.Email,
                reason = !cloud.ApiConfigured ? "This build has no AIAnalyze server set up yet."
                    : !account.SignedIn ? "Sign in to get email alerts. They go to your account's email address."
                    : null,
                dailyLimit = Notifications.EmailAlertRules.DailyLimit,
            }));

        api.MapPost("/notifications", (HttpRequest http, NotificationsRequest request, Notifications.EmailAlertSettings settings, Notifications.EmailAlertService email) =>
        {
            if (!ChatEndpoints.IsAllowedOrigin(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (request.EmailAlerts && !email.Available)
            {
                return Results.BadRequest(new { error = "Sign in first: email alerts go to your account's email address." });
            }
            settings.Enabled = request.EmailAlerts;
            return Results.Ok(new { emailAlerts = request.EmailAlerts });
        });

        api.MapPost("/notifications/test", async (HttpRequest http, Notifications.EmailAlertService email, CancellationToken cancel) =>
        {
            if (!ChatEndpoints.IsAllowedOrigin(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (!email.Available)
            {
                return Results.BadRequest(new { error = "Sign in first: email alerts go to your account's email address." });
            }
            var result = await email.SendAsync("Info", "Test email from AIAnalyze",
                "Email alerts work. You'll get an email like this when AIAnalyze finds a serious problem on your PC.",
                DateTimeOffset.UtcNow, cancel, test: true);
            return result == Notifications.EmailAlertService.SendResult.Sent
                ? Results.Ok(new { sent = true })
                : Results.Json(new { error = "The server could not send the email. Try again later." }, statusCode: StatusCodes.Status502BadGateway);
        });

        api.MapPost("/feedback", (HttpRequest http, FeedbackRequest request) =>
        {
            if (!ChatEndpoints.IsAllowedOrigin(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (request.Target is null || !Targets.Contains(request.Target) || request.Helpful is null
                || string.IsNullOrWhiteSpace(request.Kind) || request.Kind.Length > 64)
            {
                return Results.BadRequest(new { error = "Feedback needs a target, a kind and helpful true or false." });
            }

            // Kept on this PC; uploaded only if the user agreed to share.
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            var vote = new FeedbackVote(request.Target, request.Kind, request.Helpful.Value, DateTimeOffset.UtcNow);
            store.AddToOutbox("feedback", JsonSerializer.Serialize(vote, Json), vote.AtUtc);
            return Results.Ok(new { saved = true });
        });
    }
}
