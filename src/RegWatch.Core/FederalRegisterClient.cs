using System.Text.Json;
using System.Text.Json.Nodes;

namespace RegWatch.Core;

/// <summary>Queries the Federal Register documents API for one or more agencies.</summary>
public sealed class FederalRegisterClient(HttpClient http)
{
    public static readonly IReadOnlyList<string> DefaultAgencies =
    [
        "internal-revenue-service",
        "treasury-department",
        // FBAR / BOI items; see the 2026-08-14 FinCEN lesson in STATE.json notes.
        "financial-crimes-enforcement-network",
    ];

    private static readonly string[] Fields =
    [
        "document_number", "type", "title", "abstract", "action", "publication_date",
        "effective_on", "comments_close_on", "html_url", "regulation_id_numbers",
    ];

    public static string BuildUrl(string agency, string sinceDate)
    {
        var fields = string.Concat(Fields.Select(f => $"&fields[]={f}"));
        return "https://www.federalregister.gov/api/v1/documents.json"
             + $"?conditions[agencies][]={agency}"
             + $"&conditions[publication_date][gte]={sinceDate}"
             + "&order=newest&per_page=100" + fields;
    }

    /// <summary>All documents for one agency published on or after <paramref name="sinceDate"/>, following pages.</summary>
    public async Task<IReadOnlyList<FedRegDocument>> GetDocumentsAsync(
        string agency, string sinceDate, CancellationToken ct = default)
    {
        var results = new List<FedRegDocument>();
        string? url = BuildUrl(agency, sinceDate);

        // Guard against a runaway cursor; 20 pages is 2,000 documents.
        for (var page = 0; url is not null && page < 20; page++)
        {
            var json = await http.GetStringAsync(url, ct);
            var (docs, next) = ParsePage(json, agency);
            results.AddRange(docs);
            url = next;
        }

        return results;
    }

    public static (IReadOnlyList<FedRegDocument> Documents, string? NextPageUrl) ParsePage(string json, string agency)
    {
        var root = JsonNode.Parse(json) ?? throw new JsonException("Empty Federal Register response.");
        var docs = new List<FedRegDocument>();

        foreach (var node in root["results"]?.AsArray() ?? [])
        {
            if (node is null) continue;
            var number = node["document_number"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(number)) continue;

            docs.Add(new FedRegDocument(
                number,
                agency,
                Str(node, "type"),
                Str(node, "title"),
                Str(node, "abstract"),
                Str(node, "action"),
                Str(node, "publication_date"),
                Str(node, "effective_on"),
                Str(node, "comments_close_on"),
                Str(node, "html_url"),
                node["regulation_id_numbers"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? "")
                    .Where(s => s.Length > 0)
                    .ToList() ?? []));
        }

        return (docs, Str(root, "next_page_url"));
    }

    /// <summary>Merges per-agency results; the same document appears under IRS and Treasury.</summary>
    public static IReadOnlyList<FedRegDocument> Dedup(IEnumerable<FedRegDocument> docs) =>
        docs.GroupBy(d => d.DocumentNumber)
            .Select(g => g.First() with { Agency = string.Join(", ", g.Select(d => d.Agency).Distinct()) })
            .OrderByDescending(d => d.PublicationDate)
            .ToList();

    private static string? Str(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
