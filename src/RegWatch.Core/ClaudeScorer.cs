using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace RegWatch.Core;

/// <summary>
/// Scores fetched items against PROFILE.md with one Claude call. Claude does the judgment
/// (bucket match, guards, plain-English summary); the app does fetching, dedup and layout.
/// </summary>
public sealed class ClaudeScorer(AnthropicClient client)
{
    public const string Model = "claude-opus-5-5";

    private static readonly JsonSerializerOptions PayloadOptions = new() { WriteIndented = false };

    public async Task<ScoringResult> ScoreAsync(
        string profile, FetchResult fetched, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var payload = BuildPayload(fetched);
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
                Format = new BetaJsonOutputFormat { Schema = Schema.Value },
            },
            System = SystemPrompt(profile),
            Tools = useWebSearch ? [new BetaToolUnion(new BetaWebSearchTool20260209 { MaxUses = 5 })] : null,
            Messages = [new BetaUserMessageParam { Content = payload }],
        };

        log?.Report($"Scoring with {Model}{(useWebSearch ? " (web search on)" : "")}…");
        var message = await client.Beta.Messages.CreateStreaming(parameters, ct).Aggregate();
        log?.Report($"Scored: {message.Usage.InputTokens:N0} input / {message.Usage.OutputTokens:N0} output tokens.");

        var stop = message.StopReason?.ToString();
        if (stop is "refusal")
            throw new InvalidOperationException("Claude declined to score this batch (refusal). Try again or run the agent playbook.");
        if (stop is "max_tokens")
            throw new InvalidOperationException("Scoring output hit max_tokens before the JSON finished.");
        if (stop is "pause_turn")
            throw new InvalidOperationException("Scoring paused mid-search (pause_turn); retry the run.");

        return ParseResult(FinalText(message));
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

    public static ScoringResult ParseResult(string json)
    {
        var result = JsonSerializer.Deserialize<ScoringResult>(json)
            ?? throw new JsonException("Scorer returned empty JSON.");
        // The schema bounds the score, but clamp anyway: a 4 would break digest sections.
        return result with { Items = result.Items.Select(i => i with { Score = Math.Clamp(i.Score, 0, 3) }).ToList() };
    }

    public static string BuildPayload(FetchResult fetched) => JsonSerializer.Serialize(new
    {
        irb_issues = fetched.IrbIssues.Select(i => new { issue = i.Issue, url = i.Url, contents = i.Contents, highlights = i.Highlights }),
        federal_register = fetched.FedRegDocuments,
        fetch_problems = fetched.Problems,
    }, PayloadOptions);

    private static string SystemPrompt(string profile) => $"""
        You score new IRS / Treasury guidance for a weekly triage digest used by a US
        foreign-trust and international tax practice. The user message is JSON with
        Internal Revenue Bulletin issues (table of contents + "Highlights of This Issue"
        synopses) and Federal Register documents (IRS, Treasury, FinCEN).

        Return one entry in "items" for every distinct guidance item:
        - IRB: every item in each issue's contents/highlights (Rev. Rul., Rev. Proc.,
          Notice, T.D., REG-, Announcement, AOD). source = "IRB <issue>, Part <n>" when the
          part is known; url = the issue url.
        - Federal Register: every document; source = "FR <document_number>"; url = html_url;
          comment_deadline = comments_close_on, or the DATES deadline if you know it,
          else null.
        - The same item often appears in both sources (a T.D. or REG- number in an IRB issue
          and in the Federal Register). Merge those into one entry, prefer the Federal
          Register url, and keep both references in source.
        - Treasury documents with no tax angle (OFAC sanctions lists, Mint, Fiscal Service,
          bureau meetings) still get an entry, scored 0 with a ~5-word why_you_care.

        Score with the profile below: apply the false-positive guards first, then the
        highest-matching bucket, minus 1 for incidental mentions. buckets lists the matched
        bucket keys (empty for score 0).

        Before scoring any Treasury or FinCEN *final rule* 0 or 1 on its abstract alone,
        when it touches ownership transparency, AML or entity registration, run one web
        search for news coverage of its document number: abstracts can describe only part
        of a multi-part holding. Record any such check in notes.

        Rules: these summaries are triage, not authority. Never invent a cite, date or IRC
        section; write "not stated" when the source does not say. plain_english is one
        sentence; why_you_care names the bucket and the reason. headline is one or two
        sentences: the count of score-3 and score-2 items and the single most important
        item, or plainly "Nothing scores 2 or higher this run." notes lists anything the
        reader should know about coverage (a fetch problem, an item you could not identify,
        a merge you were unsure of).

        <profile>
        {profile}
        </profile>
        """;

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Schema = new(() =>
    {
        var item = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "cite", "source", "type", "irc", "comment_deadline", "url", "score", "buckets", "plain_english", "why_you_care" },
            properties = new Dictionary<string, object>
            {
                ["cite"] = new { type = "string" },
                ["source"] = new { type = "string" },
                ["type"] = new { type = "string" },
                ["irc"] = new { type = "string" },
                ["comment_deadline"] = new { type = new[] { "string", "null" }, description = "YYYY-MM-DD" },
                ["url"] = new { type = "string" },
                ["score"] = new { type = "integer", @enum = new[] { 0, 1, 2, 3 } },
                ["buckets"] = new { type = "array", items = new { type = "string" } },
                ["plain_english"] = new { type = "string" },
                ["why_you_care"] = new { type = "string" },
            },
        };
        var root = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "headline", "notes", "items" },
            ["properties"] = new Dictionary<string, object>
            {
                ["headline"] = new { type = "string" },
                ["notes"] = new { type = "array", items = new { type = "string" } },
                ["items"] = new { type = "array", items = item },
            },
        };
        return root.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
    });
}
