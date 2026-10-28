using System.Globalization;
using System.Net.Http.Headers;

namespace RegWatch.Core;

public sealed record RunOutcome(string DigestPath, string Summary, ScoringResult Result);

/// <summary>
/// One weekly run, the same steps as PLAYBOOK.md: load config, fetch IRB + Federal Register,
/// dedup, score, write the digest, update STATE.json.
/// </summary>
public sealed class RunPipeline(string root, HttpClient http, ClaudeScorer scorer)
{
    public string ProfilePath => Path.Combine(root, "PROFILE.md");
    public string StatePath => Path.Combine(root, "STATE.json");
    public string DigestsDir => Path.Combine(root, "digests");

    /// <summary>The RegWatch folder: --root / REGWATCH_ROOT, else %USERPROFILE%\RegWatch.</summary>
    public static string DefaultRoot(string? overrideRoot = null) =>
        overrideRoot
        ?? Environment.GetEnvironmentVariable("REGWATCH_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RegWatch");

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // irs.gov rejects requests with no user agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RegWatch", "1.0"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+local triage digest)"));
        return http;
    }

    public async Task<RunOutcome> RunAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(ProfilePath))
            throw new FileNotFoundException($"PROFILE.md not found in {root}.", ProfilePath);

        var profile = await File.ReadAllTextAsync(ProfilePath, ct);
        var store = new StateStore(StatePath);
        var state = store.Load();
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var fetched = await FetchAsync(state, log, ct);
        log?.Report($"Fetched {fetched.IrbIssues.Count} IRB issue(s), {fetched.FedRegDocuments.Count} new Federal Register document(s).");

        ScoringResult result;
        if (fetched.IrbIssues.Count == 0 && fetched.FedRegDocuments.Count == 0)
        {
            result = new ScoringResult { Headline = "No new IRB issues or Federal Register documents since the last run." };
        }
        else
        {
            result = await scorer.ScoreAsync(profile, fetched, log, ct);
        }

        var sorted = result with { Items = result.Items.OrderByDescending(i => i.Score).ThenBy(i => i.Cite).ToList() };
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
        state.LastRun = today;
        state.LastRunSummary = summary;
        store.Save(state);

        return new RunOutcome(path, summary, sorted);
    }

    /// <summary>Fetch step only, against the current bookmarks; writes nothing.</summary>
    public Task<FetchResult> FetchAsync(IProgress<string>? log = null, CancellationToken ct = default) =>
        FetchAsync(new StateStore(StatePath).Load(), log, ct);

    private async Task<FetchResult> FetchAsync(RunState state, IProgress<string>? log, CancellationToken ct)
    {
        var problems = new List<string>();
        var issues = new List<IrbIssue>();
        var irb = new IrbClient(http);

        try
        {
            log?.Report("Reading the IRB index…");
            var listed = await irb.ListIssuesAsync(ct);
            var last = state.LastIrbIssue;
            var fresh = listed.Where(i => last is null || IssueComparer.Instance.Compare(i, last) > 0).ToList();
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
        return new FetchResult(issues, newDocs, problems);
    }

    private static string Window(RunState state, FetchResult fetched)
    {
        var irb = fetched.IrbIssues.Count switch
        {
            0 => $"no new IRB issues (last seen {state.LastIrbIssue ?? "none"})",
            1 => $"IRB {fetched.IrbIssues[0].Issue}",
            _ => $"IRB {fetched.IrbIssues[0].Issue} → {fetched.IrbIssues[^1].Issue}",
        };
        return $"{irb}; Federal Register IRS + Treasury + FinCEN published on/after {state.LastFedRegDate ?? "the last 30 days"} ({fetched.FedRegDocuments.Count} new documents).";
    }

    private static string Summarize(string today, FetchResult fetched, ScoringResult result)
    {
        var counts = string.Join(", ", Enumerable.Range(0, 4).Reverse()
            .Select(s => $"{result.Items.Count(i => i.Score == s)}×{s}"));
        var top = result.Items.FirstOrDefault(i => i.Score >= 2);
        var topLine = top is null ? "nothing above score 1" : $"top: {top.Cite} ({top.Score})";
        var problems = fetched.Problems.Count > 0 ? $" {fetched.Problems.Count} fetch problem(s)." : "";
        return $"App run {today}: {fetched.IrbIssues.Count} IRB issue(s), {fetched.FedRegDocuments.Count} FR docs. Scores {counts}; {topLine}.{problems}";
    }
}
