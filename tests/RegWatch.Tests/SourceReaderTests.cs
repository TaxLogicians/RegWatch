using System.Net;
using RegWatch.Core;

namespace RegWatch.Tests;

/// <summary>The page readers for IRS letter rulings, Tax Court opinions and Treasury press releases.</summary>
public class SourceReaderTests
{
    private const string Profile = """
        ## Topic buckets

        - **foreign_trusts (3)** — §679, §672(f), §6048, §665–668, Form 3520, foreign grantor trust
        - **intl_info_returns (3)** — Form 5471, 8865, §6038, §1.6038-4
        - **treaties (2)** — tax treaty, §894
        - **estate_trust_valuation (1)** — §7520 rate, AFR

        ## False-positive guards (do NOT score up on the term alone)

        - "nonbank trustee" → IRA, not a foreign trust → **0**
        """;

    private static readonly ProfileTerms Core = ProfileTerms.From(Profile);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ---- Profile terms

    [Fact]
    public void ProfileTerms_TakeSectionsAndTermsFromBucketsWeightedTwoOrMore()
    {
        Assert.Equal(["679", "672(f)", "6048", "665", "666", "667", "668", "6038", "894"], Core.SectionRefs);
        Assert.Contains("672", Core.Sections);
        Assert.DoesNotContain("7520", Core.Sections); // weight 1 is not core
        Assert.Equal(["Form 3520", "foreign grantor trust", "Form 5471", "Form 8865", "tax treaty"], Core.Phrases);
    }

    [Theory]
    [InlineData("Section 2010(c)(5)(A); Portability Election", "2010(c)")]
    [InlineData("Relief Under IRC 2642(g)(1)", "2642(g)")]
    [InlineData("§ 6039F", "6039F")]
    [InlineData("secs. 6048 and 6677", "6048")]
    public void ProfileTerms_FindSectionReferences(string text, string expected) =>
        Assert.Equal(expected, ProfileTerms.SectionsIn(text)[0]);

    [Fact]
    public void ProfileTerms_MatchSectionsAndWholeTerms()
    {
        Assert.True(Core.Matches("United States, Croatia Sign Protocol to Income Tax Treaty"));
        Assert.True(Core.Matches("Penalties under section 6048(b)"));
        Assert.False(Core.Matches("Late Form 3520-A filings")); // Form 3520-A is a term of its own
        Assert.False(Core.Matches("Treasury Sanctions Facilitators of DPRK IT Worker Fraud"));
        Assert.False(Core.Matches("Section 7520 rates for November")); // not core
    }

    // ---- IRS written determinations

    [Fact]
    public void WrittenDeterminations_MergeRowsPerRuling()
    {
        var items = WrittenDeterminationsClient.Parse(Fixture("written-determinations.html"), "IRS letter rulings");

        Assert.Equal(5, items.Count);
        var multi = items.Single(i => i.Id == "WD 202642007"); // listed under two UIL codes
        Assert.Contains("2010", multi.Categories);
        Assert.Contains("9100", multi.Categories);
        Assert.Equal("https://www.irs.gov/pub/irs-wd/202642007.pdf", multi.Url);
        Assert.Equal(new DateTime(2026, 10, 16), multi.Published!.Value.Date);
        Assert.StartsWith("Written determination 202642007: Portability Election", multi.Title);
    }

    [Fact]
    public void WrittenDeterminations_KeepOnlyCoreSections()
    {
        var items = WrittenDeterminationsClient.Parse(Fixture("written-determinations.html"), "IRS letter rulings");

        var kept = WrittenDeterminationsClient.Filter(items, Core);

        Assert.Equal(["WD 202640001"], kept.Select(i => i.Id));
        Assert.Contains("6048", kept[0].Categories);
    }

    [Fact]
    public async Task WrittenDeterminations_ReadThroughFeedClient_SinceTheBookmark()
    {
        var asked = new List<string>();
        var client = new FeedClient(new HttpClient(new Route(url =>
        {
            asked.Add(url);
            return Fixture("written-determinations.html");
        })))
        { Core = Core };
        var source = FeedSource.Defaults.Single(f => f.Url == WrittenDeterminationsClient.ListUrl);

        var recent = await client.GetItemsAsync(source, since: new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        var older = await client.GetItemsAsync(source, since: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(recent); // the core ruling was released 10/02
        Assert.Single(older);
        // The first read stopped on page 1, which reached the bookmark; the second paged on to the limit.
        Assert.Equal("https://www.irs.gov/written-determinations?items_per_page=100", asked[0]);
        Assert.Equal(1 + WrittenDeterminationsClient.MaxPages, asked.Count);
        Assert.EndsWith("&page=4", asked[^1]);
    }

    // ---- Tax Court

    [Fact]
    public void TaxCourt_KeywordQuotesSectionsBothWaysAndTerms()
    {
        var keyword = TaxCourtClient.Keyword(Core);

        Assert.StartsWith("\"section 679\" | \"sec. 679\" | \"section 672(f)\" | \"sec. 672(f)\"", keyword);
        Assert.EndsWith("\"Form 3520\" | \"foreign grantor trust\" | \"Form 5471\" | \"Form 8865\" | \"tax treaty\"", keyword);
        Assert.Contains("startDate=10%2F01%2F2026&endDate=10%2F28%2F2026",
            TaxCourtClient.QueryUrl(Core, new DateTime(2026, 10, 1), new DateTime(2026, 10, 28)));
    }

    [Fact]
    public void TaxCourt_ParsesOpinions()
    {
        var items = TaxCourtClient.Parse(Fixture("tax-court-opinions.json"), "Tax Court opinions");

        Assert.Equal(3, items.Count);
        var first = items[0];
        Assert.Equal("Raju J. Mukhi, Petitioner — T.C. Opinion, docket 4329-22L", first.Title);
        Assert.Equal("https://dawson.ustaxcourt.gov/case-detail/4329-22", first.Url);
        Assert.Equal(new DateTime(2024, 4, 8), first.Published!.Value.UtcDateTime.Date);
        Assert.StartsWith("T.C. Opinion Judge Greaves 162 T.C. No. 8", first.Summary);
        Assert.DoesNotContain("(Judge Greaves)", first.Summary); // already in the title
    }

    [Fact]
    public async Task TaxCourt_WithNoCoreTerms_AsksNothing()
    {
        var called = false;
        using var http = new HttpClient(new Route(_ => { called = true; return "{}"; }));

        var items = await TaxCourtClient.GetItemsAsync(http, "Tax Court opinions", DateTimeOffset.UtcNow.AddDays(-7), DateTime.Today, ProfileTerms.Empty);

        Assert.Empty(items);
        Assert.False(called);
    }

    // ---- Treasury

    [Fact]
    public void Treasury_KeepsTaxHeadlinesOnly()
    {
        var items = TreasuryPressClient.Parse(Fixture("treasury-press.json"), "Treasury press releases");

        Assert.Equal(5, items.Count);
        Assert.Equal("https://home.treasury.gov/news/press-releases/sb0669/", items[0].Url);
        Assert.Equal(["Readouts"], items[1].Categories);

        var kept = TreasuryPressClient.Filter(items, Core).Select(i => i.Title).ToList();
        Assert.Equal(3, kept.Count);
        Assert.Contains(kept, t => t.Contains("Beneficial Ownership"));
        Assert.Contains(kept, t => t.Contains("Section 892"));
        Assert.Contains(kept, t => t.Contains("Croatia"));
    }

    [Fact]
    public async Task Treasury_ReadsLastYearToo_WhenTheBookmarkIsInLastYear()
    {
        var asked = new List<string>();
        using var http = new HttpClient(new Route(url => { asked.Add(url); return Fixture("treasury-press.json"); }));

        await TreasuryPressClient.GetItemsAsync(http, "Treasury", new DateTimeOffset(2026, 12, 28, 0, 0, 0, TimeSpan.Zero),
            new DateTime(2027, 1, 4), Core);

        Assert.Equal([TreasuryPressClient.YearUrl(2027), TreasuryPressClient.YearUrl(2026)], asked);
    }

    private sealed class Route(Func<string, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(request.RequestUri!.ToString())) });
    }
}
