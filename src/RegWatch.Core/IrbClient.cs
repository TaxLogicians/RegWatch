using System.Net;
using System.Text.RegularExpressions;

namespace RegWatch.Core;

/// <summary>Reads the Internal Revenue Bulletin index and issue pages on irs.gov.</summary>
public sealed partial class IrbClient(HttpClient http)
{
    public const string IndexUrl = "https://www.irs.gov/irb";

    public static string IssueUrl(string issue) => $"https://www.irs.gov/irb/{issue}_irb";

    /// <summary>Issue numbers (e.g. "2026-44") listed on the IRB index, oldest first.</summary>
    public async Task<IReadOnlyList<string>> ListIssuesAsync(CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(IndexUrl, ct);
        return ParseIssueList(html);
    }

    public async Task<IrbIssue> GetIssueAsync(string issue, CancellationToken ct = default)
    {
        var url = IssueUrl(issue);
        var html = await http.GetStringAsync(url, ct);
        var (contents, highlights) = ExtractHighlights(html);
        return new IrbIssue(issue, url, contents, highlights);
    }

    public static IReadOnlyList<string> ParseIssueList(string html) =>
        IssueLink().Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(i => i, IssueComparer.Instance)
            .ToList();

    /// <summary>
    /// Pulls the table of contents and the "Highlights of This Issue" synopses out of an
    /// issue page. The page is 100k-700k characters; this section is a few thousand and
    /// lists every item in the issue, so nothing is lost to truncation.
    /// </summary>
    public static (string Contents, string Highlights) ExtractHighlights(string html)
    {
        var text = HtmlToText(html);
        const string marker = "HIGHLIGHTS OF THIS ISSUE";
        const string mission = "The IRS Mission";

        var first = text.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0)
            return ("", Truncate(text, 20_000));

        var second = text.IndexOf(marker, first + marker.Length, StringComparison.Ordinal);

        // The first occurrence is the on-page table of contents; it ends at "Definition of Terms".
        var tocEnd = text.IndexOf("Definition of Terms", first, StringComparison.Ordinal);
        var contents = tocEnd > first && (second < 0 || tocEnd < second)
            ? text[first..tocEnd]
            : "";

        if (second < 0)
            return (contents, Truncate(text[first..], 20_000));

        var end = text.IndexOf(mission, second, StringComparison.Ordinal);
        var highlights = end > second ? text[second..end] : Truncate(text[second..], 20_000);
        return (contents.Trim(), highlights.Trim());
    }

    internal static string HtmlToText(string html)
    {
        var noScripts = ScriptOrStyle().Replace(html, " ");
        var noTags = Tag().Replace(noScripts, " ");
        return Whitespace().Replace(WebUtility.HtmlDecode(noTags), " ");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"irb/(\d{4}-\d{2})_irb", RegexOptions.IgnoreCase)]
    private static partial Regex IssueLink();

    [GeneratedRegex(@"<(script|style)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Orders "YYYY-NN" issue numbers chronologically.</summary>
public sealed class IssueComparer : IComparer<string>
{
    public static readonly IssueComparer Instance = new();

    public int Compare(string? x, string? y) => Key(x).CompareTo(Key(y));

    private static (int, int) Key(string? issue)
    {
        var parts = (issue ?? "").Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], out var y) && int.TryParse(parts[1], out var n)
            ? (y, n)
            : (0, 0);
    }
}
