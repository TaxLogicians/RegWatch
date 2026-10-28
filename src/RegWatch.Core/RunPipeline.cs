using System.Globalization;
using System.Net.Http.Headers;

namespace RegWatch.Core;

public sealed record RunOutcome(string DigestPath, string Summary, ScoringResult Result);

/// <summary>Another run holds the data folder's run.lock.</summary>
public sealed class RunInProgressException(string message) : InvalidOperationException(message);

/// <summary>
/// One weekly run, the same steps as PLAYBOOK.md: load config, fetch IRB + Federal Register
/// (+ commentary feeds), dedup, score, write the digest, update STATE.json.
/// </summary>
public sealed class RunPipeline(string root, HttpClient http, IScorer scorer, IReadOnlyList<FeedSource>? feeds = null, bool useFeedback = false)
{
    public string ProfilePath => Path.Combine(root, "PROFILE.md");
    public string StatePath => Path.Combine(root, "STATE.json");
    public string DigestsDir => Path.Combine(root, "digests");
    public string LockPath => Path.Combine(root, "run.lock");

    /// <summary>The RegWatch folder: --root / REGWATCH_ROOT, else %USERPROFILE%\RegWatch.</summary>
    public static string DefaultRoot(string? overrideRoot = null) =>
        overrideRoot
        ?? Environment.GetEnvironmentVariable("REGWATCH_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RegWatch");

    public static HttpClient CreateHttpClient()
    {
        // Feeds are often served gzip/brotli-compressed.
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // irs.gov rejects requests with no user agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RegWatch", AppInfo.Version));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+local triage digest)"));
        return http;
    }

    public async Task<RunOutcome> RunAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(ProfilePath))
            throw new FileNotFoundException($"PROFILE.md not found in {root}.", ProfilePath);

        // One run at a time per data folder: a catch-up scheduled run and Run now can otherwise
        // overlap, scoring the same window twice and racing to save STATE.json.
        using var runLock = AcquireLock();

        // Read once, so the sources are narrowed with the same profile the scorer sees.
        var profileText = await File.ReadAllTextAsync(ProfilePath, ct);
        var profile = profileText;
        if (useFeedback && new FeedbackLog(root).Load() is { Count: > 0 } corrections)
        {
            profile += FeedbackLog.ProfileAddendum(corrections);
            log?.Report($"Including {Math.Min(corrections.Count, FeedbackLog.MaxInPrompt)} scoring correction(s) from your feedback.");
        }
        var store = new StateStore(StatePath);
        var state = store.Load();
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var fetched = await FetchAsync(state, profileText, log, ct);
        log?.Report($"Fetched {fetched.IrbIssues.Count} IRB issue(s), {fetched.FedRegDocuments.Count} new Federal Register document(s), {fetched.CommentaryItems.Count} commentary article(s).");

        ScoringResult result;
        if (fetched.IsEmpty)
        {
            result = new ScoringResult { Headline = "No new IRB issues, Federal Register documents or commentary since the last run." };
        }
        else
        {
            result = await scorer.ScoreAsync(profile, fetched, log, ct);
        }

        var sorted = result with
        {
            Items = result.Items.OrderByDescending(i => i.Score).ThenBy(i => i.Cite).ToList(),
            Press = result.Press.OrderByDescending(p => p.Score).ThenByDescending(p => p.Published).ToList(),
        };
        var window = Window(state, fetched);
        var markdown = DigestWriter.Render(today, window, sorted, fetched.Problems);
        var path = DigestWriter.Write(DigestsDir, today, markdown, sorted);
        log?.Report($"Wrote {path}");

        var summary = Summarize(today, fetched, sorted);

        // Advance bookmarks only past what was actually fetched, so a failed source is re-pulled next run.
        if (fetched.IrbIssues.Count > 0)
            state.LastIrbIssue = fetched.IrbIssues.Select(i => i.Issue).Max(IssueComparer.Instance);
        if (!fetched.Problems.Any(p => p.StartsWith("Federal Register", StringComparison.Ordinal)))
            state.LastFedRegDate = today;
        state.AddSeen(fetched.FedRegDocuments.Select(d => d.DocumentNumber));
        foreach (var (feedUrl, newest) in fetched.FeedBookmarkUpdates)
            state.SetFeedBookmark(feedUrl, newest);
        state.AddSeenFeedItems(fetched.CommentaryItems.SelectMany(c => c.Id.Split('\u0001')));
        state.LastRun = today;
        state.LastRunSummary = summary;
        store.Save(state);

        return new RunOutcome(path, summary, sorted);
    }

    /// <summary>Fetch step only, against the current bookmarks; writes nothing.</summary>
    public async Task<FetchResult> FetchAsync(IProgress<string>? log = null, CancellationToken ct = default) =>
        await FetchAsync(new StateStore(StatePath).Load(),
            File.Exists(ProfilePath) ? await File.ReadAllTextAsync(ProfilePath, ct) : "", log, ct);

    private FileStream AcquireLock()
    {
        try
        {
            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new RunInProgressException(
                $"Another RegWatch run is already in progress for {root} (the scheduled run, or Run now in another window). Try again when it finishes.");
        }
    }

    private async Task<FetchResult> FetchAsync(RunState state, string profileText, IProgress<string>? log, CancellationToken ct)
    {
        var problems = new List<string>();
        var issues = new List<IrbIssue>();
        var irb = new IrbClient(http);

        try
        {
            log?.Report("Reading the IRB index…");
            var listed = await irb.ListIssuesAsync(ct);
            var fresh = IrbClient.SelectNew(listed, state.LastIrbIssue);
            foreach (var issue in fresh)
            {
                try
                {
                    log?.Report($"Fetching IRB {issue}…");
                    issues.Add(await irb.GetIssueAsync(issue, ct));
                }
                catch (HttpRequestException ex)
                {
                    // Stop here so the bookmark does not move past the failed issue.
                    problems.Add($"IRB {issue}: {ex.Message} (later issues deferred to next run)");
                    break;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            problems.Add($"IRB index ({IrbClient.IndexUrl}): {ex.Message}");
        }

        var fr = new FederalRegisterClient(http);
        var since = state.LastFedRegDate ?? DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var docs = new List<FedRegDocument>();
        foreach (var agency in FederalRegisterClient.DefaultAgencies)
        {
            try
            {
                log?.Report($"Querying Federal Register: {agency} since {since}…");
                docs.AddRange(await fr.GetDocumentsAsync(agency, since, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                problems.Add($"Federal Register ({agency}): {ex.Message}");
            }
        }

        var seen = state.SeenDocuments;
        var newDocs = FederalRegisterClient.Dedup(docs).Where(d => !seen.Contains(d.DocumentNumber)).ToList();

        var (commentary, bookmarks) = await FetchFeedsAsync(state, ProfileTerms.From(profileText), problems, log, ct);
        return new FetchResult(issues, newDocs, problems, commentary, bookmarks);
    }

    private async Task<(List<FeedItem>, Dictionary<string, DateTimeOffset>)> FetchFeedsAsync(
        RunState state, ProfileTerms core, List<string> problems, IProgress<string>? log, CancellationToken ct)
    {
        // Rulings, opinions and Treasury releases are narrowed to the profile's core sections and terms.
        var client = new FeedClient(http) { Core = core };
        var seen = state.SeenFeedItems;
        var items = new List<FeedItem>();
        var bookmarks = new Dictionary<string, DateTimeOffset>();

        foreach (var feed in (feeds ?? []).Where(f => f.Enabled))
        {
            try
            {
                log?.Report($"Reading {feed.Name}…");
                var read = await client.ReadAsync(feed, state.FeedBookmark(feed.Url), ct: ct);
                items.AddRange(read.Items.Where(i => !seen.Contains(i.Id)));
                if (read.Newest is { } newest) bookmarks[feed.Url] = newest;
            }
            // A timeout is a problem with that source; the user's Cancel ends the run.
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or System.Xml.XmlException or System.Text.Json.JsonException or TaskCanceledException)
            {
                problems.Add($"Feed {feed.Name}: {ex.Message}");
            }
        }
        ct.ThrowIfCancellationRequested();

        return (FeedClient.MergeDuplicates(items), bookmarks);
    }

    private static string Window(RunState state, FetchResult fetched)
    {
        var irb = fetched.IrbIssues.Count switch
        {
            0 => $"no new IRB issues (last seen {state.LastIrbIssue ?? "none"})",
            1 => $"IRB {fetched.IrbIssues[0].Issue}",
            _ => $"IRB {fetched.IrbIssues[0].Issue} → {fetched.IrbIssues[^1].Issue}",
        };
        var press = fetched.CommentaryItems.Count > 0 ? $"; {fetched.CommentaryItems.Count} commentary article(s)" : "";
        return $"{irb}; Federal Register IRS + Treasury + FinCEN published on/after {state.LastFedRegDate ?? "the last 30 days"} ({fetched.FedRegDocuments.Count} new documents){press}.";
    }

    private static string Summarize(string today, FetchResult fetched, ScoringResult result)
    {
        var counts = string.Join(", ", Enumerable.Range(0, 4).Reverse()
            .Select(s => $"score {s}: {result.Items.Count(i => i.Score == s)}"));
        var top = result.Items.FirstOrDefault(i => i.Score >= 2);
        var topLine = top is null ? "nothing above score 1" : $"top: {top.Cite} ({top.Score})";
        var problems = fetched.Problems.Count > 0 ? $" {fetched.Problems.Count} fetch problem(s)." : "";
        var press = result.Press.Count(p => p.Score >= 1);
        return $"App run {today}: {fetched.IrbIssues.Count} IRB issue(s), {fetched.FedRegDocuments.Count} FR docs, {fetched.CommentaryItems.Count} articles ({press} relevant). {counts}; {topLine}.{problems}";
    }
}
