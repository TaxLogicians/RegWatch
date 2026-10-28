using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RegWatch.Core;

/// <summary>
/// Holds the Anthropic API key encrypted with Windows DPAPI (current user only), under
/// %APPDATA%\RegWatch. ANTHROPIC_API_KEY, when set, takes precedence.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ApiKeyStore
{
    private static readonly byte[] Entropy = "RegWatch.ApiKey.v1"u8.ToArray();

    public static string KeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegWatch", "apikey.bin");

    public static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (!File.Exists(KeyPath)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static bool HasKey => Resolve() is not null;

    public static void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, cipher);
    }

    public static void Clear()
    {
        if (File.Exists(KeyPath)) File.Delete(KeyPath);
    }
}
