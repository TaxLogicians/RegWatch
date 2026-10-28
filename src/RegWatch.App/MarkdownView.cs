using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    /// <summary>
    /// A digest with "scored wrong" buttons on each item (clicks arrive as web messages, see
    /// <see cref="DigestScript"/>) and any search matches highlighted.
    /// </summary>
    /// <param name="marks">Earlier feedback on this digest: item text → verdict name, shown as pressed.</param>
    public static void ShowDigest(WebView2 view, string markdown, IReadOnlyDictionary<string, string> marks, string? highlight) =>
        view.NavigateToString(Page(Markdown.ToHtml(markdown, DigestPipeline), DigestScript(marks, highlight)));

    public static void ShowDocument(WebView2 view, string markdown) =>
        view.NavigateToString(Page(Markdown.ToHtml(markdown, DocumentPipeline)));

    public static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    // System.Text.Json escapes <, > and & by default, so the values are safe inside <script>.
    private static string DigestScript(IReadOnlyDictionary<string, string> marks, string? highlight) => $$"""
        <script>
        (function () {
          const marks = {{JsonSerializer.Serialize(marks)}};
          const query = {{JsonSerializer.Serialize(highlight ?? "")}};
          const labels = { TooHigh: "Too high", TooLow: "Too low", NotRelevant: "Not relevant" };

          function addButtons(el, item, score, verdicts) {
            const bar = document.createElement("span");
            bar.className = "rw-fb";
            for (const v of verdicts) {
              const b = document.createElement("button");
              b.textContent = labels[v];
              b.title = "Mark this score as wrong. RegWatch uses your marks to calibrate later runs (Settings, Scoring feedback).";
              if (marks[item] === v) b.classList.add("on");
              b.onclick = () => {
                window.chrome.webview.postMessage({ type: "feedback", item, score, verdict: v });
                bar.querySelectorAll("button").forEach(x => x.classList.remove("on"));
                b.classList.add("on");
              };
              bar.appendChild(b);
            }
            el.appendChild(bar);
          }

          // Scored cards: "### 🟡 FR 2026-21921 — title — relevance 1 (buckets)"
          document.querySelectorAll("h3").forEach(h => {
            const m = h.textContent.match(/ — relevance (\d)/);
            if (!m) return;
            const score = Number(m[1]);
            const item = h.textContent.slice(0, m.index).replace(/^\S+\s+/, "").trim();
            addButtons(h, item, score, ["TooHigh", "TooLow", "NotRelevant"]);
          });
          // Skipped items: the list under "Skip (score 0)" can only be under-scored.
          document.querySelectorAll("h2").forEach(h => {
            if (!/^Skip/.test(h.textContent)) return;
            const list = h.nextElementSibling;
            if (!list || list.tagName !== "UL") return;
            list.querySelectorAll(":scope > li").forEach(li => {
              const a = li.querySelector("a");
              addButtons(li, (a ? a.textContent : li.textContent).trim(), 0, ["TooLow"]);
            });
          });

          // Search matches from the digest list's search box.
          if (query) {
            const needle = query.toLowerCase();
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
              acceptNode: n => n.parentElement.closest("button, script, style") ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
            });
            const hits = [];
            for (let n = walker.nextNode(); n; n = walker.nextNode())
              if (n.nodeValue.toLowerCase().includes(needle)) hits.push(n);
            for (const n of hits) {
              const text = n.nodeValue, lower = text.toLowerCase(), frag = document.createDocumentFragment();
              let at = 0;
              for (let i = lower.indexOf(needle); i >= 0; i = lower.indexOf(needle, at)) {
                frag.append(text.slice(at, i));
                const mark = document.createElement("mark");
                mark.textContent = text.slice(i, i + needle.length);
                frag.append(mark);
                at = i + needle.length;
              }
              frag.append(text.slice(at));
              n.replaceWith(frag);
            }
            const first = document.querySelector("mark");
            if (first) first.scrollIntoView({ block: "center" });
          }
        })();
        </script>
        """;

    private static string Page(string body, string script = "") => $$"""
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
        mark { background: #ffe58a; color: #1f1f1d; border-radius: 2px; }
        .rw-fb { margin-left: 10px; white-space: nowrap; opacity: .45; transition: opacity .15s; }
        h3:hover .rw-fb, li:hover .rw-fb, .rw-fb:focus-within, .rw-fb:has(.on) { opacity: 1; }
        .rw-fb button { font: 11px "Segoe UI", sans-serif; color: var(--text); background: var(--code-bg);
          border: 1px solid var(--rule); border-radius: 10px; padding: 1px 8px; margin-left: 4px; cursor: pointer; vertical-align: middle; }
        .rw-fb button:hover { border-color: var(--link); }
        .rw-fb button.on { background: var(--link); color: var(--bg); border-color: var(--link); }
        </style></head><body>{{body}}{{script}}</body></html>
        """;
}
