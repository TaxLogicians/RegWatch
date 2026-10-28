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
    IReadOnlyList<string> Problems);

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

/// <summary>The scorer's full answer for a run.</summary>
public sealed record ScoringResult
{
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = [];
    [JsonPropertyName("items")] public IReadOnlyList<ScoredItem> Items { get; init; } = [];
}
