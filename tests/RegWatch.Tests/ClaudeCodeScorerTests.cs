using System.Text.Json;
using RegWatch.Core;

namespace RegWatch.Tests;

public class ClaudeCodeScorerTests
{
    private const string Result = """{"headline":"h","notes":["n"],"items":[{"cite":"Notice 2026-61","source":"IRB 2026-43","type":"Notice","irc":"§ 871(m)","comment_deadline":null,"url":"u","score":2,"buckets":["fatca_withholding"],"plain_english":"p","why_you_care":"w"}]}""";

    [Fact]
    public void Parse_ReadsStructuredOutput()
    {
        var envelope = $$"""{"type":"result","is_error":false,"num_turns":2,"duration_ms":91000,"result":"done","structured_output":{{Result}}}""";

        var parsed = ClaudeCodeScorer.Parse(envelope, "");

        var item = Assert.Single(parsed.Items);
        Assert.Equal("Notice 2026-61", item.Cite);
        Assert.Equal(2, item.Score);
    }

    [Fact]
    public void Parse_FallsBackToFencedTextResult()
    {
        var envelope = JsonSerializer.Serialize(new { type = "result", is_error = false, result = "```json\n" + Result + "\n```" });
        Assert.Equal("h", ClaudeCodeScorer.Parse(envelope, "").Headline);
    }

    [Fact]
    public void Parse_ExplainsExpiredSignIn()
    {
        // The exact envelope the CLI printed on this machine on 2026-10-28.
        const string envelope = """{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired and could not be refreshed"}""";

        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCodeScorer.Parse(envelope, ""));
        Assert.Contains("claude auth login", ex.Message);
    }

    [Fact]
    public void Parse_NonJsonOutput_SurfacesStderr()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCodeScorer.Parse("", "error: unknown option"));
        Assert.Contains("unknown option", ex.Message);
    }

    [Fact]
    public void Schema_IsSharedByBothScorers()
    {
        using var doc = JsonDocument.Parse(ScoringPrompt.SchemaJson);
        Assert.Equal(ScoringPrompt.Schema.Keys.OrderBy(k => k), doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k));
    }
}
