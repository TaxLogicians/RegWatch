using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>
/// Card-style editor for PROFILE.md: topic buckets, false-positive guards, and the raw file.
/// Saves back to PROFILE.md; entries you do not touch keep their exact original text.
/// </summary>
public partial class ProfileEditorWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ProfileDocument _doc = null!;
    private string _savedText = "";
    private bool _settingRaw;
    private bool _rawEdited;

    public ProfileEditorWindow(string profilePath)
    {
        _path = profilePath;
        InitializeComponent();
        DataContext = this;
        Load(File.ReadAllText(_path));
        _savedText = CurrentText();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
    }

    public IReadOnlyList<WeightOption> WeightOptions { get; } =
    [
        new(3, "3 — read now"),
        new(2, "2 — read this week"),
        new(1, "1 — skim / reference"),
        new(0, "0 — skip"),
    ];

    private ObservableCollection<BucketVm> Buckets { get; } = [];
    private ObservableCollection<GuardVm> Guards { get; } = [];

    private void Load(string text)
    {
        _doc = ProfileDocument.Parse(text);
        Buckets.Clear();
        foreach (var b in _doc.Buckets) Buckets.Add(new BucketVm(b));
        Guards.Clear();
        foreach (var g in _doc.Guards) Guards.Add(new GuardVm(g));
        BucketList.ItemsSource = Buckets;
        GuardList.ItemsSource = Guards;

        BucketsTab.IsEnabled = _doc.HasBucketSection;
        GuardsTab.IsEnabled = _doc.HasGuardSection;
        if (!_doc.HasBucketSection || !_doc.HasGuardSection) Tabs.SelectedItem = RawTab;
    }

    /// <summary>The profile as it would be saved right now.</summary>
    private string CurrentText()
    {
        if (Tabs.SelectedItem == RawTab && _rawEdited) return RawBox.Text;

        _doc.Buckets.Clear();
        _doc.Buckets.AddRange(Buckets.Select(vm => vm.ToEntry()));
        _doc.Guards.Clear();
        _doc.Guards.AddRange(Guards.Select(vm => vm.ToEntry()));
        return _doc.Render();
    }

    private bool IsDirty => CurrentText() != _savedText;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox selection changes inside the cards bubble up here too.
        if (e.Source != Tabs) return;

        if (e.RemovedItems.Contains(RawTab) && _rawEdited)
        {
            // Leaving the raw tab: re-read the cards from the edited text.
            var text = RawBox.Text;
            _rawEdited = false;
            Load(text);
        }
        if (Tabs.SelectedItem == RawTab && !_rawEdited)
        {
            _settingRaw = true;
            RawBox.Text = CurrentText();
            _settingRaw = false;
        }
    }

    private void RawBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingRaw) _rawEdited = true;
    }

    private void UpdateStatus()
    {
        var unparsed = _doc.UnparsedBullets > 0 ? $" · {_doc.UnparsedBullets} bullet(s) only editable in Raw text" : "";
        var dirty = IsDirty ? " · unsaved changes" : " · saved";
        StatusText.Text = $"{_path} · {Buckets.Count} buckets · {Guards.Count} guards{unparsed}{dirty}";
        Title = IsDirty ? "RegWatch profile *" : "RegWatch profile";
    }

    // ---- Buckets -------------------------------------------------------------------------

    private void AddBucket_Click(object sender, RoutedEventArgs e)
    {
        Buckets.Add(new BucketVm(new BucketEntry("new_bucket", 2, [])));
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            (BucketList.Parent as ScrollViewer)?.ScrollToEnd());
    }

    private void RemoveBucket_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BucketVm vm) return;
        var answer = MessageBox.Show(this, $"Remove the bucket \"{vm.Key}\" and its {vm.Terms.Count} terms?",
            "Remove bucket", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) Buckets.Remove(vm);
    }

    private void RemoveTerm_Click(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        // The chip's DataContext is the term string; the card's is the bucket.
        var card = FindAncestorDataContext<BucketVm>(button);
        if (card is not null && button.Tag is string term) card.Terms.Remove(term);
    }

    private void NewTerm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ((FrameworkElement)sender).DataContext is not BucketVm vm) return;
        vm.CommitNewTerm();
        e.Handled = true;
    }

    // ---- Guards --------------------------------------------------------------------------

    private void AddGuard_Click(object sender, RoutedEventArgs e)
    {
        Guards.Add(new GuardVm(new GuardEntry("\"term\" → **0** unless …")));
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            (GuardList.Parent as ScrollViewer)?.ScrollToEnd());
    }

    private void RemoveGuard_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GuardVm vm) Guards.Remove(vm);
    }

    // ---- Save / revert / close -----------------------------------------------------------

    private bool Save()
    {
        // Pending text in an "Add a term" box counts as added.
        foreach (var b in Buckets) b.CommitNewTerm();

        if (Validate() is { } problem)
        {
            MessageBox.Show(this, problem, "Cannot save profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var text = CurrentText();
        if (text == _savedText) return true;

        text = ProfileDocument.StampCalibrated(text, DateOnly.FromDateTime(DateTime.Today));
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);

        _rawEdited = false;
        Load(text);
        _savedText = CurrentText();
        if (Tabs.SelectedItem == RawTab)
        {
            _settingRaw = true;
            RawBox.Text = _savedText;
            _settingRaw = false;
        }
        UpdateStatus();
        return true;
    }

    private string? Validate()
    {
        if (Tabs.SelectedItem == RawTab && _rawEdited) return null; // raw text is saved as written

        var keys = Buckets.Select(b => b.Key.Trim()).ToList();
        if (keys.FirstOrDefault(k => !Regex.IsMatch(k, "^[A-Za-z0-9_]+$")) is { } bad)
            return $"Bucket key \"{bad}\" can only use letters, digits and underscores (e.g. foreign_trusts).";
        if (keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return $"Two buckets are named \"{dup.Key}\".";
        if (Buckets.FirstOrDefault(b => b.Terms.Count == 0) is { } empty)
            return $"Bucket \"{empty.Key}\" has no terms.";
        if (Guards.Any(g => string.IsNullOrWhiteSpace(g.Text)))
            return "A guard is empty; remove it or fill it in.";
        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (IsDirty && MessageBox.Show(this, "Discard unsaved changes?", "Revert",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _rawEdited = false;
        Load(File.ReadAllText(_path));
        _savedText = CurrentText();
        if (Tabs.SelectedItem == RawTab)
        {
            _settingRaw = true;
            RawBox.Text = _savedText;
            _settingRaw = false;
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!IsDirty) return;

        var answer = MessageBox.Show(this, "Save changes to PROFILE.md before closing?", "RegWatch profile",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
            e.Cancel = true;
    }

    private static T? FindAncestorDataContext<T>(DependencyObject start) where T : class
    {
        for (var d = VisualTreeHelper.GetParent(start); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: T match }) return match;
        return null;
    }

    public sealed record WeightOption(int Value, string Label);

    /// <summary>One bucket card.</summary>
    public sealed class BucketVm(BucketEntry entry) : Observable
    {
        private string _key = entry.Key;
        private int _weight = entry.Weight;
        private string _newTerm = "";

        public ObservableCollection<string> Terms { get; } = new(entry.Terms);

        public string Key
        {
            get => _key;
            set => Set(ref _key, value);
        }

        public int Weight
        {
            get => _weight;
            set => Set(ref _weight, value);
        }

        public string NewTerm
        {
            get => _newTerm;
            set
            {
                if (Set(ref _newTerm, value)) OnPropertyChanged(nameof(NewTermHintVisibility));
            }
        }

        public Visibility NewTermHintVisibility => NewTerm.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        public void CommitNewTerm()
        {
            foreach (var term in NewTerm.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
                if (!Terms.Contains(term)) Terms.Add(term);
            NewTerm = "";
        }

        /// <summary>Writes the card back into its entry so untouched cards render unchanged.</summary>
        public BucketEntry ToEntry()
        {
            entry.Key = Key.Trim();
            entry.Weight = Weight;
            entry.Terms = Terms.ToList();
            return entry;
        }
    }

    /// <summary>One guard row.</summary>
    public sealed class GuardVm(GuardEntry entry) : Observable
    {
        private string _text = entry.Text;

        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        public GuardEntry ToEntry()
        {
            entry.Text = Text.Trim();
            return entry;
        }
    }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
