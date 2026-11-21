using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIAnalyze.Account;

namespace AIAnalyze.Chat;

/// <summary>A chat failure with a message that can be shown to the user as-is.</summary>
public sealed class ChatServiceException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Asks questions through the AIAnalyze server for a signed-in user. The server holds the Anthropic key,
/// owns the assistant's instructions and enforces the daily free quota; it streams the same events as the
/// local endpoint ({text}, {done}, {error}).
/// </summary>
public sealed class CloudChat(HttpClient http, CloudOptions options, AccountService account)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => options.ApiConfigured && account.SignedIn;

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        IReadOnlyList<ChatTurn> turns,
        string briefing,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var token = await account.GetAccessTokenAsync(cancellationToken)
            ?? throw new ChatServiceException("Your sign-in has expired. Sign in again.", HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBaseUrl.TrimEnd('/')}/v1/chat")
        {
            Content = JsonContent.Create(new { messages = turns.TakeLast(ClaudeChat.MaxTurns), briefing }, options: Json),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadError(response, cancellationToken);
            throw new ChatServiceException(error ?? $"The AIAnalyze server answered {(int)response.StatusCode}.", response.StatusCode);
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            using var doc = JsonDocument.Parse(line[5..]);
            var root = doc.RootElement;
            if (root.TryGetProperty("text", out var text) && text.GetString() is { } chunk)
            {
                yield return chunk;
            }
            else if (root.TryGetProperty("error", out var err))
            {
                throw new ChatServiceException(err.GetString() ?? "The assistant could not answer.");
            }
            else if (root.TryGetProperty("done", out _))
            {
                yield break;
            }
        }
    }

    private static async Task<string?> TryReadError(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>How the assistant is reached on this PC.</summary>
public enum ChatMode
{
    Off,
    Account,
    ApiKey,
}

/// <summary>
/// Picks how to answer: through the signed-in account (the operator's key on the server) when possible,
/// otherwise the user's own API key, otherwise the assistant is off.
/// </summary>
public sealed class ChatRouter(ClaudeChat local, CloudChat cloud, ClaudeOptions options)
{
    public ChatMode Mode => cloud.IsAvailable ? ChatMode.Account : local.IsConfigured ? ChatMode.ApiKey : ChatMode.Off;

    public bool IsAvailable => Mode != ChatMode.Off;

    public bool AutoFeedbackEnabled => IsAvailable && options.AutoFeedback;

    public string FeedbackQuestion => local.FeedbackQuestion;

    public IAsyncEnumerable<string> StreamAnswerAsync(IReadOnlyList<ChatTurn> turns, string briefing, CancellationToken cancellationToken) =>
        Mode switch
        {
            ChatMode.Account => cloud.StreamAnswerAsync(turns, briefing, cancellationToken),
            ChatMode.ApiKey => local.StreamAnswerAsync(turns, briefing, cancellationToken),
            _ => throw new ChatServiceException("The assistant is off. Sign in, or add your own API key."),
        };

    public async Task<string> AnswerAsync(string question, string briefing, CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder();
        await foreach (var chunk in StreamAnswerAsync([new ChatTurn("user", question)], briefing, cancellationToken))
        {
            answer.Append(chunk);
        }
        return answer.ToString().Trim();
    }
}
