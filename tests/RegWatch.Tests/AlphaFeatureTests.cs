using System.Xml.Linq;
using RegWatch.Core;

namespace RegWatch.Tests;

public class AlphaFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "regwatch-tests-" + Guid.NewGuid().ToString("N"));

    public AlphaFeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ---- Weekly task

    [Fact]
    public void WeeklyTask_CatchesUpMissedRuns_AndEscapesPaths()
    {
        var xml = WeeklyTask.Xml(@"C:\Apps\R&D\RegWatch.Runner.exe", @"C:\Users\a&b\RegWatch", new DateTime(2026, 11, 2, 19, 0, 0));
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("true", doc.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal(@"C:\Apps\R&D\RegWatch.Runner.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal(@"--root ""C:\Users\a&b\RegWatch""", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("2026-11-02T19:00:00", doc.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Single(doc.Descendants(ns + "Monday"));
    }

    [Theory]
    [InlineData("2026-10-28 10:00", "2026-11-02 19:00")] // Wednesday
    [InlineData("2026-11-02 08:00", "2026-11-02 19:00")] // Monday morning
    [InlineData("2026-11-02 20:00", "2026-11-09 19:00")] // Monday evening, after the run time
    public void WeeklyTask_NextMondayEvening(string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected), WeeklyTask.NextMondayEvening(DateTime.Parse(now)));

    // ---- Feedback

    [Fact]
    public void Feedback_RoundTrips_AndASecondMarkReplacesTheFirst()
    {
        var log = new FeedbackLog(_dir);
        var item = new FeedbackEntry { At = new DateTime(2026, 10, 28), Digest = "2026-10-28", Item = "FR 2026-21921", Score = 1, Verdict = FeedbackVerdict.TooHigh };

        log.Add(item);
        log.Add(item with { Verdict = FeedbackVerdict.NotRelevant });
        log.Add(item with { Item = "Notice 2026-61", Score = 2, Verdict = FeedbackVerdict.TooLow });

        var loaded = log.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(FeedbackVerdict.NotRelevant, loaded.Single(e => e.Item == "FR 2026-21921").Verdict);

        log.Remove(loaded[0]);
        Assert.Single(log.Load());
    }

    [Fact]
    public void Feedback_SkipsDamagedLines()
    {
        File.WriteAllLines(Path.Combine(_dir, "feedback.jsonl"),
            ["{not json", """{"At":"2026-10-28T00:00:00","Digest":"d","Item":"i","Score":3,"Verdict":"TooHigh"}"""]);

        Assert.Single(new FeedbackLog(_dir).Load());
    }

    [Fact]
    public void Feedback_ProfileAddendum_ListsTheNewestCorrections()
    {
        Assert.Equal("", FeedbackLog.ProfileAddendum([]));

        var entries = Enumerable.Range(0, FeedbackLog.MaxInPrompt + 5)
            .Select(i => new FeedbackEntry { At = new DateTime(2026, 1, 1).AddDays(i), Digest = "d", Item = $"Item {i}", Score = 2, Verdict = FeedbackVerdict.TooLow })
            .ToList();
        var text = FeedbackLog.ProfileAddendum(entries);

        Assert.Contains("Reviewer corrections", text);
        Assert.Contains($"Item {FeedbackLog.MaxInPrompt + 4} (digest d): scored 2; reviewer says should be higher.", text);
        Assert.DoesNotContain("Item 0 ", text);
    }

    // ---- Read marks

    [Fact]
    public void ReadMarks_FirstUse_TreatsExistingDigestsAsRead()
    {
        var marks = new ReadMarks(_dir, ["2026-10-25", "2026-10-28"]);
        Assert.True(marks.IsRead("2026-10-25"));

        var reopened = new ReadMarks(_dir, ["2026-10-25", "2026-10-28", "2026-11-02"]);
        Assert.False(reopened.IsRead("2026-11-02"));

        reopened.MarkRead("2026-11-02");
        Assert.True(new ReadMarks(_dir, []).IsRead("2026-11-02"));
    }

    // ---- Update check

    [Theory]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("0.1.0", "0.1.0-alpha", 1)]
    [InlineData("0.1.0-beta", "0.1.0-alpha.2", 1)]
    [InlineData("0.1.0-alpha.2", "0.1.0-alpha", 1)]
    [InlineData("0.1.0-alpha.10", "0.1.0-alpha.9", 1)]
    [InlineData("0.1.0-alpha", "0.1.0-alpha+abc123", 0)]
    [InlineData("1.0.0", "0.9.9", 1)]
    public void UpdateCheck_ComparesVersions(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(UpdateCheck.Compare(a, b)));
        Assert.Equal(-sign, Math.Sign(UpdateCheck.Compare(b, a)));
    }

    [Fact]
    public async Task UpdateCheck_PicksTheNewestPublishedRelease_IncludingPreReleases()
    {
        const string releases = """
            [ { "tag_name": "v0.3.0-alpha", "draft": true,  "html_url": "https://x/draft" },
              { "tag_name": "v0.1.0-alpha", "draft": false, "html_url": "https://x/010" },
              { "tag_name": "v0.2.0-alpha", "draft": false, "html_url": "https://x/020", "prerelease": true } ]
            """;
        using var http = new HttpClient(new Respond(System.Net.HttpStatusCode.OK, releases));

        var info = await UpdateCheck.CheckAsync(http, "0.1.0-alpha");

        Assert.True(info.Available);
        Assert.Equal("0.2.0-alpha", info.LatestVersion);
        Assert.Equal("https://x/020", info.Url);
        Assert.False((await UpdateCheck.CheckAsync(http, "0.2.0-alpha")).Available);
    }

    [Fact]
    public async Task UpdateCheck_PrivateRepo_PointsToTheReleasesPage()
    {
        using var http = new HttpClient(new Respond(System.Net.HttpStatusCode.NotFound, "{}"));

        var info = await UpdateCheck.CheckAsync(http, "0.1.0-alpha");

        Assert.False(info.Available);
        Assert.Equal(UpdateCheck.ReleasesPage, info.Url);
        Assert.Contains("private", info.Message);
    }

    private sealed class Respond(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    // ---- IRS news releases

    [Fact]
    public void IrsNews_ParsesReleaseNumbersDatesAndLinks()
    {
        var items = IrsNewsClient.Parse(Fixture("irs-news.html"), "IRS news releases");

        Assert.Equal(3, items.Count); // the teaser without a release number is skipped
        var first = items[0];
        Assert.Equal("IR-2026-128", first.Id);
        Assert.Equal("IR-2026-128: IRS reminder: Year-round tax planning can make filing easier", first.Title);
        Assert.Equal("https://www.irs.gov/newsroom/irs-reminder-year-round-tax-planning-can-make-filing-easier", first.Url);
        Assert.Equal(new DateTime(2026, 10, 23), first.Published!.Value.Date);
        Assert.StartsWith("The Internal Revenue Service today encouraged", first.Summary);

        Assert.Contains("&", items[1].Title);
        Assert.Equal(new DateTime(2026, 9, 30), items[2].Published!.Value.Date); // "Sept."
    }

    [Fact]
    public void IrsNews_MonthArchiveUrl() =>
        Assert.Equal("https://www.irs.gov/newsroom/news-releases-for-september-2026", IrsNewsClient.MonthUrl(new DateTime(2026, 9, 1)));

    [Fact]
    public void Settings_AddNewBuiltInSources_ToSavedFeeds()
    {
        var saved = new AppSettings { Feeds = [FeedSource.Defaults[0] with { Enabled = false }] };

        var effective = saved.EffectiveFeeds;

        Assert.False(effective[0].Enabled); // the saved choice is kept
        Assert.Contains(effective, f => f.Url == IrsNewsClient.CurrentMonthUrl);
        Assert.Equal(FeedSource.Defaults.Count, effective.Count);
    }
}
