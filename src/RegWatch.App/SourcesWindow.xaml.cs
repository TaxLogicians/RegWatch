using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>Manages the commentary feeds: enable/disable, category filters, and adding custom feeds.</summary>
public partial class SourcesWindow : Window
{
    private readonly ObservableCollection<SourceVm> _sources;
    private FeedDiscovery? _found;

    public SourcesWindow()
    {
        InitializeComponent();
        _sources = new(AppSettings.Load().EffectiveFeeds.Select(f => new SourceVm(f)));
        SourceList.ItemsSource = _sources;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SourceVm vm
            && MessageBox.Show(this, $"Remove \"{vm.Name}\"?", "Remove source", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            _sources.Remove(vm);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Find_Click(sender, e);
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddressBox.Text)) return;

        FindButton.IsEnabled = false;
        FoundPanel.Visibility = Visibility.Collapsed;
        FindStatus.Foreground = System.Windows.Media.Brushes.DimGray;
        FindStatus.Text = "Looking for a feed…";
        try
        {
            using var http = RunPipeline.CreateHttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            _found = await new FeedClient(http).DiscoverAsync(AddressBox.Text);

            if (_sources.Any(s => string.Equals(s.Url, _found.FeedUrl, StringComparison.OrdinalIgnoreCase)))
            {
                FindStatus.Text = $"{_found.FeedUrl} is already in the list.";
                return;
            }

            FoundUrl.Text = _found.FeedUrl;
            SampleList.ItemsSource = _found.Sample.Take(6)
                .Select(i => $"{i.Published:yyyy-MM-dd}  {i.Title}" + (i.Categories.Count > 0 ? $"  [{string.Join(", ", i.Categories)}]" : ""))
                .ToList();
            NewNameBox.Text = _found.Title;
            NewCategoriesBox.Text = "";
            FoundCategories.Text = _found.Categories.Count > 0
                ? $"Categories in the feed: {string.Join(", ", _found.Categories)}"
                : "This feed does not label its articles with categories.";
            FoundPanel.Visibility = Visibility.Visible;
            FindStatus.Text = _found.Sample.Count == 0 ? "The feed is reachable but currently empty." : "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            FindStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            FindStatus.Text = ex is TaskCanceledException ? "The site did not answer within 20 seconds." : ex.Message;
        }
        finally
        {
            FindButton.IsEnabled = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_found is null) return;
        _sources.Add(new SourceVm(new FeedSource
        {
            Name = string.IsNullOrWhiteSpace(NewNameBox.Text) ? _found.Title : NewNameBox.Text.Trim(),
            Url = _found.FeedUrl,
            Categories = SourceVm.SplitCategories(NewCategoriesBox.Text),
        }));
        _found = null;
        FoundPanel.Visibility = Visibility.Collapsed;
        AddressBox.Clear();
        FindStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
        FindStatus.Text = "Added. Click Save to keep it; the next run reads its last 14 days.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Name)) is { } unnamed)
        {
            MessageBox.Show(this, $"Give the source at {unnamed.Url} a name.", "Commentary sources");
            return;
        }
        (AppSettings.Load() with { Feeds = _sources.Select(s => s.ToSource()).ToList() }).Save();
        DialogResult = true;
    }

    public sealed class SourceVm(FeedSource source) : ProfileEditorWindow.Observable
    {
        private string _name = source.Name;
        private bool _enabled = source.Enabled;
        private string _categoriesText = string.Join(", ", source.Categories);

        public string Url => source.Url;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public string CategoriesText
        {
            get => _categoriesText;
            set
            {
                if (Set(ref _categoriesText, value)) OnPropertyChanged(nameof(CategoriesHintVisibility));
            }
        }

        public Visibility CategoriesHintVisibility => CategoriesText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BuiltInVisibility => source.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveVisibility => source.BuiltIn ? Visibility.Collapsed : Visibility.Visible;

        public FeedSource ToSource() => source with
        {
            Name = Name.Trim(),
            Enabled = Enabled,
            Categories = SplitCategories(CategoriesText),
        };

        public static IReadOnlyList<string> SplitCategories(string text) =>
            text.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
    }
}
