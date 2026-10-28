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

    /// <summary>Optional full path to claude.exe when it is not on PATH.</summary>
    public string? ClaudeCodePath { get; init; }

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
    /// <summary>Why the configured scorer cannot run, or null when it is ready.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Problem(AppSettings settings) => settings.Scorer switch
    {
        ScorerKind.ClaudeCode when ClaudeCodeScorer.Locate(settings.ClaudeCodePath) is null =>
            "Claude Code (claude.exe) was not found. Install it, or switch scoring to an API key.",
        ScorerKind.AnthropicApi when !ApiKeyStore.HasKey =>
            "No Anthropic API key. Choose Set API key, or switch scoring to your Claude subscription.",
        _ => null,
    };

    [SupportedOSPlatform("windows")]
    public static IScorer Create(AppSettings settings) =>
        Problem(settings) is { } problem
            ? throw new InvalidOperationException(problem)
            : settings.Scorer switch
            {
                ScorerKind.AnthropicApi => new ClaudeScorer(new AnthropicClient { ApiKey = ApiKeyStore.Resolve() }),
                _ => new ClaudeCodeScorer(ClaudeCodeScorer.Locate(settings.ClaudeCodePath)!),
            };
}
