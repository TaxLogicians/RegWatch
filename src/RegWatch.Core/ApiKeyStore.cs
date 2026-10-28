using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RegWatch.Core;

/// <summary>
/// Holds one provider's API key encrypted with Windows DPAPI (current user only), under
/// %APPDATA%\RegWatch. When the store has an environment variable and it is set, that wins.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApiKeyStore
{
    public static readonly ApiKeyStore Anthropic = new("apikey.bin", "RegWatch.ApiKey.v1", "ANTHROPIC_API_KEY", "Anthropic API key");

    // No environment variable here: an OPENAI_API_KEY set for other tools must not be sent to
    // whichever server (OpenRouter, a local box, …) this provider is pointed at.
    public static readonly ApiKeyStore OpenAiCompatible = new("openai-compatible-key.bin", "RegWatch.OpenAiCompatible.v1", null, "API key");

    private readonly byte[] _entropy;

    private ApiKeyStore(string fileName, string entropy, string? envVar, string label)
    {
        KeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegWatch", fileName);
        _entropy = Encoding.UTF8.GetBytes(entropy);
        EnvVar = envVar;
        Label = label;
    }

    public string KeyPath { get; }
    public string? EnvVar { get; }
    public string Label { get; }

    public bool EnvVarIsSet => EnvVar is not null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    public string? Resolve()
    {
        if (EnvVarIsSet) return Environment.GetEnvironmentVariable(EnvVar!);
        if (!File.Exists(KeyPath)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public bool HasKey => Resolve() is not null;

    public void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), _entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, cipher);
    }

    public void Clear()
    {
        if (File.Exists(KeyPath)) File.Delete(KeyPath);
    }
}
