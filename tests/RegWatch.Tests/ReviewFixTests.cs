using System.Net;
using RegWatch.Core;

namespace RegWatch.Tests;

/// <summary>Run cancellation and locking, settings files, link safety and the digest's source notes.</summary>
public class ReviewFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "regwatch-tests-" + Guid.NewGuid().ToString("N"));

    public ReviewFixTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "PROFILE.md"), """
            ## Topic buckets

            - **foreign_trusts (3)** — §679, §6048, Form 3520
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly FeedSource Feed = new() { Name = "Feed", Url = "https://feed.example/rss" };

    // ---- Cancel and the run lock

    [Fact]
    public async Task CancelWhileReadingSources_EndsTheRun_WithoutDigestOrState()
    {
        using var cts = new CancellationTokenSource();
        var http = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith(Feed.Url, StringComparison.Ordinal))
            {
                cts.Cancel(); // the user presses Cancel while this source is being read
                throw new TaskCanceledException();
            }
            // Nothing new from the IRB or the Federal Register, so only the sources are left.
            return url.Contains("federalregister", StringComparison.Ordinal) ? """{"results":[]}""" : "<html></html>";
        }));
        var pipeline = new RunPipeline(_root, http, new NeverScorer(), [Feed]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(ct: cts.Token));

        Assert.False(File.Exists(pipeline.StatePath));
        Assert.False(Directory.Exists(pipeline.DigestsDir) && Directory.EnumerateFiles(pipeline.DigestsDir).Any());
    }

    [Fact]
    public async Task ASecondRunAtTheSameTime_IsRefused()
    {
        var pipeline = new RunPipeline(_root, new HttpClient(new Handler(_ => "")), new NeverScorer());
        using var held = new FileStream(pipeline.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var ex = await Assert.ThrowsAsync<RunInProgressException>(() => pipeline.RunAsync());

        Assert.Contains("already in progress", ex.Message);
    }

    // ---- Settings file

    [Fact]
    public void Settings_UnreadableFile_IsKeptAside_AndReported()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{ "Scorer": "OpenAiCompatible", """); // cut off mid-write

        var loaded = AppSettings.Load(path);

        Assert.Equal(new AppSettings().Scorer, loaded.Scorer);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_root, "settings.json.unreadable-*"));
        Assert.Contains("kept as", AppSettings.LoadProblem);
    }

    [Fact]
    public void Settings_SaveReplacesTheFileWhole()
    {
        var path = Path.Combine(_root, "settings.json");
        new AppSettings { Theme = AppTheme.Dark }.Save(path);
        (AppSettings.Load(path) with { Theme = AppTheme.Light }).Save(path);

        Assert.Equal(AppTheme.Light, AppSettings.Load(path).Theme);
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ---- Links

    [Theory]
    [InlineData("https://www.irs.gov/irb", true)]
    [InlineData("http://example.org/a?b=c", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"\\server\share\x.exe", false)]
    [InlineData("file://server/share/x.exe", false)]
    [InlineData("ms-msdt:/id PCWDiagnostic", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("C:\\Users", false)]
    [InlineData("", false)]
    public void OnlyWebLinksAreOpened(string target, bool expected) => Assert.Equal(expected, Links.IsWebLink(target));

    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("data:text/html;charset=utf-8;base64,PGh0bWw+", true)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("https://www.irs.gov/", false)]
    public void OnlyTheInlinePageLoadsInTheViewer(string target, bool expected) => Assert.Equal(expected, Links.IsInlinePage(target));

    // ---- Digest note for the other sources

    [Fact]
    public void OtherSourcesNote_DescribesOpinionsAndRulingsAccurately()
    {
        var result = new ScoringResult
        {
            Headline = "h",
            Press = [new PressItem { Title = "Doe v. Commissioner — Memorandum Opinion", Source = "Tax Court opinions", Url = "https://dawson.ustaxcourt.gov/case-detail/1-25", Score = 2 }],
        };

        var md = DigestWriter.Render("2026-10-28", "window", result, []);

        Assert.Contains("## Other sources — 1 relevant item(s)", md);
        Assert.Contains("only T.C. opinions are precedential", md);
        Assert.Contains("IRC 6110(k)(3)", md);
        Assert.DoesNotContain("In the press", md);
    }

    private sealed class NeverScorer : IScorer
    {
        public string Description => "test";

        public Task<ScoringResult> ScoreAsync(string profile, FetchResult fetched, IProgress<string>? log = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The scorer should not be reached in this test.");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
    }
}
