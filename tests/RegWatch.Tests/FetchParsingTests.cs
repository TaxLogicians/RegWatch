using RegWatch.Core;

namespace RegWatch.Tests;

public class FetchParsingTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void IrbIndex_ListsIssuesInOrder_HandlingUpperCaseSuffix()
    {
        var issues = IrbClient.ParseIssueList(Fixture("irb-index.html"));

        // 2026-41 and 2026-44 are linked as "_IRB" on the live index.
        Assert.Contains("2026-41", issues);
        Assert.Contains("2026-44", issues);
        Assert.Equal(issues.OrderBy(i => i, IssueComparer.Instance), issues);
        Assert.Equal(issues.Distinct().Count(), issues.Count);
    }

    [Fact]
    public void IrbIssue_ExtractsContentsAndHighlights()
    {
        var (contents, highlights) = IrbClient.ExtractHighlights(Fixture("irb-2026-44.html"));

        Assert.Contains("Notice 2026-63", contents);
        Assert.StartsWith("HIGHLIGHTS OF THIS ISSUE", highlights);
        Assert.Contains("Rev. Proc. 2026-31", highlights);
        Assert.Contains("T.D.10055", highlights);
        Assert.DoesNotContain("The IRS Mission", highlights);
        // A few KB, not the 400k+ page.
        Assert.InRange(highlights.Length, 1_000, 20_000);
    }

    [Fact]
    public void IrbIssue_WithoutMarker_FallsBackToTruncatedText()
    {
        var (contents, highlights) = IrbClient.ExtractHighlights("<html><body><p>" + new string('x', 30_000) + "</p></body></html>");
        Assert.Equal("", contents);
        Assert.Equal(20_000, highlights.Length);
    }

    [Theory]
    [InlineData("2026-9", "2026-10", -1)]
    [InlineData("2026-44", "2026-44", 0)]
    [InlineData("2027-01", "2026-52", 1)]
    public void IssueComparer_IsNumeric(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(IssueComparer.Instance.Compare(a, b)));

    [Fact]
    public void FederalRegisterPage_ParsesDocumentsAndCursor()
    {
        var (docs, next) = FederalRegisterClient.ParsePage(Fixture("fr-page.json"), "internal-revenue-service");

        Assert.Equal(3, docs.Count);
        Assert.All(docs, d => Assert.False(string.IsNullOrEmpty(d.DocumentNumber)));
        Assert.Contains(docs, d => d.DocumentNumber == "2026-21966" && d.CommentsCloseOn is null);
        Assert.NotNull(next);
        Assert.Contains("page=2", next);
    }

    [Fact]
    public void FederalRegisterDedup_MergesAgencies()
    {
        var a = new FedRegDocument("2026-1", "internal-revenue-service", "Rule", "t", null, null, "2026-10-01", null, null, "u", []);
        var b = a with { Agency = "treasury-department" };
        var c = a with { DocumentNumber = "2026-2", PublicationDate = "2026-10-02" };

        var merged = FederalRegisterClient.Dedup([a, b, c]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("2026-2", merged[0].DocumentNumber);
        Assert.Equal("internal-revenue-service, treasury-department", merged[1].Agency);
    }

    [Fact]
    public void FederalRegisterUrl_MatchesPlaybookQuery()
    {
        var url = FederalRegisterClient.BuildUrl("treasury-department", "2026-10-25");
        Assert.Contains("conditions[agencies][]=treasury-department", url);
        Assert.Contains("conditions[publication_date][gte]=2026-10-25", url);
        Assert.Contains("fields[]=comments_close_on", url);
        Assert.Contains("per_page=100", url);
    }
}
