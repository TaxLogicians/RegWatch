using System.IO;
using System.Windows;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>Lists the "scored wrong" marks from digests and controls whether they reach the scorer.</summary>
public partial class FeedbackWindow : Window
{
    private readonly FeedbackLog _log;
    private bool _loading = true;

    public FeedbackWindow(string root)
    {
        _log = new FeedbackLog(root);
        InitializeComponent();
        MaxRun.Text = FeedbackLog.MaxInPrompt.ToString();
        UseInScoring.IsChecked = AppSettings.Load().UseFeedbackInScoring;
        _loading = false;
        Reload();
    }

    private void Reload()
    {
        var entries = _log.Load().OrderByDescending(e => e.At).ToList();
        Grid.ItemsSource = entries;
        CountText.Text = entries.Count == 0
            ? "No marks yet. Hover over an item in a digest to mark its score."
            : $"{entries.Count} mark(s)";
    }

    private void UseInScoring_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        (AppSettings.Load() with { UseFeedbackInScoring = UseInScoring.IsChecked == true }).Save();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in Grid.SelectedItems.OfType<FeedbackEntry>().ToList())
            _log.Remove(entry);
        Reload();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_log.Path)) _log.Save([]);
        // .jsonl usually has no file association, so open it in Notepad.
        System.Diagnostics.Process.Start("notepad.exe", $"\"{_log.Path}\"");
    }
}
