using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIAnalyze.Account;

namespace AIAnalyze.Chat;

/// <summary>A chat failure with a message that can be shown to the user as-is.</summary>
public sealed class ChatServiceException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Asks questions through the AIAnalyze server for a signed-in user. The server holds the Anthropic key,
/// owns the assistant's instructions and tool definitions, and enforces the daily free quota. Each request
/// is one model call: when the model asks for tools, this class runs them on this PC and sends only their
/// results back in the next request, so the PC's data never leaves except what the model asked for.
/// Events from the server: {text}, {tool}, {assistant, stop_reason}, {done}, {error}.
/// </summary>
public sealed class CloudChat(HttpClient http, CloudOptions options, AccountService account, AssistantTools tools)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => options.ApiConfigured && account.SignedIn;

    public async IAsyncEnumerable<ChatEvent> StreamAnswerAsync(
        IReadOnlyList<ChatTurn> turns,
        string briefing,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = new JsonArray();
        foreach (var turn in turns.TakeLast(ClaudeChat.MaxTurns))
        {
            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

        for (var step = 0; step < ClaudeChat.MaxSteps; step++)
        {
            JsonNode? assistant = null;
            string? stopReason = null;
            await foreach (var (kind, payload) in SendAsync(messages, briefing, cancellationToken))
            {
                switch (kind)
                {
                    case "text":
                        yield return new ChatText(payload.GetString() ?? "");
                        break;
                    case "assistant":
                        assistant = JsonNode.Parse(payload.GetRawText());
                        break;
                    case "stop_reason":
                        stopReason = payload.GetString();
                        break;
                }
            }

            if (stopReason != "tool_use" || assistant is not JsonArray content)
            {
                yield break;
            }

            var results = new JsonArray();
            var wroteText = false;
            foreach (var block in content)
            {
                var type = block?["type"]?.GetValue<string>();
                wroteText |= type == "text";
                if (type != "tool_use")
                {
                    continue;
                }
                var id = block!["id"]!.GetValue<string>();
                var name = block["name"]!.GetValue<string>();
                var input = JsonSerializer.SerializeToElement(block["input"] ?? new JsonObject());
                yield return new ChatToolCall(name, AssistantTools.Describe(name, input));
                var output = await Task.Run(() => tools.Run(name, input), cancellationToken);
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output });
            }
            if (results.Count == 0)
            {
                yield break;
            }
            if (wroteText)
            {
                yield return new ChatText("\n\n");
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
    }

    /// <summary>One model call through the server, as (event kind, value) pairs.</summary>
    private async IAsyncEnumerable<(string Kind, JsonElement Value)> SendAsync(
        JsonArray messages,
        string briefing,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var token = await account.GetAccessTokenAsync(cancellationToken)
            ?? throw new ChatServiceException("Your sign-in has expired. Sign in again.", HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBaseUrl.TrimEnd('/')}/v1/chat")
        {
            Content = new StringContent(
                new JsonObject { ["messages"] = messages.DeepClone(), ["briefing"] = briefing }.ToJsonString(),
                System.Text.Encoding.UTF8,
                "application/json"),
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
            if (root.TryGetProperty("error", out var err))
            {
                throw new ChatServiceException(err.GetString() ?? "The assistant could not answer.");
            }
            if (root.TryGetProperty("done", out _))
            {
                yield break;
            }
            foreach (var kind in (string[])["text", "assistant", "stop_reason"])
            {
                if (root.TryGetProperty(kind, out var value))
                {
                    yield return (kind, value.Clone());
                }
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

    public IAsyncEnumerable<ChatEvent> StreamAnswerAsync(IReadOnlyList<ChatTurn> turns, string briefing, CancellationToken cancellationToken) =>
        Mode switch
        {
            ChatMode.Account => cloud.StreamAnswerAsync(turns, briefing, cancellationToken),
            ChatMode.ApiKey => local.StreamAnswerAsync(turns, briefing, cancellationToken),
            _ => throw new ChatServiceException("The assistant is off. Sign in, or add your own API key."),
        };

    public async Task<string> AnswerAsync(string question, string briefing, CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder();
        await foreach (var ev in StreamAnswerAsync([new ChatTurn("user", question)], briefing, cancellationToken))
        {
            if (ev is ChatText text)
            {
                answer.Append(text.Text);
            }
        }
        return answer.ToString().Trim();
    }
}
