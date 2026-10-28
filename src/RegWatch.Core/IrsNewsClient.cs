using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RegWatch.Core;

/// <summary>
/// IRS news releases (IR-2026-128 and so on). The IRS no longer publishes them as a feed, so this
/// reads the newsroom's current-month page, plus last month's archive page when the bookmark
/// falls in last month (a run early in a month would otherwise miss the end of the previous one).
/// </summary>
public static partial class IrsNewsClient
{
    public const string CurrentMonthUrl = "https://www.irs.gov/newsroom/news-releases-for-current-month";

    public static bool Handles(string url) => url.Equals(CurrentMonthUrl, StringComparison.OrdinalIgnoreCase);

    public static string MonthUrl(DateTime month) =>
        $"https://www.irs.gov/newsroom/news-releases-for-{month.ToString("MMMM", CultureInfo.InvariantCulture).ToLowerInvariant()}-{month:yyyy}";

    public static async Task<IReadOnlyList<FeedItem>> GetItemsAsync(
        HttpClient http, string sourceName, DateTimeOffset cutoff, DateTime today, CancellationToken ct = default)
    {
        var items = Parse(await http.GetStringAsync(CurrentMonthUrl, ct), sourceName).ToList();

        var thisMonth = new DateTime(today.Year, today.Month, 1);
        if (cutoff.LocalDateTime < thisMonth)
        {
            try
            {
                items.AddRange(Parse(await http.GetStringAsync(MonthUrl(thisMonth.AddMonths(-1)), ct), sourceName));
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Last month's archive page is not up yet; the current page is all there is.
            }
        }
        return items;
    }

    /// <summary>Numbered releases only; other teasers on the page (archive links, fact sheets) are skipped.</summary>
    public static IReadOnlyList<FeedItem> Parse(string html, string sourceName) =>
        Teaser().Matches(html)
            .Select(m =>
            {
                var abstractText = WebUtility.HtmlDecode(m.Groups["abstract"].Value).Trim();
                var head = ReleaseHead().Match(abstractText);
                if (!head.Success) return null;
                var id = head.Groups["id"].Value;
                var title = WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
                return new FeedItem(sourceName, id, $"{id}: {title}", "https://www.irs.gov" + m.Groups["href"].Value,
                    ParseDate(head.Groups["date"].Value), [], abstractText[head.Length..].Trim());
            })
            .OfType<FeedItem>()
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToList();

    // "Oct. 23, 2026", "Sept. 3, 2026", "May 5, 2026"
    internal static DateTimeOffset? ParseDate(string text)
    {
        var cleaned = text.Replace(".", "").Replace("Sept ", "Sep ").Trim();
        return DateTime.TryParseExact(cleaned, ["MMM d, yyyy", "MMMM d, yyyy"], CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var d)
            ? new DateTimeOffset(d.AddHours(12), TimeSpan.Zero)
            : null;
    }

    [GeneratedRegex("""<h2>\s*<a href="(?<href>/newsroom/[^"]+)"[^>]*>\s*<span>(?<title>[^<]+)</span>\s*</a>\s*</h2>.*?field--name-field-pup-description-abstract[^>]*>(?<abstract>[^<]*)<""", RegexOptions.Singleline)]
    private static partial Regex Teaser();

    [GeneratedRegex(@"^(?<id>IR-\d{4}-\d+),\s*(?<date>[A-Z][a-z]+\.?\s+\d{1,2},\s*\d{4})\s*[—–-]\s*")]
    private static partial Regex ReleaseHead();
}
