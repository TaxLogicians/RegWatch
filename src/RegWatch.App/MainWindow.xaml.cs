using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RegWatch.Core;

namespace RegWatch.App;

public partial class MainWindow : Window
{
    private const string TaskName = "RegWatch weekly digest";

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
        var settings = AppSettings.Load();
        var ready = ScorerFactory.Problem(settings) ?? "ready to run";
        StatusText.Text = $"Last run {state.LastRun ?? "never"} · IRB through {state.LastIrbIssue ?? "–"} · FR from {state.LastFedRegDate ?? "–"} · " +
                          $"scoring via {ScorerFactory.Describe(settings.Scorer)}, {ready}";
        StatusText.ToolTip = state.LastRunSummary;

        CheckOnly(ScoringMenu, settings.Scorer.ToString());
        CheckOnly(ThemeMenu, settings.Theme.ToString());
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

    private void DigestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DigestList.SelectedItem is not DigestRow row || Viewer.CoreWebView2 is null) return;
        MarkdownView.ShowDigest(Viewer, File.ReadAllText(row.Summary.Path));
        Tabs.SelectedIndex = 0;
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
            var pipeline = new RunPipeline(_root, http, scorer, settings.EffectiveFeeds);
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

    private static void OpenExternal(string target) => MarkdownView.OpenExternal(target);


    private sealed class DigestRow(DigestSummary summary)
    {
        public DigestSummary Summary { get; } = summary;
        public string Name => Summary.Name;
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
