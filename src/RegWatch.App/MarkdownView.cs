using System.Diagnostics;
using System.IO;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>Shows Markdown (digests, the user guide) in a WebView2 with the app's page style.</summary>
public static class MarkdownView
{
    // Digests are written with meaningful line breaks; documents like the guide wrap freely.
    private static readonly MarkdownPipeline DigestPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly MarkdownPipeline DocumentPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoLinks()
        .Build();

    public static async Task InitAsync(WebView2 view)
    {
        // Keep WebView2's cache out of the install folder.
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegWatch", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
        await view.EnsureCoreWebView2Async(env);

        // Links open in the default browser rather than inside the app.
        view.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        view.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        ApplyTheme(view, AppSettings.Load().Theme);
    }

    // The page CSS picks light or dark from prefers-color-scheme, which this sets. It is a
    // profile setting, so it reaches every open view (the digest and the guide) at once.
    public static void ApplyTheme(WebView2 view, AppTheme theme)
    {
        if (view.CoreWebView2 is null) return;
        view.CoreWebView2.Profile.PreferredColorScheme = theme switch
        {
            AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
            AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
    }

    public static void ShowDigest(WebView2 view, string markdown) =>
        view.NavigateToString(Page(Markdown.ToHtml(markdown, DigestPipeline)));

    public static void ShowDocument(WebView2 view, string markdown) =>
        view.NavigateToString(Page(Markdown.ToHtml(markdown, DocumentPipeline)));

    public static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static string Page(string body) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        :root { color-scheme: light dark;
          --bg: #fff; --text: #1f1f1d; --rule: #e2e2de; --quote-bg: #f4f4f1; --quote-bar: #c9c9c3;
          --quote-text: #555; --link: #1a5fb4; --code-bg: #f0f0ec; --cell: #ddd; }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #1c1c1c; --text: #e6e6e3; --rule: #3a3a38; --quote-bg: #262625; --quote-bar: #55554f;
            --quote-text: #b8b8b2; --link: #8ab4f8; --code-bg: #2c2c2a; --cell: #3a3a38; }
        }
        html, body { background: var(--bg); }
        body { font: 14px/1.55 "Segoe UI", sans-serif; color: var(--text); max-width: 860px; margin: 24px 32px 64px; }
        h1 { font-size: 22px; margin-bottom: 4px; }
        h2 { font-size: 16px; margin-top: 28px; padding-bottom: 4px; border-bottom: 1px solid var(--rule); }
        h3 { font-size: 14px; margin: 20px 0 4px; }
        blockquote { margin: 12px 0; padding: 6px 12px; background: var(--quote-bg); border-left: 3px solid var(--quote-bar); color: var(--quote-text); }
        a { color: var(--link); word-break: break-all; }
        code { background: var(--code-bg); padding: 1px 4px; border-radius: 3px; }
        table { border-collapse: collapse; } td, th { border: 1px solid var(--cell); padding: 4px 8px; vertical-align: top; }
        </style></head><body>{{body}}</body></html>
        """;
}
