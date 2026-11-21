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
