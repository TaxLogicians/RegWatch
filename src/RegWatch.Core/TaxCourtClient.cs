using System.Globalization;
using System.Text.Json;

namespace RegWatch.Core;

/// <summary>
/// US Tax Court opinions (T.C., memorandum, summary and bench opinions). The court publishes no
/// feed, but its DAWSON public search takes a keyword query and a date range, so one search per
/// run asks for opinions filed since the bookmark that cite a core profile section or term.
/// </summary>
public static class TaxCourtClient
{
    public const string SearchUrl = "https://public-api.dawson.ustaxcourt.gov/public-api/opinion-search";
    public const string OpinionTypes = "MOP,SOP,TCOP,OST";

    public static bool Handles(string url) => url.Equals(SearchUrl, StringComparison.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<FeedItem>> GetItemsAsync(
        HttpClient http, string sourceName, DateTimeOffset cutoff, DateTime today, ProfileTerms core, CancellationToken ct = default)
    {
        if (core.IsEmpty) return [];
        return Parse(await http.GetStringAsync(QueryUrl(core, cutoff.LocalDateTime.Date, today), ct), sourceName);
    }

    public static string QueryUrl(ProfileTerms core, DateTime from, DateTime to) =>
        SearchUrl
        + "?keyword=" + Uri.EscapeDataString(Keyword(core))
        + "&dateRange=customDates"
        + "&startDate=" + Uri.EscapeDataString(from.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture))
        + "&endDate=" + Uri.EscapeDataString(to.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture))
        + "&opinionTypes=" + OpinionTypes;

    /// <summary>
    /// Every core section as "section 679" | "sec. 679" (opinions use both styles), and every
    /// other core term as a quoted phrase, joined with | (the search's OR).
    /// </summary>
    public static string Keyword(ProfileTerms core) =>
        string.Join(" | ",
            core.SectionRefs.SelectMany(s => new[] { $"\"section {s}\"", $"\"sec. {s}\"" })
                .Concat(core.Phrases.Select(p => $"\"{p.Replace("\"", "")}\"")));

    public static IReadOnlyList<FeedItem> Parse(string json, string sourceName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        return results.EnumerateArray()
            .Where(r => !(r.TryGetProperty("isStricken", out var s) && s.ValueKind == JsonValueKind.True))
            .Select(r =>
            {
                var docket = Str(r, "docketNumberWithSuffix") ?? Str(r, "docketNumber") ?? "";
                var caption = Str(r, "caseCaption") ?? "Unnamed case";
                var type = Str(r, "documentType") ?? "Opinion";
                var title = Str(r, "documentTitle") ?? type;
                var judge = Str(r, "judge") ?? Str(r, "signedJudgeName");
                DateTimeOffset? filed = DateTimeOffset.TryParse(Str(r, "filingDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                return new FeedItem(sourceName, "TC " + (Str(r, "docketEntryId") ?? docket + " " + title),
                    $"{caption} — {type}, docket {docket}",
                    $"https://dawson.ustaxcourt.gov/case-detail/{Uri.EscapeDataString(Str(r, "docketNumber") ?? docket)}",
                    filed, [Str(r, "eventCode") ?? ""],
                    title + (judge is not null && !title.Contains(judge, StringComparison.OrdinalIgnoreCase) ? $" (Judge {judge})" : "")
                    + ". Matched a core term in the full text of the opinion.");
            })
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
}
