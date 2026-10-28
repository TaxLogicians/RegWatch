using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RegWatch.Core;

/// <summary>A starting point for the OpenAI-compatible settings; every field stays editable.</summary>
public sealed record ProviderPreset(string Name, string BaseUrl, string ModelHint, bool Local, string Notes);

/// <summary>
/// Scores through any server that speaks the OpenAI chat-completions API: OpenAI itself,
/// local runners (Ollama, LM Studio, llama.cpp), Gemini's compatibility endpoint, OpenRouter.
/// Same prompt and schema as the Claude scorers, but there is no web search, so the
/// final-rule news check is skipped and noted.
/// </summary>
public sealed class OpenAiCompatibleScorer(OpenAiCompatibleSettings settings, string? apiKey, HttpClient? http = null) : IScorer
{
    public static readonly IReadOnlyList<ProviderPreset> Presets =
    [
        new("OpenAI", "https://api.openai.com/v1", "a current GPT model name from platform.openai.com", false,
            "Needs an OpenAI API key."),
        new("Ollama (local)", "http://localhost:11434/v1", "e.g. qwen2.5:14b or llama3.1:8b (as shown by `ollama list`)", true,
            "Runs on this PC. Raise the context length (OLLAMA_CONTEXT_LENGTH) or set items per request: a weekly payload is 5k–40k tokens."),
        new("LM Studio (local)", "http://localhost:1234/v1", "the model identifier shown in LM Studio's server tab", true,
            "Runs on this PC. Start the local server in LM Studio and load a model with a large enough context."),
        new("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "a current Gemini model name", false,
            "Needs a Google AI Studio API key."),
        new("OpenRouter", "https://openrouter.ai/api/v1", "provider/model, e.g. as listed on openrouter.ai/models", false,
            "Needs an OpenRouter API key; routes to many vendors."),
        new("Custom", "", "the model name your server expects", false,
            "Any server with an OpenAI-style /chat/completions endpoint."),
    ];

    private static readonly HttpClient SharedHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http = http ?? SharedHttp;

    public string Description => $"{settings.Preset} ({settings.Model})";

    public static string? Problem(OpenAiCompatibleSettings s)
    {
        if (!Uri.TryCreate(s.BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return "Set a base URL for the OpenAI-compatible server under Advanced (e.g. http://localhost:11434/v1).";
        if (string.IsNullOrWhiteSpace(s.Model))
            return "Set a model name for the OpenAI-compatible server under Advanced.";
        return null;
    }

    /// <summary>True when the URL points at this machine, so a key (if any) never leaves it.</summary>
    public static bool IsLocal(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    public async Task<ScoringResult> ScoreAsync(
        string profile, FetchResult fetched, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var batches = ScoringBatches.Split(fetched, settings.ItemsPerRequest);
        var results = new List<ScoringResult>();
        for (var i = 0; i < batches.Count; i++)
        {
            log?.Report(batches.Count == 1
                ? $"Scoring with {Description}…"
                : $"Scoring batch {i + 1} of {batches.Count} with {Description}…");
            results.Add(await CallAsync(profile, batches[i], log, ct));
        }

        var merged = batches.Count == 1 ? results[0] : ScoringBatches.Merge(results);
        return merged with
        {
            Notes = [.. merged.Notes, $"Scored by {Description}: no web search, so the final-rule news check was not run."],
        };
    }

    private async Task<ScoringResult> CallAsync(string profile, FetchResult batch, IProgress<string>? log, CancellationToken ct)
    {
        var body = BuildRequest(settings, profile, ScoringPrompt.BuildPayload(batch));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, settings.TimeoutMinutes)));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{settings.Preset} did not answer within {settings.TimeoutMinutes} minutes.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach {settings.BaseUrl} ({ex.Message}). " +
                (IsLocal(settings.BaseUrl) ? "Is the local server running?" : "Check the base URL."));
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{settings.Preset} returned HTTP {(int)response.StatusCode}: {Trim(text)}");
            return ParseResponse(text, log);
        }
    }

    public static JsonObject BuildRequest(OpenAiCompatibleSettings s, string profile, string payload)
    {
        var system = ScoringPrompt.SystemPrompt(profile);
        if (s.StructuredOutput != StructuredOutputMode.JsonSchema)
            system += "\n\nReply with only one JSON object (no prose, no code fence) matching this JSON Schema:\n" + ScoringPrompt.SchemaJson;

        var body = new JsonObject
        {
            ["model"] = s.Model.Trim(),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = payload }),
        };

        if (s.StructuredOutput == StructuredOutputMode.JsonSchema)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "regwatch_digest",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(ScoringPrompt.SchemaJson),
                },
            };
        else if (s.StructuredOutput == StructuredOutputMode.JsonObject)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };

        return body;
    }

    public static ScoringResult ParseResponse(string json, IProgress<string>? log = null)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Empty response from the server.");
        var choice = root["choices"]?[0] ?? throw new InvalidOperationException($"No choices in the response: {Trim(json)}");
        var message = choice["message"];

        if (message?["refusal"] is JsonValue refusal && refusal.TryGetValue<string>(out var why) && why.Length > 0)
            throw new InvalidOperationException($"The model declined to score: {Trim(why)}");
        if (choice["finish_reason"]?.GetValue<string>() == "length")
            throw new InvalidOperationException("The model ran out of output tokens before finishing the JSON. Lower items per request.");

        if (root["usage"] is JsonObject usage)
            log?.Report($"Scored: {usage["prompt_tokens"]} input / {usage["completion_tokens"]} output tokens.");

        var content = message?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The response had no message content: {Trim(json)}");
        try
        {
            return ScoringPrompt.ParseResult(ExtractJson(content));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The model's reply was not valid digest JSON ({ex.Message}). Try another structured-output mode or a larger model. Reply began: {Trim(content)}");
        }
    }

    /// <summary>Pulls the JSON object out of a reply that may be fenced or have prose around it.</summary>
    internal static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s.Trim();
}

/// <summary>Splits a run into smaller requests and merges the answers, for models with small context windows.</summary>
public static class ScoringBatches
{
    public static IReadOnlyList<FetchResult> Split(FetchResult fetched, int itemsPerRequest)
    {
        var total = fetched.IrbIssues.Count + fetched.FedRegDocuments.Count + fetched.CommentaryItems.Count;
        if (itemsPerRequest <= 0 || total <= itemsPerRequest)
            return [fetched];

        var batches = new List<FetchResult>();
        // Each IRB issue goes alone: its highlights list ~5–20 items already.
        foreach (var issue in fetched.IrbIssues)
            batches.Add(new FetchResult([issue], [], fetched.Problems));
        foreach (var chunk in fetched.FedRegDocuments.Chunk(itemsPerRequest))
            batches.Add(new FetchResult([], chunk, fetched.Problems));
        foreach (var chunk in fetched.CommentaryItems.Chunk(itemsPerRequest))
            batches.Add(new FetchResult([], [], fetched.Problems, chunk));
        return batches;
    }

    /// <summary>Concatenates batch results; an item reported by two batches keeps its higher score.</summary>
    public static ScoringResult Merge(IReadOnlyList<ScoringResult> results)
    {
        var items = results.SelectMany(r => r.Items)
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Cite) ? i.Url : i.Cite, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.Score).First())
            .ToList();

        var threes = items.Count(i => i.Score == 3);
        var twos = items.Count(i => i.Score == 2);
        var top = items.OrderByDescending(i => i.Score).FirstOrDefault(i => i.Score >= 2);
        var headline = top is null
            ? "Nothing scores 2 or higher this run."
            : $"{threes} item(s) score 3 and {twos} score 2; the top item is {top.Cite}.";

        return new ScoringResult
        {
            Headline = headline,
            Notes = [.. results.SelectMany(r => r.Notes).Distinct(), $"Scored in {results.Count} batches; IRB/Federal Register duplicates across batches may not be merged."],
            Items = items,
            Press = results.SelectMany(r => r.Press).GroupBy(p => p.Url).Select(g => g.OrderByDescending(p => p.Score).First()).ToList(),
        };
    }
}
