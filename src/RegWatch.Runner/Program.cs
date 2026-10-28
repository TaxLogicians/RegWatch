using Anthropic;
using RegWatch.Core;

// Headless weekly run for Windows Task Scheduler:
//   RegWatch.Runner.exe [--root <RegWatch folder>] [--dry-run]
// --dry-run fetches and saves the scoring payload to logs\ without calling Claude or touching STATE.json.
// Exit codes: 0 ok, 2 no API key, 1 run failed. Each run appends to <root>\logs\<date>.log.

string? rootArg = null;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--root") rootArg = args[i + 1];
var dryRun = args.Contains("--dry-run");

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
    var pipeline = new RunPipeline(root, http, scorer: null!);
    var fetched = await pipeline.FetchAsync(new Progress<string>(Log));
    var payloadPath = Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd}-payload.json");
    File.WriteAllText(payloadPath, ClaudeScorer.BuildPayload(fetched));
    foreach (var issue in fetched.IrbIssues)
        Log($"IRB {issue.Issue}: {issue.Highlights.Length:N0} chars of highlights");
    foreach (var problem in fetched.Problems)
        Log($"Problem: {problem}");
    Log($"Dry run: {fetched.IrbIssues.Count} IRB issue(s), {fetched.FedRegDocuments.Count} FR docs; payload {new FileInfo(payloadPath).Length:N0} bytes at {payloadPath}");
    return 0;
}

var key = ApiKeyStore.Resolve();
if (key is null)
{
    Log("No Anthropic API key. Open the RegWatch app and choose Set API key, or set ANTHROPIC_API_KEY.");
    return 2;
}

try
{
    using var http = RunPipeline.CreateHttpClient();
    var client = new AnthropicClient { ApiKey = key };
    var pipeline = new RunPipeline(root, http, new ClaudeScorer(client));
    var outcome = await pipeline.RunAsync(new Progress<string>(Log));
    Log(outcome.Summary);
    return 0;
}
catch (Exception ex)
{
    Log($"Run failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
