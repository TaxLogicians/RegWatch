using System.Text.Json;
using System.Text.Json.Nodes;

namespace RegWatch.Core;

/// <summary>
/// Reads and writes STATE.json. Works on the raw JSON object so fields the app does not
/// know about (the agent-era "notes", "seeded") survive a round trip.
/// </summary>
public sealed class StateStore(string path)
{
    /// <summary>How many reported document numbers to remember for cross-run dedup.</summary>
    public const int SeenLimit = 1000;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    public RunState Load()
    {
        var obj = File.Exists(Path)
            ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? []
            : [];
        return new RunState(obj);
    }

    public void Save(RunState state)
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, state.Raw.ToJsonString(WriteOptions));
        File.Move(tmp, Path, overwrite: true);
    }
}

public sealed class RunState(JsonObject raw)
{
    public JsonObject Raw { get; } = raw;

    public string? LastIrbIssue
    {
        get => Get("last_irb_issue");
        set => Raw["last_irb_issue"] = value;
    }

    public string? LastFedRegDate
    {
        get => Get("last_fedreg_date");
        set => Raw["last_fedreg_date"] = value;
    }

    public string? LastRun
    {
        get => Get("last_run");
        set => Raw["last_run"] = value;
    }

    public string? LastRunSummary
    {
        get => Get("last_run_summary");
        set => Raw["last_run_summary"] = value;
    }

    /// <summary>Federal Register document numbers already reported in an earlier digest.</summary>
    public IReadOnlySet<string> SeenDocuments =>
        (Raw["seen_documents"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .OfType<string>()
            .ToHashSet();

    public void AddSeen(IEnumerable<string> documentNumbers)
    {
        var existing = (Raw["seen_documents"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .OfType<string>()
            .ToList();
        var merged = documentNumbers.Concat(existing).Distinct().Take(StateStore.SeenLimit);
        Raw["seen_documents"] = new JsonArray(merged.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
    }

    private string? Get(string key) =>
        Raw[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
