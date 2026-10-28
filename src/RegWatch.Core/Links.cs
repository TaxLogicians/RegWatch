namespace RegWatch.Core;

/// <summary>Which links the app may hand to the browser. Digests carry links from fetched pages.</summary>
public static class Links
{
    /// <summary>True only for absolute http and https URLs.</summary>
    public static bool IsWebLink(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !uri.IsUnc;

    /// <summary>The page WebView2 builds from NavigateToString (about:blank or a data: URL).</summary>
    public static bool IsInlinePage(string? target) =>
        target is not null
        && (target.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase));
}
