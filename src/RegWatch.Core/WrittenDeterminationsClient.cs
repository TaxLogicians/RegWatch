using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RegWatch.Core;

/// <summary>
/// IRS written determinations (private letter rulings, TAMs, Chief Counsel advice), released in a
/// weekly batch of 30 or so. The IRS publishes them only as a table on irs.gov, so this reads the
/// table and keeps rulings whose Uniform Issue List code or subject names a core profile section.
/// </summary>
public static partial class WrittenDeterminationsClient
{
    public const string ListUrl = "https://www.irs.gov/written-determinations";
    public const int PageSize = 100;
    public const int MaxPages = 5;

    public static bool Handles(string url) => url.Equals(ListUrl, StringComparison.OrdinalIgnoreCase);

    public static string PageUrl(int page) =>
        $"{ListUrl}?items_per_page={PageSize}" + (page > 0 ? $"&page={page}" : "");

    /// <summary>Every ruling since the bookmark (pages are read until one reaches it); <see cref="IsCore"/> narrows them.</summary>
    public static async Task<IReadOnlyList<FeedItem>> GetItemsAsync(
        HttpClient http, string sourceName, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var all = new List<FeedItem>();
        for (var page = 0; page < MaxPages; page++)
        {
            var rows = Parse(await http.GetStringAsync(PageUrl(page), ct), sourceName);
            all.AddRange(rows);
            // Rows are merged per ruling, so a full page can give fewer than PageSize items.
            if (rows.Count == 0 || rows.Any(r => r.Published is { } p && p <= cutoff)) break;
        }
        return all;
    }

    /// <summary>A ruling whose sections (from the UIL codes and the subject) include a core section, or whose subject names a core term.</summary>
    public static bool IsCore(FeedItem ruling, ProfileTerms core) =>
        ruling.Categories.Any(core.Sections.Contains) || core.Matches(ruling.Title);

    /// <summary>One item per ruling; a ruling filed under several UIL codes has a row for each, merged here.</summary>
    public static IReadOnlyList<FeedItem> Parse(string html, string sourceName) =>
        Row().Matches(html)
            .Select(m => (
                Number: m.Groups["num"].Value,
                Uil: Clean(m.Groups["uil"].Value),
                Subject: Clean(m.Groups["subject"].Value),
                Date: Clean(m.Groups["date"].Value)))
            .GroupBy(r => r.Number)
            .Select(g =>
            {
                var number = g.Key;
                var uils = g.Select(r => r.Uil).Where(u => u.Length > 0).Distinct().ToList();
                var subjects = g.Select(r => r.Subject).Where(s => s.Length > 0).Distinct().ToList();
                var sections = uils.Select(u => UilSection().Match(u)).Where(u => u.Success)
                    .Select(u => ProfileTerms.BaseSection(u.Groups[1].Value))
                    .Concat(subjects.SelectMany(ProfileTerms.SectionsIn).Select(ProfileTerms.BaseSection))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var released = DateTime.TryParseExact(g.First().Date, "MM/dd/yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d)
                    ? new DateTimeOffset(d.AddHours(12), TimeSpan.Zero)
                    : (DateTimeOffset?)null;
                var uilText = string.Join(", ", uils);
                return new FeedItem(sourceName, "WD " + number,
                    $"Written determination {number}: {(subjects.Count > 0 ? string.Join("; ", subjects) : "no subject given")} (UIL {uilText})",
                    $"https://www.irs.gov/pub/irs-wd/{number}.pdf", released, sections,
                    $"IRS written determination {number}, Uniform Issue List {uilText}. Not precedent: it applies only to the taxpayer who asked (IRC 6110(k)(3)).",
                    FeedItemKind.Ruling);
            })
            .ToList();

    private static string Clean(string s) => Spaces().Replace(WebUtility.HtmlDecode(s), " ").Trim();

    [GeneratedRegex("""<tr>\s*<td[^>]*picklist-number[^>]*>\s*<a href="/pub/irs-wd/(?<num>\d+)\.pdf"[^>]*>.*?</td>\s*<td[^>]*picklist-uilc[^>]*>(?<uil>[^<]*)</td>\s*<td[^>]*picklist-subject[^>]*>(?<subject>[^<]*)</td>\s*<td[^>]*release-date[^>]*>(?<date>[^<]*)</td>""", RegexOptions.Singleline)]
    private static partial Regex Row();

    // "0430.00-00" gives 0430; "1400Z.02-00" gives 1400Z.
    [GeneratedRegex(@"^(\d{1,4}[A-Z]?)\.")]
    private static partial Regex UilSection();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
