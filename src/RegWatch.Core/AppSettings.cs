using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;

namespace RegWatch.Core;

public enum ScorerKind
{
    /// <summary>The Claude Code CLI, on the user's Claude subscription.</summary>
    ClaudeCode,

    /// <summary>The Claude API, billed to an Anthropic API key.</summary>
    AnthropicApi,

    /// <summary>Any server speaking the OpenAI chat-completions API: OpenAI, Ollama, LM Studio, Gemini, OpenRouter, …</summary>
    OpenAiCompatible,
}

/// <summary>How the OpenAI-compatible scorer asks for JSON; local models support less.</summary>
public enum StructuredOutputMode
{
    /// <summary>response_format json_schema (strict). OpenAI, Gemini, LM Studio, recent Ollama.</summary>
    JsonSchema,

    /// <summary>response_format json_object plus the schema in the prompt.</summary>
    JsonObject,

    /// <summary>No response_format; the schema goes in the prompt and the reply is parsed leniently.</summary>
    PromptOnly,
}

/// <summary>Light or dark window colours; System follows the Windows setting.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record OpenAiCompatibleSettings
{
    public string Preset { get; init; } = "OpenAI";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "";
    public StructuredOutputMode StructuredOutput { get; init; } = StructuredOutputMode.JsonSchema;

    /// <summary>Split runs into requests of this many items (IRB issues count as one each); 0 sends everything at once.</summary>
    public int ItemsPerRequest { get; init; }

    public int TimeoutMinutes { get; init; } = 30;
}

/// <summary>Per-user settings in %APPDATA%\RegWatch\settings.json.</summary>
public sealed record AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ScorerKind Scorer { get; init; } = ScorerKind.ClaudeCode;

    /// <summary>Lenient: an unknown value (hand edit, newer version) falls back to System instead of failing the whole file.</summary>
    [JsonConverter(typeof(LenientEnumConverter<AppTheme>))]
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Optional full path to claude.exe when it is not on PATH.</summary>
    public string? ClaudeCodePath { get; init; }

    /// <summary>Model alias or name passed to <c>claude --model</c>.</summary>
    public string ClaudeCodeModel { get; init; } = ClaudeCodeScorer.DefaultModel;

    public string AnthropicModel { get; init; } = ClaudeScorer.DefaultModel;

    public OpenAiCompatibleSettings OpenAi { get; init; } = new();

    /// <summary>Commentary feeds; null (settings from before feeds existed) means the built-in defaults.</summary>
    public IReadOnlyList<FeedSource>? Feeds { get; init; }

    /// <summary>Saved feeds, plus any built-in source added in a later version (so it appears without a reset).</summary>
    [JsonIgnore]
    public IReadOnlyList<FeedSource> EffectiveFeeds => Feeds is null
        ? FeedSource.Defaults
        : [.. Feeds, .. FeedSource.Defaults.Where(d => Feeds.All(f => !string.Equals(f.Url, d.Url, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>Show a Windows notification when a scheduled (headless) run finishes.</summary>
    public bool NotifyAfterScheduledRun { get; init; } = true;

    /// <summary>Send the latest "scored wrong" marks to the scorer as calibration examples.</summary>
    public bool UseFeedbackInScoring { get; init; } = true;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegWatch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath) ? Parse(File.ReadAllText(SettingsPath)) : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, ToJson());
    }

    internal static AppSettings Parse(string json) => JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new();

    internal string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>Reads an enum by name (any case) or number, returning the default for anything unrecognised.</summary>
public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named)
            && Enum.IsDefined(named))
            return named;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(T), n))
            return (T)Enum.ToObject(typeof(T), n);
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public static class ScorerFactory
{
    public static string Describe(ScorerKind kind) => kind switch
    {
        ScorerKind.ClaudeCode => "Claude subscription (Claude Code)",
        ScorerKind.AnthropicApi => "Anthropic API key",
        _ => "OpenAI-compatible / local AI",
    };

    /// <summary>Why the configured scorer cannot run, or null when it is ready.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Problem(AppSettings settings) => settings.Scorer switch
    {
        ScorerKind.ClaudeCode when ClaudeCodeScorer.Locate(settings.ClaudeCodePath) is null =>
            "Claude Code (claude.exe) was not found. Install it, or pick another scorer under Settings.",
        ScorerKind.AnthropicApi when !ApiKeyStore.Anthropic.HasKey =>
            "No Anthropic API key. Set one under Settings > Advanced scoring settings, or pick another scorer.",
        ScorerKind.OpenAiCompatible => OpenAiCompatibleScorer.Problem(settings.OpenAi),
        _ => null,
    };

    [SupportedOSPlatform("windows")]
    public static IScorer Create(AppSettings settings) =>
        Problem(settings) is { } problem
            ? throw new InvalidOperationException(problem)
            : settings.Scorer switch
            {
                ScorerKind.AnthropicApi => new ClaudeScorer(
                    new AnthropicClient { ApiKey = ApiKeyStore.Anthropic.Resolve() }, settings.AnthropicModel),
                ScorerKind.OpenAiCompatible => new OpenAiCompatibleScorer(
                    settings.OpenAi, ApiKeyStore.OpenAiCompatible.Resolve()),
                _ => new ClaudeCodeScorer(ClaudeCodeScorer.Locate(settings.ClaudeCodePath)!, settings.ClaudeCodeModel),
            };
}
