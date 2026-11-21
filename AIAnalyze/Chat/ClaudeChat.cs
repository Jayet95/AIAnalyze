using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace AIAnalyze.Chat;

/// <summary>Settings for the chat assistant, bound from the "Claude" config section.</summary>
public sealed class ClaudeOptions
{
    /// <summary>API key. Empty means: use the ANTHROPIC_API_KEY environment variable.</summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Write feedback on its own when a session ends with warnings (at most one every 10 minutes).</summary>
    public bool AutoFeedback { get; set; } = true;

    /// <summary>Language of the automatic feedback, for example "English" or "Hebrew". Chat replies follow the user.</summary>
    public string FeedbackLanguage { get; set; } = "English";

    public string ResolvedApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";

    public bool IsConfigured => ResolvedApiKey.Length > 0;
}

/// <summary>One message of the conversation as the UI sends it.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Answers questions about the PC with Claude, grounded in the app's measurements.
/// The fixed instructions come first and are cached; the measurements change every request and follow them.
/// </summary>
public sealed class ClaudeChat(ClaudeOptions options)
{
    public const int MaxTurns = 20;

    public const string Instructions = """
        You are the assistant inside AIAnalyze, a diagnostics app running locally on the user's Windows PC.
        With each question you receive a briefing of what the app has measured on this PC: hardware and drivers,
        current sensor readings, recent alerts, recent game sessions with per-second FPS and the app's findings,
        and, when the user has one open, a summary of a single session. Use it to answer questions about the
        PC's performance, temperatures and stability.

        - Reply in the language the user writes in.
        - Base every claim on the briefing and quote the numbers you rely on. When the briefing does not show
          something, say so and say what would show it, rather than guessing.
        - Write for a gamer, not an engineer: lead with the answer, then the evidence, then what to do.
        - Suggest concrete steps the user can take themselves, such as in-game settings, cooling, power plan,
          closing background programs, or updating a driver from the vendor's official site. Mention a restore
          point before driver changes.
        - The app only reads data. It cannot change settings or install anything, so never say you changed something.
        - Keep it short: a few sentences or a short list, unless the user asks for more detail.
        """;

    public bool IsConfigured => options.IsConfigured;

    public bool AutoFeedbackEnabled => options.IsConfigured && options.AutoFeedback;

    /// <summary>The question asked on the user's behalf when a session ends with warnings.</summary>
    public string FeedbackQuestion =>
        $"""
        The session the user is looking at has just ended, and the app flagged problems in it. Give the user short
        feedback on it without being asked: what happened, the most likely cause according to the numbers, and the one
        or two things worth doing. Write in {options.FeedbackLanguage}. At most five sentences or a short list.
        """;

    /// <summary>Asks one question and returns the whole answer.</summary>
    public async Task<string> AnswerAsync(string question, string briefing, CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder();
        await foreach (var chunk in StreamAnswerAsync([new ChatTurn("user", question)], briefing, cancellationToken))
        {
            answer.Append(chunk);
        }
        return answer.ToString().Trim();
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        IReadOnlyList<ChatTurn> turns,
        string briefing,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = new AnthropicClient { ApiKey = options.ResolvedApiKey };

        var messages = new List<BetaMessageParam>();
        foreach (var turn in turns.TakeLast(MaxTurns))
        {
            messages.Add(turn.Role == "assistant"
                ? new BetaMessageParam(new BetaAssistantMessageParam { Content = turn.Content })
                : new BetaMessageParam(new BetaUserMessageParam { Content = turn.Content }));
        }

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 8000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            System = new List<BetaTextBlockParam>
            {
                new() { Text = Instructions, CacheControl = new BetaCacheControlEphemeral() },
                new() { Text = "Briefing of what the app has measured:\n\n" + briefing },
            },
            Messages = messages,
        };

        string? stopReason = null;
        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                yield return text.Text;
            }
            else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
            {
                stopReason = reason.ToString();
            }
        }

        if (stopReason is not null && stopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase))
        {
            yield return "\n\n(The assistant declined to answer this question.)";
        }
    }
}
