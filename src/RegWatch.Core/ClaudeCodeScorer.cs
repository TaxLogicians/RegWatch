using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RegWatch.Core;

/// <summary>
/// Scores through the Claude Code CLI (<c>claude -p</c>), so a run counts against the user's
/// Claude Pro/Max subscription instead of an API key. Same prompt and schema as
/// <see cref="ClaudeScorer"/>; the CLI is limited to the WebSearch tool.
/// </summary>
public sealed class ClaudeCodeScorer(string claudePath) : IScorer
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    public string Description => "Claude subscription (Claude Code)";

    public async Task<ScoringResult> ScoreAsync(
        string profile, FetchResult fetched, IProgress<string>? log = null, CancellationToken ct = default)
    {
        // Run from an empty folder so no project CLAUDE.md or settings leak into the scoring.
        var work = Directory.CreateTempSubdirectory("regwatch-score").FullName;
        try
        {
            var promptFile = Path.Combine(work, "system.md");
            await File.WriteAllTextAsync(promptFile, ScoringPrompt.SystemPrompt(profile), ct);

            var psi = new ProcessStartInfo(claudePath)
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var arg in new[]
            {
                "-p", "Score every item in the JSON on stdin as the system prompt describes and return the structured result.",
                "--output-format", "json",
                "--json-schema", ScoringPrompt.SchemaJson,
                "--system-prompt-file", promptFile,
                "--tools", "WebSearch",
                "--allowedTools", "WebSearch",
                "--model", "opus",
                "--effort", "high",
                "--no-session-persistence",
                "--setting-sources", "",
            })
                psi.ArgumentList.Add(arg);

            log?.Report("Scoring with Claude Code on your subscription (this can take a few minutes)…");
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Could not start {claudePath}.");

            await process.StandardInput.WriteAsync(ScoringPrompt.BuildPayload(fetched));
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Claude Code did not finish scoring within {Timeout.TotalMinutes:0} minutes.");
            }

            return Parse(await stdout, await stderr, log);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Reads the CLI's <c>--output-format json</c> envelope.</summary>
    public static ScoringResult Parse(string stdout, string stderr, IProgress<string>? log = null)
    {
        JsonObject envelope;
        try
        {
            envelope = JsonNode.Parse(stdout) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Claude Code returned no result: {Trim(detail)}");
        }

        var message = envelope["result"]?.GetValue<string>() ?? "";
        if (envelope["is_error"]?.GetValue<bool>() == true)
        {
            if (message.Contains("authenticate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("login", StringComparison.OrdinalIgnoreCase)
                || message.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Claude Code is not signed in ({Trim(message)}). Open a terminal and run: claude auth login");
            throw new InvalidOperationException($"Claude Code reported an error: {Trim(message)}");
        }

        if (envelope["num_turns"] is JsonValue turns && envelope["duration_ms"] is JsonValue ms)
            log?.Report($"Scored in {ms.GetValue<double>() / 1000:0}s over {turns} turn(s).");

        // --json-schema puts the validated object in structured_output; fall back to the text result.
        var json = envelope["structured_output"] is JsonObject structured
            ? structured.ToJsonString()
            : StripFences(message);
        return ScoringPrompt.ParseResult(json);
    }

    /// <summary>Finds claude.exe on PATH or in the usual install folders.</summary>
    public static string? Locate(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(local, "Microsoft", "WinGet", "Links"))
            .Select(dir => Path.Combine(dir, "claude.exe"));

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;

        // WinGet's package folder, for Task Scheduler runs whose PATH lacks the Links shim.
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        return Directory.Exists(packages)
            ? Directory.GetDirectories(packages, "Anthropic.ClaudeCode*")
                .Select(d => Path.Combine(d, "claude.exe"))
                .FirstOrDefault(File.Exists)
            : null;
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var start = t.IndexOf('\n') + 1;
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return end > start ? t[start..end] : t;
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s.Trim();
}
