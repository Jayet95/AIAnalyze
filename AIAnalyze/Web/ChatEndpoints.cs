using System.Text.Json;
using AIAnalyze.Capture;
using AIAnalyze.Chat;
using AIAnalyze.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AIAnalyze.Web;

/// <summary>Request body of POST /api/chat.</summary>
public sealed record ChatRequest(List<ChatTurn>? Messages, long? SessionId);

/// <summary>Alerts feed and the chat endpoint, which streams the answer as server-sent events.</summary>
public static class ChatEndpoints
{
    private const int MaxMessageLength = 4000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapChat(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/alerts", (long? after) =>
        {
            using var store = new SampleStore(TelemetryPaths.DatabasePath);
            return Results.Ok(store.GetAlerts(after ?? 0, 50));
        });

        api.MapGet("/chat/status", (ChatRouter chat, Account.AccountService account) => Results.Ok(new
        {
            configured = chat.IsAvailable,
            mode = chat.Mode.ToString(),
            signInAvailable = account.Status.SignInConfigured,
        }));

        api.MapPost("/chat", async (HttpContext http, ChatRequest request, ChatRouter chat, CaptureStatus capture, Health.HealthState health, ILogger<ClaudeChat> logger) =>
        {
            // Only the app's own page may spend the user's quota or API credits. A JSON body already forces a CORS
            // preflight, which this server never approves; the origin check also covers same-machine pages.
            if (!IsAllowedOrigin(http.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (!chat.IsAvailable)
            {
                return Results.Json(new { error = "The assistant is off. Sign in to use it, or add your own Anthropic API key." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var error = Validate(request.Messages);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            string briefing;
            using (var store = new SampleStore(TelemetryPaths.DatabasePath))
            {
                briefing = DiagnosticContext.Build(store, capture, request.SessionId, DateTimeOffset.UtcNow, health.Latest);
            }

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var cancel = http.RequestAborted;

            try
            {
                await foreach (var chunk in chat.StreamAnswerAsync(request.Messages!, briefing, cancel))
                {
                    await Send(http, new { text = chunk }, cancel);
                }
                await Send(http, new { done = true }, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // The user closed the page or stopped the answer.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat request failed");
                await Send(http, new { error = FriendlyError(ex) }, CancellationToken.None);
            }

            return Results.Empty;
        });
    }

    /// <summary>Requests without an Origin header (not from a browser page) or from this app's own address pass.</summary>
    public static bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var localHost = uri.Host is "127.0.0.1" or "localhost" or "[::1]";
        return localHost && uri.Port == request.Host.Port;
    }

    public static string? Validate(List<ChatTurn>? messages)
    {
        if (messages is not { Count: > 0 })
        {
            return "Ask a question first.";
        }
        if (messages[^1].Role != "user")
        {
            return "The last message must be the user's question.";
        }
        if (messages.Any(m => m.Role is not ("user" or "assistant")))
        {
            return "Messages must come from the user or the assistant.";
        }
        if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > MaxMessageLength))
        {
            return $"Messages must be between 1 and {MaxMessageLength} characters.";
        }
        return null;
    }

    private static async Task Send(HttpContext http, object payload, CancellationToken cancel)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, Json)}\n\n", cancel);
        await http.Response.Body.FlushAsync(cancel);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        ChatServiceException service => service.Message,
        Anthropic.Exceptions.AnthropicRateLimitException => "Too many questions in a short time. Try again in a minute.",
        Anthropic.Exceptions.AnthropicUnauthorizedException => "The Anthropic API key was rejected. Check the key in the installer or ANTHROPIC_API_KEY.",
        HttpRequestException => "Could not reach the Anthropic API. Check the internet connection.",
        _ => "The assistant could not answer right now. Try again.",
    };
}
