using RegWatch.Core;
using RegWatch.Runner;

// Headless weekly run for Windows Task Scheduler:
//   RegWatch.Runner.exe [--root <RegWatch folder>] [--dry-run] [--no-notify]
// --dry-run fetches and saves the scoring payload to logs\ without calling Claude or touching STATE.json.
// Unless --no-notify is given or the setting is off, a Windows notification reports the result.
// Exit codes: 0 ok, 2 scorer not ready (no claude.exe or API key), 1 run failed. Each run appends to <root>\logs\<date>.log.

string? rootArg = null;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--root") rootArg = args[i + 1];
var dryRun = args.Contains("--dry-run");
var notify = !args.Contains("--no-notify") && AppSettings.Load().NotifyAfterScheduledRun;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = RunPipeline.DefaultRoot(rootArg);
var logDir = Path.Combine(root, "logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd}.log");

void Log(string line)
{
    var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
    Console.WriteLine(stamped);
    File.AppendAllText(logPath, stamped + Environment.NewLine);
}

if (dryRun)
{
    using var http = RunPipeline.CreateHttpClient();
    var pipeline = new RunPipeline(root, http, scorer: null!, AppSettings.Load().EffectiveFeeds);
    var fetched = await pipeline.FetchAsync(new Progress<string>(Log));
    var payloadPath = Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd}-payload.json");
    File.WriteAllText(payloadPath, ScoringPrompt.BuildPayload(fetched));
    foreach (var issue in fetched.IrbIssues)
        Log($"IRB {issue.Issue}: {issue.Highlights.Length:N0} chars of highlights");
    foreach (var c in fetched.CommentaryItems)
        Log($"Article: {c.Source} | {c.Published:yyyy-MM-dd} | {c.Title}");
    foreach (var p in fetched.Problems)
        Log($"Problem: {p}");
    Log($"Dry run: {fetched.IrbIssues.Count} IRB issue(s), {fetched.FedRegDocuments.Count} FR docs; payload {new FileInfo(payloadPath).Length:N0} bytes at {payloadPath}");
    return 0;
}

var settings = AppSettings.Load();
if (ScorerFactory.Problem(settings) is { } problem)
{
    Log(problem);
    if (notify) Notifier.Show("RegWatch could not run", problem, root);
    return 2;
}

try
{
    using var http = RunPipeline.CreateHttpClient();
    var scorer = ScorerFactory.Create(settings);
    Log($"Scoring via {scorer.Description}.");
    var pipeline = new RunPipeline(root, http, scorer, settings.EffectiveFeeds, settings.UseFeedbackInScoring);
    var outcome = await pipeline.RunAsync(new Progress<string>(Log));
    Log(outcome.Summary);
    if (notify)
    {
        var (title, text) = NotificationText(outcome.Result);
        Notifier.Show(title, text, root);
    }
    return 0;
}
catch (Exception ex)
{
    Log($"Run failed: {ex.GetType().Name}: {ex.Message}");
    if (notify) Notifier.Show("RegWatch run failed", $"{ex.Message}\nSee the log in {logDir}. The next run retries the same window.", root);
    return 1;
}

static (string Title, string Text) NotificationText(ScoringResult result)
{
    var read = result.Items.Count(i => i.Score == 3);
    var week = result.Items.Count(i => i.Score == 2);
    var press = result.Press.Count(p => p.Score >= 2);
    var title = read > 0 ? $"RegWatch: {read} to read now"
        : week > 0 ? $"RegWatch: {week} to read this week"
        : "RegWatch: nothing urgent this week";
    var counts = $"{read} scored 3 · {week} scored 2" + (press > 0 ? $" · {press} relevant article(s)" : "");
    return (title, $"{counts}\n{result.Headline}");
}
