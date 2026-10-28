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
        PressSection(sb, result);

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
            sb.AppendLine($"**Why you care:** {i.WhyYouCare}");
            var coverage = result.Press.Where(p => Covers(p, i)).ToList();
            if (coverage.Count > 0)
                sb.AppendLine("**Also covered by:** " + string.Join("; ", coverage.Select(p => $"[{p.Source}: {p.Title}]({p.Url})")));
            sb.AppendLine();
        }
    }

    /// <summary>Commentary articles: secondary sources, linked back to the guidance cards they discuss.</summary>
    private static void PressSection(StringBuilder sb, ScoringResult result)
    {
        var relevant = result.Press.Where(p => p.Score >= 1).ToList();
        var skipped = result.Press.Where(p => p.Score == 0).ToList();
        if (relevant.Count == 0 && skipped.Count == 0) return;

        sb.AppendLine($"## In the press — {relevant.Count} relevant article(s)").AppendLine();
        sb.AppendLine("> Commentary from professional publications: secondary sources. Use them to find and understand the guidance, then rely on the primary source.").AppendLine();

        foreach (var p in relevant)
        {
            var buckets = p.Buckets.Count > 0 ? $" ({string.Join(" / ", p.Buckets)})" : "";
            var date = p.Published is { Length: > 0 } d ? $", {d}" : "";
            var link = "";
            if (!string.IsNullOrWhiteSpace(p.RelatedCite))
                link = result.Items.Any(i => i.Score >= 1 && Covers(p, i))
                    ? $" · on {p.RelatedCite} (card above)"
                    : result.Items.Any(i => Covers(p, i))
                        ? $" · on {p.RelatedCite} (skipped above)"
                        : $" · on {p.RelatedCite} (not in this digest)";
            sb.AppendLine($"- **[{p.Title}]({p.Url})** — {p.Source}{date} · relevance {p.Score}{buckets}{link}");
            sb.AppendLine($"  {p.WhyYouCare}");
        }

        if (skipped.Count > 0)
        {
            if (relevant.Count > 0) sb.AppendLine();
            sb.AppendLine($"**Not relevant to the profile ({skipped.Count}):** " +
                string.Join("; ", skipped.Select(p => $"[{p.Title}]({p.Url})")));
        }
        sb.AppendLine();
    }

    /// <summary>True when an article's related cite names this guidance item (T.D. 10060 = TD 10060).</summary>
    internal static bool Covers(PressItem press, ScoredItem item)
    {
        var key = CiteKey(press.RelatedCite);
        if (key.Length < 4) return false;
        return CiteKey(item.Cite) == key || CiteKey(item.Source).Contains(key, StringComparison.Ordinal);
    }

    internal static string CiteKey(string? cite) =>
        new string((cite ?? "").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

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
