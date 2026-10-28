using System.Text;
using System.Text.Json;

namespace RegWatch.Core;

/// <summary>Renders a scored run as digests/&lt;date&gt;.md in the playbook's card format.</summary>
public static class DigestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Render(string date, string window, ScoringResult result, IReadOnlyList<string> fetchProblems)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# RegWatch Digest — {date}").AppendLine();
        sb.AppendLine($"**Window:** {window}");
        sb.AppendLine($"**Headline:** {result.Headline}").AppendLine();
        sb.AppendLine("> Triage only, not authority. Summaries come from IRB/FR pages and abstracts; verify against the primary source before relying on anything.").AppendLine();

        var notes = fetchProblems.Select(p => $"**Fetch problem:** {p}").Concat(result.Notes).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine("## Run notes / fetch caveats");
            foreach (var n in notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        if (!result.Items.Any(i => i.Score >= 2) && !result.Headline.Contains("Nothing scores 2", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("**Nothing scores 2 or higher this run.**").AppendLine();

        Section(sb, result, 3, "Score 3 — read now");
        Section(sb, result, 2, "Score 2 — read this week");
        Section(sb, result, 1, "Score 1 — skim / reference");

        var skips = result.Items.Where(i => i.Score == 0).ToList();
        if (skips.Count > 0)
        {
            sb.AppendLine($"## Skip (score 0) — {skips.Count} items");
            foreach (var s in skips)
                sb.AppendLine($"- [{DisplayCite(s)}]({s.Url}) — {s.WhyYouCare}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, ScoringResult result, int score, string heading)
    {
        var items = result.Items.Where(i => i.Score == score).ToList();
        if (items.Count == 0) return;

        sb.AppendLine($"## {heading}").AppendLine();
        foreach (var i in items)
        {
            var marker = score == 3 ? "🔴" : "🟡";
            var buckets = i.Buckets.Count > 0 ? string.Join(" / ", i.Buckets) : "none";
            sb.AppendLine($"### {marker} {DisplayCite(i)} — relevance {i.Score} ({buckets})");
            sb.AppendLine($"**Type:** {i.Type} ({i.Source}) · **IRC:** {i.Irc} · **Comment deadline:** {i.CommentDeadline ?? "n/a"}");
            sb.AppendLine($"**Source:** {i.Url}");
            sb.AppendLine($"**Plain English:** {i.PlainEnglish}");
            sb.AppendLine($"**Why you care:** {i.WhyYouCare}").AppendLine();
        }
    }

    /// <summary>Federal Register notices often have no guidance number; fall back to "FR &lt;doc&gt;".</summary>
    internal static string DisplayCite(ScoredItem item) =>
        string.IsNullOrWhiteSpace(item.Cite) || item.Cite.Equals("not stated", StringComparison.OrdinalIgnoreCase)
            ? item.Source
            : item.Cite;

    /// <summary>Writes the Markdown digest and a JSON sidecar; returns the Markdown path.</summary>
    public static string Write(string digestsDir, string date, string markdown, ScoringResult result)
    {
        Directory.CreateDirectory(digestsDir);
        var stem = date;
        for (var n = 2; File.Exists(Path.Combine(digestsDir, stem + ".md")); n++)
            stem = $"{date}_{n}";

        var mdPath = Path.Combine(digestsDir, stem + ".md");
        File.WriteAllText(mdPath, markdown, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(digestsDir, stem + ".json"), JsonSerializer.Serialize(result, JsonOptions));
        return mdPath;
    }
}
