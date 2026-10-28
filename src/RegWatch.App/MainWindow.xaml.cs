using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RegWatch.Core;

namespace RegWatch.App;

public partial class MainWindow : Window
{
    private readonly string _root;
    private CancellationTokenSource? _runCts;

    public MainWindow(string root)
    {
        _root = root;
        InitializeComponent();
        Title = $"RegWatch {AppInfo.DisplayVersion}";
        VersionBadge.Text = AppInfo.DisplayVersion;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F1) return;
            Guide_Click(this, e);
            e.Handled = true;
        };
        Loaded += async (_, _) =>
        {
            await MarkdownView.InitAsync(Viewer);
            Viewer.CoreWebView2.WebMessageReceived += Viewer_WebMessageReceived;
            Refresh();
        };
    }

    private string DigestsDir => Path.Combine(_root, "digests");

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenBelow(SettingsMenu, SettingsButton);

    private void Help_Click(object sender, RoutedEventArgs e) => OpenBelow(HelpMenu, HelpButton);

    // The drop-down buttons open their menu on a normal click, not just a right-click.
    private static void OpenBelow(ContextMenu menu, UIElement button)
    {
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private HelpWindow? _guide;

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        // One guide window, kept beside the main window (not modal) so it can stay open while working.
        if (_guide is { IsLoaded: true })
        {
            _guide.Activate();
            return;
        }
        _guide = new HelpWindow { Owner = this };
        _guide.Show();
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"RegWatch {AppInfo.DisplayVersion}\n\n" +
            "Weekly IRS / Treasury triage digest for US foreign-trust and international tax work. " +
            "Triage only, not tax advice: verify every item against its primary source.\n\n" +
            $"Data folder: {_root}\n" +
            $"Settings: {AppSettings.SettingsPath}\n" +
            $"Scoring: {ScorerFactory.Describe(AppSettings.Load().Scorer)}\n" +
            $".NET: {Environment.Version} · Windows {Environment.OSVersion.Version}\n\n" +
            "Press Ctrl+C to copy these details.",
            "About RegWatch", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        var theme = Enum.Parse<AppTheme>(tag);
        (AppSettings.Load() with { Theme = theme }).Save();
        Theme.Apply(theme);
        MarkdownView.ApplyTheme(Viewer, theme);
        CheckOnly(ThemeMenu, tag);
    }

    private List<DigestRow> _digests = [];
    private ReadMarks? _readMarks;

    private void Refresh(string? selectPath = null)
    {
        var summaries = DigestIndex.List(DigestsDir);
        _readMarks ??= new ReadMarks(_root, summaries.Select(s => s.Name));
        _digests = summaries.Select(d => new DigestRow(d) { IsUnread = !_readMarks.IsRead(d.Name) }).ToList();
        ApplyFilter(selectPath);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var deadlines = DigestIndex.UpcomingDeadlines(DigestsDir, today).Select(d => new DeadlineRow(d, today)).ToList();
        DeadlineGrid.ItemsSource = deadlines;
        DeadlinesTab.Header = deadlines.Count > 0 ? $"Comment deadlines ({deadlines.Count})" : "Comment deadlines";

        var state = new StateStore(Path.Combine(_root, "STATE.json")).Load();
        var settings = AppSettings.Load();
        var ready = ScorerFactory.Problem(settings) ?? "ready to run";
        StatusText.Text = $"Last run {state.LastRun ?? "never"} · IRB through {state.LastIrbIssue ?? "–"} · FR from {state.LastFedRegDate ?? "–"} · " +
                          $"scoring via {ScorerFactory.Describe(settings.Scorer)}, {ready}";
        StatusText.ToolTip = state.LastRunSummary;

        CheckOnly(ScoringMenu, settings.Scorer.ToString());
        CheckOnly(ThemeMenu, settings.Theme.ToString());
        NotifyItem.IsChecked = settings.NotifyAfterScheduledRun;
    }

    private string Query => SearchBox.Text.Trim();

    // Shows the digests that pass the search box and the filter, keeping the selection where possible.
    private void ApplyFilter(string? selectPath = null)
    {
        selectPath ??= (DigestList.SelectedItem as DigestRow)?.Summary.Path;
        var filter = (FilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        var shown = _digests
            .Where(d => filter switch
            {
                "Unread" => d.IsUnread,
                "Score3" => d.Summary.Score3 > 0,
                "Score2" => d.Summary.Score3 + d.Summary.Score2 > 0,
                _ => true,
            })
            .Where(d => Query.Length == 0 || d.Text.Contains(Query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DigestList.ItemsSource = shown;
        DigestList.SelectedItem = shown.FirstOrDefault(d => d.Summary.Path == selectPath) ?? shown.FirstOrDefault();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
        ShowSelectedDigest(); // re-highlight matches in the open digest
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        _readMarks?.MarkAllRead(_digests.Select(d => d.Name));
        Refresh();
    }

    private void Notify_Click(object sender, RoutedEventArgs e) =>
        (AppSettings.Load() with { NotifyAfterScheduledRun = NotifyItem.IsChecked }).Save();

    private void Feedback_Click(object sender, RoutedEventArgs e) =>
        new FeedbackWindow(_root) { Owner = this }.ShowDialog();

    private async void Updates_Click(object sender, RoutedEventArgs e)
    {
        UpdateInfo info;
        try
        {
            using var http = RunPipeline.CreateHttpClient();
            info = await UpdateCheck.CheckAsync(http, AppInfo.DisplayVersion);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"Could not check for updates: {ex.Message}", "Check for updates");
            return;
        }

        var open = MessageBox.Show(this, info.Message + (info.Url is null ? "" : "\n\nOpen the Releases page?"),
            "Check for updates", info.Url is null ? MessageBoxButton.OK : MessageBoxButton.YesNo,
            info.Available ? MessageBoxImage.Information : MessageBoxImage.None);
        if (open == MessageBoxResult.Yes && info.Url is not null) OpenExternal(info.Url);
    }

    // "Too high / Too low / Not relevant" clicks from the digest page.
    private void Viewer_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_shownDigest is null) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var msg = doc.RootElement;
            if (msg.GetProperty("type").GetString() != "feedback") return;
            new FeedbackLog(_root).Add(new FeedbackEntry
            {
                At = DateTime.Now,
                Digest = _shownDigest,
                Item = msg.GetProperty("item").GetString() ?? "",
                Score = msg.GetProperty("score").GetInt32(),
                Verdict = Enum.Parse<FeedbackVerdict>(msg.GetProperty("verdict").GetString() ?? ""),
            });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            // A malformed message from the page is ignored rather than crashing the app.
        }
    }

    // Radio-style ticks in a submenu: only the item whose Tag matches is checked.
    private static void CheckOnly(MenuItem menu, string tag)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsChecked = (string)item.Tag == tag;
    }

    private void ScorerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        (AppSettings.Load() with { Scorer = Enum.Parse<ScorerKind>(tag) }).Save();
        Refresh((DigestList.SelectedItem as DigestRow)?.Summary.Path);
    }

    private string? _shownDigest;

    private void DigestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShowSelectedDigest()) Tabs.SelectedIndex = 0;
    }

    private bool ShowSelectedDigest()
    {
        if (DigestList.SelectedItem is not DigestRow row || Viewer.CoreWebView2 is null) return false;
        var marks = new FeedbackLog(_root).Load()
            .Where(f => f.Digest == row.Name)
            .GroupBy(f => f.Item)
            .ToDictionary(g => g.Key, g => g.Last().Verdict.ToString());
        _shownDigest = row.Name;
        MarkdownView.ShowDigest(Viewer, row.Text, marks, Query.Length > 0 ? Query : null);

        if (row.IsUnread)
        {
            _readMarks?.MarkRead(row.Name);
            row.IsUnread = false;
        }
        return true;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load();
        if (ScorerFactory.Problem(settings) is not null)
        {
            // Open the settings so the missing piece (key, URL, model) can be filled in.
            Advanced_Click(sender, e);
            settings = AppSettings.Load();
            if (ScorerFactory.Problem(settings) is { } problem)
            {
                MessageBox.Show(this, problem, "RegWatch");
                return;
            }
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
            var scorer = ScorerFactory.Create(settings);
            ((IProgress<string>)progress).Report($"Scoring via {scorer.Description}.");
            var pipeline = new RunPipeline(_root, http, scorer, settings.EffectiveFeeds, settings.UseFeedbackInScoring);
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

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ScoringSettingsWindow(Path.Combine(_root, "PROFILE.md")) { Owner = this };
        if (dialog.ShowDialog() == true) Refresh((DigestList.SelectedItem as DigestRow)?.Summary.Path);
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e) =>
        new ProfileEditorWindow(Path.Combine(_root, "PROFILE.md")) { Owner = this }.ShowDialog();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenExternal(_root);

    private void Sources_Click(object sender, RoutedEventArgs e) =>
        new SourcesWindow { Owner = this }.ShowDialog();

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        var runner = Path.Combine(AppContext.BaseDirectory, "RegWatch.Runner.exe");
        if (!File.Exists(runner))
        {
            MessageBox.Show(this, $"RegWatch.Runner.exe was not found next to the app ({AppContext.BaseDirectory}).", "RegWatch");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Create a Windows scheduled task \"{WeeklyTask.Name}\" that runs every Monday at 19:00 (catches the US-morning IRS release)?\n\n" +
            "It runs as you, while you are signed in. If the computer is off or asleep at 19:00, it runs as soon as " +
            "possible afterwards. It replaces any existing task with that name.",
            "Schedule weekly run", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        // schtasks reads task XML as UTF-16, matching the declaration in WeeklyTask.Xml.
        var xmlPath = Path.Combine(Path.GetTempPath(), $"regwatch-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, WeeklyTask.Xml(runner, _root, WeeklyTask.NextMondayEvening(DateTime.Now)), System.Text.Encoding.Unicode);
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "/Create", "/F", "/TN", WeeklyTask.Name, "/XML", xmlPath })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            MessageBox.Show(this, output.Trim(), p.ExitCode == 0 ? "Scheduled" : "Could not schedule");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    private void DeadlineGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeadlineGrid.SelectedItem is DeadlineRow { Entry.Url.Length: > 0 } row) OpenExternal(row.Entry.Url);
    }

    private static void OpenExternal(string target) => MarkdownView.OpenExternal(target);


    private sealed class DigestRow(DigestSummary summary) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isUnread;
        private string? _text;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public DigestSummary Summary { get; } = summary;
        public string Name => Summary.Name;

        /// <summary>The digest's Markdown, read once (for search and display).</summary>
        public string Text => _text ??= File.ReadAllText(Summary.Path);

        public bool IsUnread
        {
            get => _isUnread;
            set
            {
                _isUnread = value;
                PropertyChanged?.Invoke(this, new(nameof(IsUnread)));
            }
        }
        public string Headline => Summary.Headline;
        public string Badge => Summary.Score3 > 0 ? $"{Summary.Score3}× 3" : Summary.Score2 > 0 ? $"{Summary.Score2}× 2" : "–";
        public int BadgeLevel => Summary.Score3 > 0 ? 3 : Summary.Score2 > 0 ? 2 : 0;
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
