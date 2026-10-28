using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RegWatch.Core;

/// <summary>A commentary feed: built-in (The Tax Adviser, JofA) or added by the user.</summary>
public sealed record FeedSource
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";

    /// <summary>Keep only items in one of these categories (case-insensitive); empty keeps everything.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    public bool Enabled { get; init; } = true;
    public bool BuiltIn { get; init; }

    public static IReadOnlyList<FeedSource> Defaults { get; } =
    [
        new() { Name = "The Tax Adviser", Url = "https://www.thetaxadviser.com/feed/", BuiltIn = true },
        new() { Name = "Journal of Accountancy", Url = "https://www.journalofaccountancy.com/feed/", Categories = ["TAX"], BuiltIn = true },
        new() { Name = "IRS news releases", Url = IrsNewsClient.CurrentMonthUrl, BuiltIn = true },
    ];
}

/// <summary>One article from a commentary feed; only the headline, a short summary and the link are kept.</summary>
public sealed record FeedItem(
    string Source,
    string Id,
    string Title,
    string Url,
    DateTimeOffset? Published,
    IReadOnlyList<string> Categories,
    string Summary);

public sealed record FeedDiscovery(string FeedUrl, string Title, IReadOnlyList<FeedItem> Sample, IReadOnlyList<string> Categories);

/// <summary>Reads RSS 2.0 and Atom feeds, pages back through WordPress feeds, and finds a site's feed.</summary>
public sealed partial class FeedClient(HttpClient http)
{
    public const int SummaryLimit = 600;
    public const int MaxPages = 5;

    /// <summary>
    /// New items since <paramref name="since"/> (or the last <paramref name="firstRunDays"/> days on a
    /// first run), filtered by the source's categories. WordPress feeds only show the latest page,
    /// so older pages are read until the bookmark is reached.
    /// </summary>
    public async Task<IReadOnlyList<FeedItem>> GetItemsAsync(
        FeedSource source, DateTimeOffset? since, int firstRunDays = 14, CancellationToken ct = default)
    {
        var cutoff = since ?? DateTimeOffset.UtcNow.AddDays(-firstRunDays);
        var items = new List<FeedItem>();

        if (IrsNewsClient.Handles(source.Url))
            items.AddRange(await IrsNewsClient.GetItemsAsync(http, source.Name, cutoff, DateTime.Today, ct));
        else
            await ReadFeedPagesAsync(source, cutoff, items, ct);

        return items
            .Where(i => i.Published is null || i.Published > cutoff)
            .Where(i => source.Categories.Count == 0
                || i.Categories.Any(c => source.Categories.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToList();
    }

    private async Task ReadFeedPagesAsync(FeedSource source, DateTimeOffset cutoff, List<FeedItem> items, CancellationToken ct)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = page == 1 ? source.Url : PageUrl(source.Url, page);
            string xml;
            try
            {
                xml = await http.GetStringAsync(url, ct);
            }
            catch (HttpRequestException ex) when (page > 1 && ex.StatusCode == HttpStatusCode.NotFound)
            {
                break; // ran off the end of the archive
            }

            var parsed = Parse(xml, source.Name);
            // A page that repeats earlier items (some servers ignore ?paged=) ends the walk.
            var isNew = parsed.Any(p => items.All(i => i.Id != p.Id));
            items.AddRange(parsed);

            var reachedBookmark = parsed.Count == 0 || parsed.Any(i => i.Published is { } p && p <= cutoff);
            if (reachedBookmark || !isNew || !IsWordPress(source.Url, xml)) break;
        }
    }

    public static IReadOnlyList<FeedItem> Parse(string xml, string sourceName)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var root = doc.Root ?? throw new XmlException("Empty feed.");

        if (root.Name.LocalName == "feed") // Atom
        {
            XNamespace a = root.Name.Namespace;
            return root.Elements(a + "entry").Select(e =>
            {
                var link = e.Elements(a + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate");
                var url = (string?)link?.Attribute("href") ?? "";
                return new FeedItem(
                    sourceName,
                    (string?)e.Element(a + "id") ?? url,
                    Clean((string?)e.Element(a + "title")),
                    url,
                    Date((string?)e.Element(a + "published") ?? (string?)e.Element(a + "updated")),
                    e.Elements(a + "category").Select(c => (string?)c.Attribute("term") ?? "").Where(c => c.Length > 0).ToList(),
                    Summary((string?)e.Element(a + "summary") ?? (string?)e.Element(a + "content")));
            }).ToList();
        }

        var channel = root.Element("channel") ?? root; // RSS 2.0 (and RSS 1.0-style roots)
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        return channel.Elements().Concat(root.Elements()).Where(e => e.Name.LocalName == "item").Distinct().Select(e =>
        {
            var url = Clean((string?)e.Elements().FirstOrDefault(x => x.Name.LocalName == "link"));
            var guid = Clean((string?)e.Elements().FirstOrDefault(x => x.Name.LocalName == "guid"));
            var date = (string?)e.Elements().FirstOrDefault(x => x.Name.LocalName is "pubDate" or "date");
            return new FeedItem(
                sourceName,
                guid.Length > 0 ? guid : url,
                Clean((string?)e.Elements().FirstOrDefault(x => x.Name.LocalName == "title")),
                url,
                Date(date),
                e.Elements().Where(x => x.Name.LocalName == "category").Select(x => Clean(x.Value)).Where(c => c.Length > 0).ToList(),
                Summary((string?)e.Elements().FirstOrDefault(x => x.Name.LocalName == "description")
                        ?? (string?)e.Element(content + "encoded")));
        }).ToList();
    }

    /// <summary>
    /// Finds the feed for a site or feed URL: the URL itself if it is a feed, else the page's
    /// &lt;link rel="alternate"&gt;, else the usual paths (/feed/, /rss, /atom.xml …).
    /// </summary>
    public async Task<FeedDiscovery> DiscoverAsync(string input, CancellationToken ct = default)
    {
        var start = input.Trim();
        if (!start.Contains("://")) start = "https://" + start;
        if (!Uri.TryCreate(start, UriKind.Absolute, out var uri))
            throw new ArgumentException($"\"{input}\" is not a web address.");

        var tried = new List<string>();
        async Task<FeedDiscovery?> TryFeed(string url)
        {
            tried.Add(url);
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!LooksLikeFeed(body)) return null;

                var items = Parse(body, uri.Host);
                var title = Clean(XDocument.Parse(body).Root?.Descendants().FirstOrDefault(d => d.Name.LocalName == "title")?.Value) is { Length: > 0 } t ? t : uri.Host;
                var final = response.RequestMessage?.RequestUri?.ToString() ?? url;
                return new FeedDiscovery(final, title, items.Take(8).ToList(),
                    items.SelectMany(i => i.Categories).GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).Select(g => g.Key).ToList());
            }
            catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException)
            {
                return null;
            }
        }

        string page;
        try
        {
            using var response = await http.GetAsync(uri, ct);
            page = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"{uri.Host} answered HTTP {(int)response.StatusCode}" +
                    (response.StatusCode == HttpStatusCode.Forbidden ? "; the site blocks automated readers." : "."));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach {uri.Host}: {ex.Message}");
        }

        if (LooksLikeFeed(page) && await TryFeed(uri.ToString()) is { } direct) return direct;

        foreach (Match m in AlternateLink().Matches(page))
        {
            var href = HrefAttr().Match(m.Value);
            if (href.Success && Uri.TryCreate(uri, WebUtility.HtmlDecode(href.Groups[1].Value), out var feedUri)
                && await TryFeed(feedUri.ToString()) is { } linked)
                return linked;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var basePath = uri.AbsolutePath.TrimEnd('/');
        foreach (var path in new[] { basePath + "/feed/", "/feed/", "/rss", "/feed", "/rss.xml", "/atom.xml", "/index.xml" }.Distinct())
            if (await TryFeed(origin + path) is { } guessed) return guessed;

        throw new InvalidOperationException(
            $"No RSS or Atom feed found for {uri.Host}. Tried the page's feed links and {string.Join(", ", tried.Take(4))}. " +
            "If the site has a feed, paste its address directly.");
    }

    /// <summary>
    /// The Tax Adviser and JofA share AICPA news stories: keep one copy per headline, naming both
    /// sources and remembering every id (joined with U+0001) so each feed's copy is marked seen.
    /// </summary>
    public static List<FeedItem> MergeDuplicates(IEnumerable<FeedItem> items) =>
        items.GroupBy(i => Whitespace().Replace(i.Title.ToLowerInvariant(), " ").Trim())
            .Select(g => g.First() with
            {
                Source = string.Join(" / ", g.Select(i => i.Source).Distinct()),
                Id = string.Join("\u0001", g.Select(i => i.Id).Distinct()),
            })
            .ToList();

    internal static bool LooksLikeFeed(string body)
    {
        var head = body.Length > 2000 ? body[..2000] : body;
        return head.Contains("<rss", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<feed", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<rdf:RDF", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWordPress(string url, string xml) =>
        xml.Contains("com-wordpress:feed-additions", StringComparison.Ordinal)
        || xml.Contains("wordpress.org", StringComparison.OrdinalIgnoreCase)
        || new Uri(url).AbsolutePath.TrimEnd('/').EndsWith("/feed", StringComparison.OrdinalIgnoreCase);

    internal static string PageUrl(string feedUrl, int page) =>
        feedUrl + (feedUrl.Contains('?') ? "&" : "?") + "paged=" + page.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        // RFC 822 with a named zone ("GMT", "EST") that DateTimeOffset does not parse.
        var noZone = Regex.Replace(s, @"\s+[A-Z]{2,4}$", "");
        return DateTimeOffset.TryParse(noZone, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d) ? d : null;
    }

    private static string Summary(string? html)
    {
        var text = Clean(Tag().Replace(html ?? "", " "));
        // WordPress appends "The post X appeared first on Y."; drop it.
        text = AppearedFirst().Replace(text, "").Trim();
        return text.Length > SummaryLimit ? text[..SummaryLimit].TrimEnd() + "…" : text;
    }

    private static string Clean(string? s) =>
        Whitespace().Replace(WebUtility.HtmlDecode(s ?? ""), " ").Trim();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"The post .{1,300} appeared first on .{1,120}\.$")]
    private static partial Regex AppearedFirst();

    [GeneratedRegex(@"<link\b[^>]*\brel=[""']?alternate[""']?[^>]*\btype=[""']?application/(rss|atom)\+xml[^>]*>|<link\b[^>]*\btype=[""']?application/(rss|atom)\+xml[^>]*\brel=[""']?alternate[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AlternateLink();

    [GeneratedRegex(@"\bhref=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttr();
}
