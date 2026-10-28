using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Anthropic;
using Markdig;
using Microsoft.Web.WebView2.Core;
using RegWatch.Core;

namespace RegWatch.App;

public partial class MainWindow : Window
{
    private const string TaskName = "RegWatch weekly digest";

    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private readonly string _root;
    private CancellationTokenSource? _runCts;

    public MainWindow(string root)
    {
        _root = root;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await InitViewerAsync();
            Refresh();
        };
    }

    private string DigestsDir => Path.Combine(_root, "digests");

    private async Task InitViewerAsync()
    {
        // Keep WebView2's cache out of the publish folder.
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegWatch", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
        await Viewer.EnsureCoreWebView2Async(env);

        // Links open in the default browser rather than inside the app.
        Viewer.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        Viewer.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
    }

    private void Refresh(string? selectPath = null)
    {
        var digests = DigestIndex.List(DigestsDir).Select(d => new DigestRow(d)).ToList();
        DigestList.ItemsSource = digests;
        DigestList.SelectedItem = digests.FirstOrDefault(d => d.Summary.Path == selectPath) ?? digests.FirstOrDefault();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var deadlines = DigestIndex.UpcomingDeadlines(DigestsDir, today).Select(d => new DeadlineRow(d, today)).ToList();
        DeadlineGrid.ItemsSource = deadlines;
        DeadlinesTab.Header = deadlines.Count > 0 ? $"Comment deadlines ({deadlines.Count})" : "Comment deadlines";

        var state = new StateStore(Path.Combine(_root, "STATE.json")).Load();
        var key = ApiKeyStore.HasKey ? "API key set" : "No API key: choose Set API key before running";
        StatusText.Text = $"Last run {state.LastRun ?? "never"} · IRB through {state.LastIrbIssue ?? "–"} · FR from {state.LastFedRegDate ?? "–"} · {key}";
        StatusText.ToolTip = state.LastRunSummary;
        KeyButton.Content = ApiKeyStore.HasKey ? "Change API key" : "Set API key";
    }

    private void DigestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DigestList.SelectedItem is not DigestRow row || Viewer.CoreWebView2 is null) return;
        var html = Markdig.Markdown.ToHtml(File.ReadAllText(row.Summary.Path), Markdown);
        Viewer.NavigateToString(Page(html));
        Tabs.SelectedIndex = 0;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyStore.Resolve();
        if (key is null)
        {
            SetKey_Click(sender, e);
            key = ApiKeyStore.Resolve();
            if (key is null) return;
        }

        RunButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        LogBox.Clear();
        Tabs.SelectedItem = LogTab;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<string>(line => LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"));

        try
        {
            using var http = RunPipeline.CreateHttpClient();
            var pipeline = new RunPipeline(_root, http, new ClaudeScorer(new AnthropicClient { ApiKey = key }));
            var outcome = await Task.Run(() => pipeline.RunAsync(progress, _runCts.Token));
            ((IProgress<string>)progress).Report(outcome.Summary);
            Refresh(outcome.DigestPath);
        }
        catch (OperationCanceledException)
        {
            ((IProgress<string>)progress).Report("Cancelled. STATE.json was not changed.");
        }
        catch (Exception ex)
        {
            ((IProgress<string>)progress).Report($"Run failed: {ex.GetType().Name}: {ex.Message}");
            ((IProgress<string>)progress).Report("STATE.json was not changed, so the next run re-pulls the same window.");
        }
        finally
        {
            RunButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    private void SetKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ApiKeyWindow { Owner = this };
        if (dialog.ShowDialog() == true) Refresh((DigestList.SelectedItem as DigestRow)?.Summary.Path);
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e) => OpenExternal(Path.Combine(_root, "PROFILE.md"));

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenExternal(_root);

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        var runner = Path.Combine(AppContext.BaseDirectory, "RegWatch.Runner.exe");
        if (!File.Exists(runner))
        {
            MessageBox.Show(this, $"RegWatch.Runner.exe was not found next to the app ({AppContext.BaseDirectory}).", "RegWatch");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Create a Windows scheduled task \"{TaskName}\" that runs every Monday at 19:00 (catches the US-morning IRS release)?\n\n" +
            "It runs as you, only while you are signed in, and replaces any existing task with that name.",
            "Schedule weekly run", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/Create", "/F", "/SC", "WEEKLY", "/D", "MON", "/ST", "19:00", "/TN", TaskName,
                     "/TR", $"\"{runner}\" --root \"{_root}\"" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        MessageBox.Show(this, output.Trim(), p.ExitCode == 0 ? "Scheduled" : "Could not schedule");
    }

    private void DeadlineGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeadlineGrid.SelectedItem is DeadlineRow { Entry.Url.Length: > 0 } row) OpenExternal(row.Entry.Url);
    }

    private static void OpenExternal(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static string Page(string body) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        :root { color-scheme: light; }
        html, body { background: #fff; }
        body { font: 14px/1.55 "Segoe UI", sans-serif; color: #1f1f1d; max-width: 860px; margin: 24px 32px 64px; }
        h1 { font-size: 22px; margin-bottom: 4px; }
        h2 { font-size: 16px; margin-top: 28px; padding-bottom: 4px; border-bottom: 1px solid #e2e2de; }
        h3 { font-size: 14px; margin: 20px 0 4px; }
        blockquote { margin: 12px 0; padding: 6px 12px; background: #f4f4f1; border-left: 3px solid #c9c9c3; color: #555; }
        a { color: #1a5fb4; word-break: break-all; }
        code { background: #f0f0ec; padding: 1px 4px; border-radius: 3px; }
        table { border-collapse: collapse; } td, th { border: 1px solid #ddd; padding: 4px 8px; }
        </style></head><body>{{body}}</body></html>
        """;

    private sealed class DigestRow(DigestSummary summary)
    {
        public DigestSummary Summary { get; } = summary;
        public string Name => Summary.Name;
        public string Headline => Summary.Headline;
        public string Badge => Summary.Score3 > 0 ? $"{Summary.Score3}× 3" : Summary.Score2 > 0 ? $"{Summary.Score2}× 2" : "–";
        public Brush BadgeBrush => Summary.Score3 > 0 ? Brushes.Firebrick : Summary.Score2 > 0 ? Brushes.DarkGoldenrod : Brushes.Gray;
    }

    private sealed class DeadlineRow(DeadlineEntry entry, DateOnly today)
    {
        public DeadlineEntry Entry { get; } = entry;
        public DateTime Deadline => Entry.Deadline.ToDateTime(TimeOnly.MinValue);
        public int DaysLeft => Entry.Deadline.DayNumber - today.DayNumber;
        public int Score => Entry.Score;
        public string Cite => Entry.Cite;
        public string DigestName => Entry.DigestName;
    }
}
