using System.Text.Json.Nodes;
using RegWatch.Core;

namespace RegWatch.Tests;

public sealed class DigestAndStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("regwatch-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ScoringResult Sample() => new()
    {
        Headline = "One score-3 item: Notice 2026-99.",
        Notes = ["Merged T.D. 10060 across IRB and FR."],
        Items =
        [
            new() { Cite = "Notice 2026-99", Source = "IRB 2026-45, Part III", Type = "Notice", Irc = "§ 6048", Url = "https://www.irs.gov/irb/2026-45_irb", Score = 3, Buckets = ["foreign_trusts"], PlainEnglish = "p", WhyYouCare = "w" },
            new() { Cite = "REG-123456-26", Source = "FR 2026-22000", Type = "Proposed Rule", Irc = "§ 6038D", CommentDeadline = "2099-01-15", Url = "https://www.federalregister.gov/d/2026-22000", Score = 2, Buckets = ["intl_info_returns"], PlainEnglish = "p", WhyYouCare = "w" },
            new() { Cite = "FR 2026-22001", Source = "FR 2026-22001", Type = "Notice", Url = "https://www.federalregister.gov/d/2026-22001", Score = 0, WhyYouCare = "OFAC sanctions list" },
        ],
    };

    [Fact]
    public void Render_UsesCardFormat_AndSkipList()
    {
        var md = DigestWriter.Render("2026-11-02", "IRB 2026-45", Sample(), ["IRB 2026-46: 503"]);

        Assert.Contains("# RegWatch Digest — 2026-11-02", md);
        Assert.Contains("### 🔴 Notice 2026-99 — relevance 3 (foreign_trusts)", md);
        Assert.Contains("### 🟡 REG-123456-26 — relevance 2 (intl_info_returns)", md);
        Assert.Contains("**Comment deadline:** 2099-01-15", md);
        Assert.Contains("## Skip (score 0) — 1 items", md);
        Assert.Contains("**Fetch problem:** IRB 2026-46: 503", md);
        Assert.DoesNotContain("Nothing scores 2 or higher", md);
    }

    [Fact]
    public void Render_SaysPlainlyWhenNothingScoresTwo()
    {
        var md = DigestWriter.Render("2026-11-02", "w", new ScoringResult { Headline = "h" }, []);
        Assert.Contains("Nothing scores 2 or higher this run.", md);
    }

    [Fact]
    public void Write_DoesNotOverwriteSameDayDigest()
    {
        var first = DigestWriter.Write(_dir, "2026-11-02", "a", Sample());
        var second = DigestWriter.Write(_dir, "2026-11-02", "b", Sample());

        Assert.EndsWith("2026-11-02.md", first);
        Assert.EndsWith("2026-11-02_2.md", second);
        Assert.True(File.Exists(Path.Combine(_dir, "2026-11-02_2.json")));
    }

    [Fact]
    public void Index_ReadsAppDigests_RoundTrip()
    {
        var md = DigestWriter.Render("2026-11-02", "w", Sample(), []);
        DigestWriter.Write(_dir, "2026-11-02", md, Sample());

        var summary = Assert.Single(DigestIndex.List(_dir));
        Assert.Equal((1, 1, 0), (summary.Score3, summary.Score2, summary.Score1));
        Assert.Equal("One score-3 item: Notice 2026-99.", summary.Headline);

        var deadline = Assert.Single(DigestIndex.UpcomingDeadlines(_dir, new DateOnly(2026, 11, 2)));
        Assert.Equal("REG-123456-26", deadline.Cite);
        Assert.Equal(new DateOnly(2099, 1, 15), deadline.Deadline);
    }

    [Fact]
    public void Index_ReadsAgentWrittenDigests()
    {
        // The 2026-10-25 digest written by the scheduled agent, before the app existed.
        var agentDigest = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "digests", "2026-10-25.md");
        if (!File.Exists(agentDigest)) return;

        var cards = DigestIndex.Cards(File.ReadAllText(agentDigest)).ToList();

        Assert.Contains(cards, c => c.Cite == "Notice 2026-61" && c.Score == 2);
        Assert.Contains(cards, c => c.Cite.StartsWith("FR 2026-20743") && c.Deadline == new DateOnly(2026, 12, 8));
    }

    [Fact]
    public void State_PreservesUnknownFields_AndTracksSeen()
    {
        var path = Path.Combine(_dir, "STATE.json");
        File.WriteAllText(path, """{"last_irb_issue":"2026-44","last_fedreg_date":"2026-10-25","notes":"keep me"}""");
        var store = new StateStore(path);

        var state = store.Load();
        Assert.Equal("2026-44", state.LastIrbIssue);
        state.LastIrbIssue = "2026-45";
        state.AddSeen(["2026-1", "2026-2"]);
        state.AddSeen(["2026-2", "2026-3"]);
        store.Save(state);

        var reloaded = store.Load();
        Assert.Equal("2026-45", reloaded.LastIrbIssue);
        Assert.Equal("keep me", reloaded.Raw["notes"]!.GetValue<string>());
        Assert.Equal(new HashSet<string> { "2026-1", "2026-2", "2026-3" }, reloaded.SeenDocuments);
    }

    [Fact]
    public void ParseResult_ClampsScores()
    {
        var json = new JsonObject
        {
            ["headline"] = "h",
            ["notes"] = new JsonArray(),
            ["items"] = new JsonArray(new JsonObject { ["cite"] = "X", ["score"] = 7 }),
        }.ToJsonString();

        Assert.Equal(3, ClaudeScorer.ParseResult(json).Items[0].Score);
    }
}
