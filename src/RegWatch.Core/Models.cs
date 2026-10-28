using System.Text.Json.Serialization;

namespace RegWatch.Core;

/// <summary>One Internal Revenue Bulletin issue, reduced to its table of contents and synopses.</summary>
public sealed record IrbIssue(string Issue, string Url, string Contents, string Highlights);

/// <summary>One Federal Register document as returned by the documents.json API.</summary>
public sealed record FedRegDocument(
    [property: JsonPropertyName("document_number")] string DocumentNumber,
    [property: JsonPropertyName("agency")] string Agency,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("abstract")] string? Abstract,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("publication_date")] string? PublicationDate,
    [property: JsonPropertyName("effective_on")] string? EffectiveOn,
    [property: JsonPropertyName("comments_close_on")] string? CommentsCloseOn,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("regulation_id_numbers")] IReadOnlyList<string>? RegulationIdNumbers);

/// <summary>Everything fetched for one run, plus any fetch problems to report in the digest.</summary>
public sealed record FetchResult(
    IReadOnlyList<IrbIssue> IrbIssues,
    IReadOnlyList<FedRegDocument> FedRegDocuments,
    IReadOnlyList<string> Problems,
    IReadOnlyList<FeedItem>? Commentary = null,
    IReadOnlyDictionary<string, DateTimeOffset>? FeedBookmarks = null)
{
    /// <summary>New articles from the commentary feeds (secondary sources).</summary>
    public IReadOnlyList<FeedItem> CommentaryItems => Commentary ?? [];

    /// <summary>Newest article date per feed URL, for feeds that were read successfully.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> FeedBookmarkUpdates => FeedBookmarks ?? new Dictionary<string, DateTimeOffset>();

    public bool IsEmpty => IrbIssues.Count == 0 && FedRegDocuments.Count == 0 && CommentaryItems.Count == 0;
}

/// <summary>One scored item, as returned by the scorer (snake_case matches the JSON schema).</summary>
public sealed record ScoredItem
{
    [JsonPropertyName("cite")] public string Cite { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("irc")] public string Irc { get; init; } = "not stated";
    [JsonPropertyName("comment_deadline")] public string? CommentDeadline { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("buckets")] public IReadOnlyList<string> Buckets { get; init; } = [];
    [JsonPropertyName("plain_english")] public string PlainEnglish { get; init; } = "";
    [JsonPropertyName("why_you_care")] public string WhyYouCare { get; init; } = "";
}

/// <summary>One scored commentary article (The Tax Adviser, JofA, a custom feed).</summary>
public sealed record PressItem
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("published")] public string? Published { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("buckets")] public IReadOnlyList<string> Buckets { get; init; } = [];

    /// <summary>Cite of the IRB / Federal Register item the article discusses (e.g. "T.D. 10060"), when it names one.</summary>
    [JsonPropertyName("related_cite")] public string? RelatedCite { get; init; }

    [JsonPropertyName("why_you_care")] public string WhyYouCare { get; init; } = "";
}

/// <summary>The scorer's full answer for a run.</summary>
public sealed record ScoringResult
{
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = [];
    [JsonPropertyName("items")] public IReadOnlyList<ScoredItem> Items { get; init; } = [];

    /// <summary>Commentary articles; missing in digests scored before feeds existed.</summary>
    [JsonPropertyName("press")] public IReadOnlyList<PressItem> Press { get; init; } = [];
}
