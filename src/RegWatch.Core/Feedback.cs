using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegWatch.Core;

public enum FeedbackVerdict
{
    /// <summary>Scored higher than it deserved.</summary>
    TooHigh,

    /// <summary>Deserved a higher score.</summary>
    TooLow,

    /// <summary>Out of scope entirely; should have been 0.</summary>
    NotRelevant,
}

/// <summary>One "scored wrong" mark the reviewer left on a digest item.</summary>
public sealed record FeedbackEntry
{
    public DateTime At { get; init; }
    public string Digest { get; init; } = "";
    public string Item { get; init; } = "";
    public int Score { get; init; }
    public FeedbackVerdict Verdict { get; init; }

    [JsonIgnore]
    public string VerdictText => Verdict switch
    {
        FeedbackVerdict.TooHigh => "should be lower",
        FeedbackVerdict.TooLow => "should be higher",
        _ => "not relevant (should be 0)",
    };
}

/// <summary>
/// Scoring feedback in &lt;root&gt;\feedback.jsonl, one entry per line. When enabled, the latest
/// entries go to the scorer with the profile as calibration examples.
/// </summary>
public sealed class FeedbackLog(string root)
{
    public const int MaxInPrompt = 40;

    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public string Path { get; } = System.IO.Path.Combine(root, "feedback.jsonl");

    public IReadOnlyList<FeedbackEntry> Load() =>
        File.Exists(Path)
            ? File.ReadLines(Path)
                .Where(l => l.Trim().Length > 0)
                .Select(l => TryParse(l))
                .OfType<FeedbackEntry>()
                .ToList()
            : [];

    public void Add(FeedbackEntry entry)
    {
        // A second mark on the same item replaces the first.
        Save(Load().Where(e => !SameItem(e, entry)).Append(entry));
    }

    public void Remove(FeedbackEntry entry) => Save(Load().Where(e => e != entry));

    public void Save(IEnumerable<FeedbackEntry> entries) =>
        File.WriteAllLines(Path, entries.Select(e => JsonSerializer.Serialize(e, Options)));

    /// <summary>Text appended to the profile for the scorer; empty when there is no feedback.</summary>
    public static string ProfileAddendum(IReadOnlyList<FeedbackEntry> entries)
    {
        if (entries.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("## Reviewer corrections (from in-app feedback)").AppendLine();
        sb.AppendLine("The reviewer marked these past scores as wrong. Treat them as calibration examples:");
        sb.AppendLine("score similar items the way the reviewer indicates. They do not override the guards above.");
        sb.AppendLine();
        foreach (var e in entries.OrderByDescending(e => e.At).Take(MaxInPrompt))
            sb.AppendLine($"- {e.Item} (digest {e.Digest}): scored {e.Score}; reviewer says {e.VerdictText}.");
        return sb.ToString();
    }

    private static bool SameItem(FeedbackEntry a, FeedbackEntry b) => a.Digest == b.Digest && a.Item == b.Item;

    private static FeedbackEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<FeedbackEntry>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
