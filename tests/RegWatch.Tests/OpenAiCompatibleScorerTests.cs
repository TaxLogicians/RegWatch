using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RegWatch.Core;

namespace RegWatch.Tests;

public class OpenAiCompatibleScorerTests
{
    private const string Digest = """{"headline":"h","notes":[],"items":[{"cite":"Notice 2026-61","source":"IRB 2026-43","type":"Notice","irc":"§ 871(m)","comment_deadline":null,"url":"u","score":2,"buckets":["fatca_withholding"],"plain_english":"p","why_you_care":"w"}]}""";

    private static string Completion(string content, string finish = "stop") => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["finish_reason"] = finish,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 1200, ["completion_tokens"] = 300 },
    }.ToJsonString();

    private static FetchResult Fetched(int frDocs = 1, int irbIssues = 0) => new(
        Enumerable.Range(0, irbIssues).Select(i => new IrbIssue($"2026-{40 + i}", "u", "c", "h")).ToList(),
        Enumerable.Range(0, frDocs).Select(i => new FedRegDocument($"2026-{20000 + i}", "irs", "Notice", "t", null, null, "2026-10-28", null, null, "u", [])).ToList(),
        []);

    [Fact]
    public void Request_JsonSchemaMode_UsesStrictResponseFormat()
    {
        var body = OpenAiCompatibleScorer.BuildRequest(new() { Model = "m" }, "PROFILE", "{}");

        Assert.Equal("m", body["model"]!.GetValue<string>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Contains("PROFILE", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("Reply with only one JSON object", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(StructuredOutputMode.JsonObject, "json_object")]
    [InlineData(StructuredOutputMode.PromptOnly, null)]
    public void Request_WeakerModes_PutSchemaInPrompt(StructuredOutputMode mode, string? formatType)
    {
        var body = OpenAiCompatibleScorer.BuildRequest(new() { Model = "m", StructuredOutput = mode }, "P", "{}");

        Assert.Equal(formatType, body["response_format"]?["type"]?.GetValue<string>());
        Assert.Contains("matching this JSON Schema", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Response_ParsesPlainFencedAndChattyReplies()
    {
        Assert.Equal(2, OpenAiCompatibleScorer.ParseResponse(Completion(Digest)).Items[0].Score);
        Assert.Equal("h", OpenAiCompatibleScorer.ParseResponse(Completion("```json\n" + Digest + "\n```")).Headline);
        Assert.Equal("h", OpenAiCompatibleScorer.ParseResponse(Completion("Here is the digest:\n" + Digest + "\nHope this helps.")).Headline);
    }

    [Fact]
    public void Response_ExplainsTruncationRefusalAndBadJson()
    {
        Assert.Contains("items per request", Assert.Throws<InvalidOperationException>(
            () => OpenAiCompatibleScorer.ParseResponse(Completion("{\"headline\":", "length"))).Message);

        var refusal = """{"choices":[{"finish_reason":"stop","message":{"content":null,"refusal":"no"}}]}""";
        Assert.Contains("declined", Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleScorer.ParseResponse(refusal)).Message);

        Assert.Contains("not valid digest JSON", Assert.Throws<InvalidOperationException>(
            () => OpenAiCompatibleScorer.ParseResponse(Completion("I cannot help with tax questions."))).Message);
    }

    [Fact]
    public async Task Score_PostsToChatCompletions_WithKeyOnlyWhenSet()
    {
        var handler = new FakeHandler(_ => Completion(Digest));
        var scorer = new OpenAiCompatibleScorer(new() { Preset = "Ollama (local)", BaseUrl = "http://localhost:11434/v1/", Model = "qwen" }, apiKey: null, new HttpClient(handler));

        var result = await scorer.ScoreAsync("P", Fetched());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.Uri);
        Assert.Null(request.Authorization);
        Assert.Contains(result.Notes, n => n.Contains("no web search"));

        var keyed = new FakeHandler(_ => Completion(Digest));
        await new OpenAiCompatibleScorer(new() { BaseUrl = "https://api.openai.com/v1", Model = "m" }, "sk-test", new HttpClient(keyed)).ScoreAsync("P", Fetched());
        Assert.Equal("Bearer sk-test", keyed.Requests[0].Authorization);
    }

    [Fact]
    public async Task Score_HttpErrorIncludesStatusAndBody()
    {
        var handler = new FakeHandler(_ => """{"error":{"message":"model 'nope' not found"}}""", HttpStatusCode.NotFound);
        var scorer = new OpenAiCompatibleScorer(new() { Preset = "Ollama (local)", BaseUrl = "http://localhost:11434/v1", Model = "nope" }, null, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scorer.ScoreAsync("P", Fetched()));
        Assert.Contains("HTTP 404", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Score_InBatches_SendsSeveralRequestsAndMerges()
    {
        var handler = new FakeHandler(_ => Completion(Digest));
        var scorer = new OpenAiCompatibleScorer(new() { BaseUrl = "http://localhost:1234/v1", Model = "m", ItemsPerRequest = 10 }, null, new HttpClient(handler));

        var result = await scorer.ScoreAsync("P", Fetched(frDocs: 25, irbIssues: 2));

        Assert.Equal(2 + 3, handler.Requests.Count); // one per IRB issue + ceil(25 / 10)
        Assert.Single(result.Items); // the same cite from every batch collapses to one
        Assert.Contains(result.Notes, n => n.Contains("5 batches"));
    }

    [Fact]
    public void Split_LeavesSmallRunsWhole()
    {
        Assert.Single(ScoringBatches.Split(Fetched(frDocs: 5), 0));
        Assert.Single(ScoringBatches.Split(Fetched(frDocs: 5), 10));
        Assert.Equal(3, ScoringBatches.Split(Fetched(frDocs: 5), 2).Count);
    }

    [Fact]
    public void Merge_KeepsHighestScoreAndRebuildsHeadline()
    {
        ScoredItem Item(string cite, int score) => new() { Cite = cite, Score = score, Url = "u" };
        var merged = ScoringBatches.Merge(
        [
            new ScoringResult { Items = [Item("T.D. 10060", 1), Item("Notice 2026-61", 2)] },
            new ScoringResult { Items = [Item("T.D. 10060", 3)] },
        ]);

        Assert.Equal(3, merged.Items.Single(i => i.Cite == "T.D. 10060").Score);
        Assert.Contains("1 item(s) score 3 and 1 score 2", merged.Headline);
    }

    [Theory]
    [InlineData("", "m", "base URL")]
    [InlineData("ftp://x", "m", "base URL")]
    [InlineData("http://localhost:11434/v1", "", "model name")]
    [InlineData("http://localhost:11434/v1", "m", null)]
    public void Problem_ChecksUrlAndModel(string url, string model, string? expected)
    {
        var problem = OpenAiCompatibleScorer.Problem(new() { BaseUrl = url, Model = model });
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("https://api.openai.com/v1", false)]
    public void IsLocal_RecognisesThisMachine(string url, bool local) =>
        Assert.Equal(local, OpenAiCompatibleScorer.IsLocal(url));

    [Fact]
    public void Settings_FromBeforeProviders_LoadWithDefaults()
    {
        // settings.json as written by the previous version: only the scorer choice.
        var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var old = JsonSerializer.Deserialize<AppSettings>("""{"Scorer":"ClaudeCode","ClaudeCodePath":null}""", options)!;

        Assert.Equal(ScorerKind.ClaudeCode, old.Scorer);
        Assert.Equal("opus", old.ClaudeCodeModel);
        Assert.Equal("claude-opus-5-5", old.AnthropicModel);
        Assert.Equal(StructuredOutputMode.JsonSchema, old.OpenAi.StructuredOutput);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(respond(request)) };
        }
    }
}
