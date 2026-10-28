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

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Optional full path to claude.exe when it is not on PATH.</summary>
    public string? ClaudeCodePath { get; init; }

    /// <summary>Model alias or name passed to <c>claude --model</c>.</summary>
    public string ClaudeCodeModel { get; init; } = ClaudeCodeScorer.DefaultModel;

    public string AnthropicModel { get; init; } = ClaudeScorer.DefaultModel;

    public OpenAiCompatibleSettings OpenAi { get; init; } = new();

    /// <summary>Commentary feeds; null (settings from before feeds existed) means the built-in defaults.</summary>
    public IReadOnlyList<FeedSource>? Feeds { get; init; }

    [JsonIgnore]
    public IReadOnlyList<FeedSource> EffectiveFeeds => Feeds ?? FeedSource.Defaults;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegWatch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
    }
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
            "Claude Code (claude.exe) was not found. Install it, or pick another scorer under Advanced.",
        ScorerKind.AnthropicApi when !ApiKeyStore.Anthropic.HasKey =>
            "No Anthropic API key. Set one under Advanced, or pick another scorer.",
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
