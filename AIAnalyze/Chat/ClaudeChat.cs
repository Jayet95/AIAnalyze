using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Helpers;
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

/// <summary>Something the assistant produced while answering: answer text, or a note that it is checking something.</summary>
public abstract record ChatEvent;

public sealed record ChatText(string Text) : ChatEvent;

/// <summary>The assistant called a tool. Label is a short line for the UI ("Searching the Event Log ...").</summary>
public sealed record ChatToolCall(string Name, string Label) : ChatEvent;

/// <summary>
/// Answers questions about the PC with Claude using the user's own API key. The model gets a short
/// briefing and read-only tools over the app's local data, and this class runs the tool loop: each tool
/// call is answered on this PC and only the tool's result goes back to the model.
/// </summary>
public sealed class ClaudeChat(ClaudeOptions options, AssistantTools tools)
{
    public const int MaxTurns = 20;

    /// <summary>Model calls per question. Each round can call several tools at once.</summary>
    public const int MaxSteps = 8;

    public static string Instructions => AssistantTools.Instructions;

    public bool IsConfigured => options.IsConfigured;

    public bool AutoFeedbackEnabled => options.IsConfigured && options.AutoFeedback;

    /// <summary>The question asked on the user's behalf when a session ends with warnings.</summary>
    public string FeedbackQuestion =>
        $"""
        The session the user is looking at has just ended, and the app flagged problems in it. Give the user short
        feedback on it without being asked: what happened, the most likely cause according to the numbers, and the one
        or two things worth doing. Write in {options.FeedbackLanguage}. At most five sentences or a short list.
        """;

    private static readonly IReadOnlyList<BetaToolUnion> ToolParams = AssistantTools.Definitions
        .Select(d => (BetaToolUnion)new BetaTool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = new InputSchema
            {
                Properties = d.InputSchema.TryGetProperty("properties", out var p)
                    ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                    : new Dictionary<string, JsonElement>(),
                Required = d.InputSchema.TryGetProperty("required", out var r)
                    ? r.EnumerateArray().Select(x => x.GetString()!).ToList()
                    : null,
            },
        })
        .ToList();

    public async IAsyncEnumerable<ChatEvent> StreamAnswerAsync(
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

        for (var step = 0; step < MaxSteps; step++)
        {
            var lastStep = step == MaxSteps - 1;
            var parameters = new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 8000,
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                System = new List<BetaTextBlockParam>
                {
                    // Tools and instructions never change: cached across questions. The briefing changes per
                    // question: cached across the tool steps of one question.
                    new() { Text = Instructions, CacheControl = new BetaCacheControlEphemeral() },
                    new() { Text = "Briefing of what the app has measured:\n\n" + briefing, CacheControl = new BetaCacheControlEphemeral() },
                },
                Tools = ToolParams.ToList(),
                // On the last allowed step the model must answer with what it has.
                ToolChoice = lastStep ? new BetaToolChoiceNone() : null,
                Messages = messages,
            };

            var aggregator = new BetaMessageContentAggregator();
            await foreach (var ev in aggregator.CollectAsync(client.Beta.Messages.CreateStreaming(parameters, cancellationToken)))
            {
                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    yield return new ChatText(text.Text);
                }
            }

            var message = aggregator.Message();
            var stopReason = message.StopReason?.ToString() ?? "";
            if (stopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ChatText("\n\n(The assistant declined to answer this question.)");
                yield break;
            }

            var assistantContent = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistantContent.Add(new BetaTextBlockParam { Text = text.Text });
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    assistantContent.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    assistantContent.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistantContent.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    var input = JsonSerializer.SerializeToElement(toolUse.Input);
                    yield return new ChatToolCall(toolUse.Name, AssistantTools.Describe(toolUse.Name, input));
                    var output = await Task.Run(() => tools.Run(toolUse.Name, input), cancellationToken);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = toolUse.ID, Content = output });
                }
            }

            if (results.Count == 0)
            {
                yield break;
            }

            // Separate the text written before the tool calls from the answer that follows them.
            if (assistantContent.Any(b => b.TryPickText(out _)))
            {
                yield return new ChatText("\n\n");
            }
            messages.Add(new BetaMessageParam(new BetaAssistantMessageParam { Content = assistantContent }));
            messages.Add(new BetaMessageParam(new BetaUserMessageParam { Content = results }));
        }
    }
}
