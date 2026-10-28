using System.Net;
using System.Text.Json;

namespace RegWatch.Core;

public sealed record UpdateInfo(bool Available, string? LatestVersion, string? Url, string Message);

/// <summary>Compares this build with the latest GitHub release of RegWatch.</summary>
public static class UpdateCheck
{
    public const string Repo = "Maubec-Deeds/RegWatch";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    private const string PrivateRepoMessage =
        "RegWatch could not see its releases. While the repository is private, GitHub only shows them to " +
        "signed-in collaborators, so check the Releases page in your browser.";

    public static async Task<UpdateInfo> CheckAsync(HttpClient http, string currentVersion, CancellationToken ct = default)
    {
        // The release list, not /releases/latest: "latest" skips pre-releases, and every alpha is one.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=20");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);

        // A private repo seen without signing in looks like a missing one.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(false, null, ReleasesPage, PrivateRepoMessage);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var newest = doc.RootElement.EnumerateArray()
            .Where(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()))
            .Select(r => (Version: (r.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V'),
                          Url: r.TryGetProperty("html_url", out var u) ? u.GetString() : ReleasesPage))
            .Where(r => r.Version.Length > 0)
            .OrderByDescending(r => r.Version, Comparer<string>.Create(Compare))
            .FirstOrDefault();
        if (newest.Version is null)
            return new(false, null, ReleasesPage, "No release has been published yet.");

        var (latest, url) = newest;
        return Compare(latest, currentVersion) > 0
            ? new(true, latest, url, $"RegWatch {latest} is available (you have {currentVersion}).")
            : new(false, latest, url, $"You have the latest version ({currentVersion}).");
    }

    /// <summary>
    /// Semantic-version order: 0.2.0 &gt; 0.1.0, and a release beats its pre-releases
    /// (0.1.0 &gt; 0.1.0-beta &gt; 0.1.0-alpha.2 &gt; 0.1.0-alpha).
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var c = coreA[i].CompareTo(coreB[i]);
            if (c != 0) return c;
        }
        if (preA == preB) return 0;
        if (preA.Length == 0) return 1;
        if (preB.Length == 0) return -1;

        var partsA = preA.Split('.');
        var partsB = preB.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var bothNumeric = int.TryParse(partsA[i], out var na) & int.TryParse(partsB[i], out var nb);
            var c = bothNumeric ? na.CompareTo(nb) : string.CompareOrdinal(partsA[i], partsB[i]);
            if (c != 0) return c;
        }
        return partsA.Length.CompareTo(partsB.Length);
    }

    private static (int[] Core, string Pre) Split(string version)
    {
        var plain = version.Split('+')[0];
        var dash = plain.IndexOf('-');
        var core = (dash < 0 ? plain : plain[..dash]).Split('.');
        var numbers = Enumerable.Range(0, 3).Select(i => i < core.Length && int.TryParse(core[i], out var n) ? n : 0).ToArray();
        return (numbers, dash < 0 ? "" : plain[(dash + 1)..]);
    }
}
