using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RegWatch.Core;

/// <summary>
/// Treasury press releases. Treasury's RSS feed carries other content, but home.treasury.gov keeps
/// a JSON list of each year's releases (the data behind its press-release page). Most releases are
/// sanctions and readouts, so only tax headlines and headlines naming a core profile term are kept.
/// </summary>
public static partial class TreasuryPressClient
{
    public const string PageUrl = "https://home.treasury.gov/news/press-releases";

    public static bool Handles(string url) => url.TrimEnd('/').Equals(PageUrl, StringComparison.OrdinalIgnoreCase);

    public static string YearUrl(int year) =>
        $"https://home.treasury.gov/news-data/press-releases/search/{year.ToString(CultureInfo.InvariantCulture)}.json";

    /// <summary>This year's list, plus last year's when the bookmark falls in last year.</summary>
    public static async Task<IReadOnlyList<FeedItem>> GetItemsAsync(
        HttpClient http, string sourceName, DateTimeOffset cutoff, DateTime today, CancellationToken ct = default)
    {
        var items = Parse(await http.GetStringAsync(YearUrl(today.Year), ct), sourceName).ToList();
        if (cutoff.LocalDateTime.Year < today.Year)
            items.AddRange(Parse(await http.GetStringAsync(YearUrl(today.Year - 1), ct), sourceName));
        return items;
    }

    /// <summary>A tax headline, or one naming a core profile term.</summary>
    public static bool IsKept(FeedItem release, ProfileTerms core) =>
        TaxHeadline().IsMatch(release.Title) || core.Matches(release.Title);

    public static IReadOnlyList<FeedItem> Parse(string json, string sourceName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return [];

        return items.EnumerateArray()
            .Select(e =>
            {
                var path = Str(e, "url");
                var title = Str(e, "title");
                if (path is null || title is null) return null;
                DateTimeOffset? published = DateTimeOffset.TryParse(Str(e, "datetime") ?? Str(e, "date"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : "https://home.treasury.gov" + path;
                var label = e.TryGetProperty("subcategory", out var sub) ? Str(sub, "title") : null;
                return new FeedItem(sourceName, url, title, url, published,
                    label is null ? [] : [label], label is null ? "Treasury press release." : $"Treasury press release ({label}).",
                    FeedItemKind.Announcement);
            })
            .OfType<FeedItem>()
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    [GeneratedRegex(@"\b(tax|taxes|taxpayers?|IRS|treaty|treaties|FATCA|Pillar (One|Two)|global minimum|beneficial owner\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaxHeadline();
}
