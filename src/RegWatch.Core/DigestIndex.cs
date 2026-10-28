using System.Globalization;
using System.Text.RegularExpressions;

namespace RegWatch.Core;

public sealed record DigestSummary(string Path, string Name, string Date, string Headline, int Score3, int Score2, int Score1);

public sealed record DeadlineEntry(DateOnly Deadline, string Cite, int Score, string Url, string DigestName);

/// <summary>
/// Reads every digest in digests/. Parses the Markdown rather than the JSON sidecars so the
/// agent-written digests from before the app (June–October 2026) show up too.
/// </summary>
public static partial class DigestIndex
{
    public static IReadOnlyList<DigestSummary> List(string digestsDir)
    {
        if (!Directory.Exists(digestsDir)) return [];
        return Directory.GetFiles(digestsDir, "*.md")
            .Select(Summarize)
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static DigestSummary Summarize(string path)
    {
        var text = File.ReadAllText(path);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var headline = Headline().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        var scores = Cards(text).Select(c => c.Score).ToList();
        return new DigestSummary(path, name, name.Split('_')[0], headline,
            scores.Count(s => s == 3), scores.Count(s => s == 2), scores.Count(s => s == 1));
    }

    /// <summary>Comment deadlines on or after <paramref name="from"/>, soonest first, one row per cite.</summary>
    public static IReadOnlyList<DeadlineEntry> UpcomingDeadlines(string digestsDir, DateOnly from)
    {
        if (!Directory.Exists(digestsDir)) return [];
        return Directory.GetFiles(digestsDir, "*.md")
            .SelectMany(p => Cards(File.ReadAllText(p))
                .Where(c => c.Deadline is not null && c.Deadline >= from)
                .Select(c => new DeadlineEntry(c.Deadline!.Value, c.Cite, c.Score, c.Url,
                    System.IO.Path.GetFileNameWithoutExtension(p))))
            .GroupBy(d => d.Cite)
            .Select(g => g.OrderByDescending(d => d.DigestName, StringComparer.Ordinal).First())
            .OrderBy(d => d.Deadline)
            .ToList();
    }

    public sealed record Card(string Cite, int Score, DateOnly? Deadline, string Url);

    /// <summary>Splits a digest into its "### ... — relevance N" cards.</summary>
    public static IEnumerable<Card> Cards(string markdown)
    {
        var heads = CardHead().Matches(markdown);
        for (var i = 0; i < heads.Count; i++)
        {
            var start = heads[i].Index;
            var end = i + 1 < heads.Count ? heads[i + 1].Index : markdown.Length;
            var body = markdown[start..end];

            DateOnly? deadline = Deadline().Match(body) is { Success: true } d
                && DateOnly.TryParseExact(d.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt : null;
            var url = Url().Match(body) is { Success: true } u ? u.Value : "";

            yield return new Card(heads[i].Groups[1].Value.Trim(), int.Parse(heads[i].Groups[2].Value), deadline, url);
        }
    }

    [GeneratedRegex(@"^\*\*Headline:\*\*\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex Headline();

    // "### 🟡 Notice 2026-61 — relevance 2 (fatca_withholding)"; the marker emoji is optional.
    [GeneratedRegex(@"^###\s+(?:\S+\s+)?(.+?)\s+—\s+relevance\s+(\d)", RegexOptions.Multiline)]
    private static partial Regex CardHead();

    [GeneratedRegex(@"Comment deadline:\*\*\s*(\d{4}-\d{2}-\d{2})")]
    private static partial Regex Deadline();

    [GeneratedRegex(@"https?://[^\s)\]]+")]
    private static partial Regex Url();
}
