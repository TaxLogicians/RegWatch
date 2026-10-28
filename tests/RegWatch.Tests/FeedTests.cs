using System.Net;
using System.Text.Json.Nodes;
using RegWatch.Core;

namespace RegWatch.Tests;

public sealed class FeedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("regwatch-feeds").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ---- Parsing ---------------------------------------------------------------------------

    [Fact]
    public void Parse_RealTaxAdviserFeed()
    {
        var items = FeedClient.Parse(Fixture("feed-taxadviser.xml"), "The Tax Adviser");

        Assert.Equal(20, items.Count);
        var first = items[0];
        Assert.Equal("“Ghost preparers” tied to $179 million in potentially fraudulent refunds", first.Title);
        Assert.StartsWith("https://www.thetaxadviser.com/", first.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 20, 12, 56, TimeSpan.Zero), first.Published!.Value.ToUniversalTime());
        Assert.Contains("PRACTICE & PROCEDURES", first.Categories);
        Assert.InRange(first.Summary.Length, 50, FeedClient.SummaryLimit + 1);
        Assert.DoesNotContain("<", first.Summary);
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Id)));
    }

    [Fact]
    public void Parse_Atom()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Blog</title>
              <entry><id>tag:x,2026:1</id><title>Notice 2026-61 explained</title>
                <link rel="alternate" href="https://blog.example/n61"/><published>2026-10-27T09:00:00Z</published>
                <category term="International"/><summary type="html">&lt;p&gt;Dividend &amp;amp; equivalents&lt;/p&gt;</summary></entry>
            </feed>
            """;

        var item = Assert.Single(FeedClient.Parse(atom, "Blog"));
        Assert.Equal("https://blog.example/n61", item.Url);
        Assert.Equal("tag:x,2026:1", item.Id);
        Assert.Equal(["International"], item.Categories);
        Assert.Equal("Dividend & equivalents", item.Summary);
    }

    [Fact]
    public async Task GetItems_FiltersJofAToTaxCategory()
    {
        var client = new FeedClient(new HttpClient(new Fake(_ => (HttpStatusCode.OK, Fixture("feed-jofa.xml")))));
        var jofa = FeedSource.Defaults.Single(f => f.Name == "Journal of Accountancy");

        var items = await client.GetItemsAsync(jofa, since: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Contains("TAX", i.Categories));
        Assert.DoesNotContain(items, i => i.Title.Contains("PCAOB"));
    }

    [Fact]
    public async Task GetItems_StopsAtBookmark_AndPagesWordPressFeedsBackwards()
    {
        var requested = new List<string>();
        var page1 = FeedClient.Parse(Fixture("feed-taxadviser.xml"), "x");
        var handler = new Fake(req =>
        {
            requested.Add(req.RequestUri!.ToString());
            return req.RequestUri!.Query.Contains("paged=2")
                ? (HttpStatusCode.OK, Rss(("gap-1", "Between the runs", "Mon, 07 Sep 2026 10:00:00 +0000"), ("old-1", "Before the bookmark", "Sun, 30 Aug 2026 10:00:00 +0000")))
                : (HttpStatusCode.OK, Fixture("feed-taxadviser.xml"));
        });
        var client = new FeedClient(new HttpClient(handler));
        var feed = FeedSource.Defaults[0];

        // Bookmark inside page 1: one request, only newer items.
        var recent = await client.GetItemsAsync(feed, since: new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        Assert.Single(requested);
        Assert.All(recent, i => Assert.True(i.Published > new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)));

        // Bookmark older than page 1 (a long gap between runs): reads page 2 as well.
        requested.Clear();
        var all = await client.GetItemsAsync(feed, since: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(["https://www.thetaxadviser.com/feed/", "https://www.thetaxadviser.com/feed/?paged=2"], requested);
        Assert.Equal(page1.Count + 1, all.Count); // the 30 Aug item is older than the bookmark
        Assert.Contains(all, i => i.Title == "Between the runs");
    }

    [Fact]
    public void MergeDuplicates_KeepsOneCopyOfSharedAicpaStories()
    {
        var tta = FeedClient.Parse(Fixture("feed-taxadviser.xml"), "The Tax Adviser");
        var jofa = FeedClient.Parse(Fixture("feed-jofa.xml"), "Journal of Accountancy");

        var merged = FeedClient.MergeDuplicates(tta.Concat(jofa));

        var ghost = Assert.Single(merged, i => i.Title.Contains("Ghost preparers"));
        Assert.Equal("The Tax Adviser / Journal of Accountancy", ghost.Source);
        Assert.Equal(2, ghost.Id.Split('\u0001').Length);
        Assert.True(merged.Count < tta.Count + jofa.Count);
    }

    // ---- Discovery -------------------------------------------------------------------------

    [Fact]
    public async Task Discover_FollowsPageAlternateLink()
    {
        var handler = new Fake(req => req.RequestUri!.AbsolutePath switch
        {
            "/" => (HttpStatusCode.OK, """<html><head><link rel="alternate" type="application/rss+xml" title="News" href="/news/rss.xml"></head></html>"""),
            "/news/rss.xml" => (HttpStatusCode.OK, Rss(("a", "Item A", "Tue, 27 Oct 2026 10:00:00 GMT"))),
            _ => (HttpStatusCode.NotFound, ""),
        });

        var found = await new FeedClient(new HttpClient(handler)).DiscoverAsync("example.org");

        Assert.Equal("https://example.org/news/rss.xml", found.FeedUrl);
        Assert.Equal("Test feed", found.Title);
        Assert.Single(found.Sample);
        Assert.NotNull(found.Sample[0].Published); // "GMT" zone parsed
    }

    [Fact]
    public async Task Discover_FallsBackToWordPressFeedPath()
    {
        var handler = new Fake(req => req.RequestUri!.AbsolutePath switch
        {
            "/" => (HttpStatusCode.OK, "<html><head><title>No feed link</title></head></html>"),
            "/feed/" => (HttpStatusCode.OK, Rss(("a", "Item A", "Tue, 27 Oct 2026 10:00:00 +0000"))),
            _ => (HttpStatusCode.NotFound, ""),
        });

        var found = await new FeedClient(new HttpClient(handler)).DiscoverAsync("https://blog.example/");
        Assert.Equal("https://blog.example/feed/", found.FeedUrl);
    }

    [Fact]
    public async Task Discover_ExplainsBlockedSites()
    {
        var handler = new Fake(_ => (HttpStatusCode.Forbidden, "denied"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FeedClient(new HttpClient(handler)).DiscoverAsync("https://www.taxnotes.com"));
        Assert.Contains("blocks automated readers", ex.Message);
    }

    // ---- State, digest, batching -----------------------------------------------------------

    [Fact]
    public void State_TracksFeedBookmarksAndSeenItems()
    {
        var path = Path.Combine(_dir, "STATE.json");
        var store = new StateStore(path);
        var state = store.Load();

        state.SetFeedBookmark("https://f/feed/", new DateTimeOffset(2026, 10, 23, 20, 10, 42, TimeSpan.Zero));
        state.SetFeedBookmark("https://f/feed/", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)); // never moves back
        state.AddSeenFeedItems(["a", "b"]);
        store.Save(state);

        var reloaded = store.Load();
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 20, 10, 42, TimeSpan.Zero), reloaded.FeedBookmark("https://f/feed/"));
        Assert.Null(reloaded.FeedBookmark("https://other/feed/"));
        Assert.Equal(new HashSet<string> { "a", "b" }, reloaded.SeenFeedItems);
    }

    [Fact]
    public void Digest_HasPressSection_LinkedToGuidanceCards()
    {
        var result = new ScoringResult
        {
            Headline = "h",
            Items =
            [
                new() { Cite = "T.D. 10060", Source = "FR 2026-21267", Type = "Final rule", Url = "https://fr/1", Score = 1, Buckets = ["penalties_relief_intl"] },
                new() { Cite = "FR 2026-21944", Source = "FR 2026-21944", Url = "https://fr/2", Score = 0, WhyYouCare = "OFAC" },
            ],
            Press =
            [
                new() { Title = "Final regulations clear path for direct IRS card payments", Source = "The Tax Adviser", Url = "https://tta/1", Published = "2026-10-16", Score = 1, Buckets = ["penalties_relief_intl"], RelatedCite = "TD 10060", WhyYouCare = "w1" },
                new() { Title = "Notice 2026-61 explained", Source = "Blog", Url = "https://blog/1", Score = 2, RelatedCite = "Notice 2026-61", WhyYouCare = "w2" },
                new() { Title = "PCAOB inspection data", Source = "Journal of Accountancy", Url = "https://jofa/1", Score = 0, WhyYouCare = "audit" },
            ],
        };

        var md = DigestWriter.Render("2026-10-28", "w", result, []);

        Assert.Contains("**Also covered by:** [The Tax Adviser: Final regulations clear path for direct IRS card payments](https://tta/1)", md);
        Assert.Contains("## In the press — 2 relevant article(s)", md);
        Assert.Contains("on TD 10060 (card above)", md);
        Assert.Contains("on Notice 2026-61 (not in this digest)", md);
        Assert.Contains("**Not relevant to the profile (1):** [PCAOB inspection data](https://jofa/1)", md);
        Assert.True(md.IndexOf("## In the press", StringComparison.Ordinal) < md.IndexOf("## Skip", StringComparison.Ordinal));

        // Press entries must not be mistaken for guidance cards by the viewer.
        Assert.Equal(["T.D. 10060"], DigestIndex.Cards(md).Select(c => c.Cite));
    }

    [Theory]
    [InlineData("TD 10060", "T.D. 10060", "FR 2026-21267", true)]
    [InlineData("2026-21267", "T.D. 10060", "FR 2026-21267", true)]
    [InlineData("REG-115646-25", "REG-115646-25", "IRB 2026-44", true)]
    [InlineData("Notice 2026-6", "Notice 2026-61", "IRB 2026-43", false)]
    [InlineData("", "T.D. 10060", "FR 1", false)]
    public void Covers_MatchesCiteVariants(string related, string cite, string source, bool expected) =>
        Assert.Equal(expected, DigestWriter.Covers(new PressItem { RelatedCite = related }, new ScoredItem { Cite = cite, Source = source }));

    [Fact]
    public void Batching_SendsCommentaryInItsOwnRequests()
    {
        var articles = Enumerable.Range(0, 12).Select(i => new FeedItem("S", $"id{i}", $"t{i}", $"u{i}", null, [], "")).ToList();
        var fetched = new FetchResult([], [], [], articles);

        var batches = ScoringBatches.Split(fetched, 5);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Empty(b.FedRegDocuments));
        Assert.Equal(12, batches.Sum(b => b.CommentaryItems.Count));
    }

    [Fact]
    public void Schema_AndPayload_IncludeCommentary()
    {
        var schema = JsonNode.Parse(ScoringPrompt.SchemaJson)!;
        Assert.Contains("press", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var payload = JsonNode.Parse(ScoringPrompt.BuildPayload(new FetchResult([], [], [],
            [new FeedItem("The Tax Adviser", "1", "Title", "https://u", new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), ["TAX"], "Summary")])))!;
        Assert.Equal("2026-10-23", payload["commentary"]![0]!["published"]!.GetValue<string>());

        // Answers scored before feeds existed have no "press" array.
        Assert.Empty(ScoringPrompt.ParseResult("""{"headline":"h","notes":[],"items":[]}""").Press);
    }

    [Fact]
    public void Settings_WithoutFeeds_UseBuiltInDefaults()
    {
        var settings = new AppSettings();
        Assert.Equal(["The Tax Adviser", "Journal of Accountancy"], settings.EffectiveFeeds.Select(f => f.Name));
        Assert.Equal(["TAX"], settings.EffectiveFeeds[1].Categories);
    }

    private static string Rss(params (string Id, string Title, string Date)[] items) =>
        "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>Test feed</title>" +
        string.Concat(items.Select(i => $"<item><guid>{i.Id}</guid><title>{i.Title}</title><link>https://x/{i.Id}</link><pubDate>{i.Date}</pubDate></item>")) +
        "</channel></rss>";

    private sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body), RequestMessage = request });
        }
    }
}
