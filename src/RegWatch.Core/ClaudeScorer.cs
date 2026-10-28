using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace RegWatch.Core;

/// <summary>
/// Scores fetched items with one Claude API call, billed to an Anthropic API key. Claude does
/// the judgment (bucket match, guards, plain-English summary); the app does fetching, dedup
/// and layout.
/// </summary>
public sealed class ClaudeScorer(AnthropicClient client) : IScorer
{
    public const string Model = "claude-opus-5-5";

    public string Description => "Anthropic API key";

    public async Task<ScoringResult> ScoreAsync(
        string profile, FetchResult fetched, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var payload = ScoringPrompt.BuildPayload(fetched);
        try
        {
            return await CallAsync(profile, payload, useWebSearch: true, log, ct);
        }
        catch (AnthropicBadRequestException ex)
        {
            // Web search can be disabled at the org level; score from the fetched text alone.
            log?.Report($"Web search unavailable ({ex.Message}); retrying without it.");
            var result = await CallAsync(profile, payload, useWebSearch: false, log, ct);
            return result with { Notes = [.. result.Notes, "Final-rule news check skipped: web search unavailable."] };
        }
    }

    private async Task<ScoringResult> CallAsync(
        string profile, string payload, bool useWebSearch, IProgress<string>? log, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 64000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.High,
                Format = new BetaJsonOutputFormat { Schema = ScoringPrompt.Schema },
            },
            System = ScoringPrompt.SystemPrompt(profile),
            Tools = useWebSearch ? [new BetaToolUnion(new BetaWebSearchTool20260209 { MaxUses = 5 })] : null,
            Messages = [new BetaUserMessageParam { Content = payload }],
        };

        log?.Report($"Scoring with {Model} via API{(useWebSearch ? " (web search on)" : "")}…");
        var message = await client.Beta.Messages.CreateStreaming(parameters, ct).Aggregate();
        log?.Report($"Scored: {message.Usage.InputTokens:N0} input / {message.Usage.OutputTokens:N0} output tokens.");

        var stop = message.StopReason?.ToString();
        if (stop is "refusal")
            throw new InvalidOperationException("Claude declined to score this batch (refusal). Try again or run the agent playbook.");
        if (stop is "max_tokens")
            throw new InvalidOperationException("Scoring output hit max_tokens before the JSON finished.");
        if (stop is "pause_turn")
            throw new InvalidOperationException("Scoring paused mid-search (pause_turn); retry the run.");

        return ScoringPrompt.ParseResult(FinalText(message));
    }

    /// <summary>The structured answer is the text written after the last web-search result.</summary>
    private static string FinalText(BetaMessage message)
    {
        var sb = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickWebSearchToolResult(out _) || block.TryPickServerToolUse(out _))
                sb.Clear();
            else if (block.TryPickText(out var text))
                sb.Append(text.Text);
        }
        return sb.ToString();
    }
}
