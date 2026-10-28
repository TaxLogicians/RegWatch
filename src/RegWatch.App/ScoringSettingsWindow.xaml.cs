using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>
/// Advanced scoring settings: which AI scores the items and how to reach it. Saved per Windows
/// user in %APPDATA%\RegWatch\settings.json; keys are stored separately with DPAPI.
/// </summary>
public partial class ScoringSettingsWindow : Window
{
    private readonly string _profilePath;
    private bool _loading;

    public ScoringSettingsWindow(string profilePath)
    {
        _profilePath = profilePath;
        InitializeComponent();
        PresetBox.ItemsSource = OpenAiCompatibleScorer.Presets;
        Load(AppSettings.Load());
    }

    private void Load(AppSettings s)
    {
        _loading = true;
        ClaudeCodeRadio.IsChecked = s.Scorer == ScorerKind.ClaudeCode;
        AnthropicRadio.IsChecked = s.Scorer == ScorerKind.AnthropicApi;
        OpenAiRadio.IsChecked = s.Scorer == ScorerKind.OpenAiCompatible;

        ClaudeCodeModelBox.Text = s.ClaudeCodeModel;
        ClaudePathBox.Text = s.ClaudeCodePath ?? "";
        var found = ClaudeCodeScorer.Locate(s.ClaudeCodePath);
        ClaudePathHint.Text = found is null
            ? "claude.exe was not found. Install Claude Code, or browse to it."
            : string.IsNullOrWhiteSpace(s.ClaudeCodePath) ? $"Leave blank to use {found}." : "";

        AnthropicModelBox.Text = s.AnthropicModel;

        var preset = OpenAiCompatibleScorer.Presets.FirstOrDefault(p => p.Name == s.OpenAi.Preset)
            ?? OpenAiCompatibleScorer.Presets[^1];
        PresetBox.SelectedItem = preset;
        BaseUrlBox.Text = s.OpenAi.BaseUrl;
        OpenAiModelBox.Text = s.OpenAi.Model;
        StructuredBox.SelectedItem = StructuredBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == s.OpenAi.StructuredOutput.ToString());
        ItemsPerRequestBox.Text = s.OpenAi.ItemsPerRequest.ToString();
        TimeoutBox.Text = s.OpenAi.TimeoutMinutes.ToString();
        ShowPresetHelp(preset);
        _loading = false;

        RefreshKeys();
        ShowPanels();
    }

    /// <summary>The form as settings, or an error message.</summary>
    private (AppSettings? Settings, string? Error) ReadForm()
    {
        if (!int.TryParse(ItemsPerRequestBox.Text.Trim(), out var perRequest) || perRequest < 0)
            return (null, "Items per request must be 0 or a positive whole number.");
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 1)
            return (null, "Timeout must be a whole number of minutes, 1 or more.");

        var scorer = AnthropicRadio.IsChecked == true ? ScorerKind.AnthropicApi
                   : OpenAiRadio.IsChecked == true ? ScorerKind.OpenAiCompatible
                   : ScorerKind.ClaudeCode;

        return (AppSettings.Load() with
        {
            Scorer = scorer,
            ClaudeCodeModel = Blank(ClaudeCodeModelBox.Text) ?? ClaudeCodeScorer.DefaultModel,
            ClaudeCodePath = Blank(ClaudePathBox.Text),
            AnthropicModel = Blank(AnthropicModelBox.Text) ?? ClaudeScorer.DefaultModel,
            OpenAi = new OpenAiCompatibleSettings
            {
                Preset = (PresetBox.SelectedItem as ProviderPreset)?.Name ?? "Custom",
                BaseUrl = BaseUrlBox.Text.Trim(),
                Model = OpenAiModelBox.Text.Trim(),
                StructuredOutput = Enum.Parse<StructuredOutputMode>((string)((ComboBoxItem)StructuredBox.SelectedItem).Tag),
                ItemsPerRequest = perRequest,
                TimeoutMinutes = timeout,
            },
        }, null);
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowPanels()
    {
        ClaudeCodePanel.Visibility = ClaudeCodeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AnthropicPanel.Visibility = AnthropicRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = OpenAiRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TestResult.Text = "";
    }

    private void RefreshKeys()
    {
        AnthropicKeyStatus.Text = KeyStatus(ApiKeyStore.Anthropic);
        OpenAiKeyStatus.Text = KeyStatus(ApiKeyStore.OpenAiCompatible)
            + (OpenAiCompatibleScorer.IsLocal(BaseUrlBox.Text) ? " (not needed for a local server)" : "");

        // A key should never travel over plain http to another machine.
        KeyWarning.Text = ApiKeyStore.OpenAiCompatible.HasKey
            && Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && !OpenAiCompatibleScorer.IsLocal(BaseUrlBox.Text)
                ? "This URL is plain http on another machine: your API key would be sent unencrypted. Use https."
                : "";
    }

    private static string KeyStatus(ApiKeyStore store) =>
        store.EnvVarIsSet ? $"From {store.EnvVar}" : store.HasKey ? "Saved (encrypted)" : "Not set";

    private void ShowPresetHelp(ProviderPreset preset)
    {
        PresetNotes.Text = preset.Notes;
        ModelHint.Text = $"Model name: {preset.ModelHint}.";
    }

    private void Scorer_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading) ShowPanels();
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is not ProviderPreset preset) return;
        ShowPresetHelp(preset);
        if (_loading) return;

        // Fill the URL for a known server; keep whatever was typed for Custom.
        if (preset.BaseUrl.Length > 0) BaseUrlBox.Text = preset.BaseUrl;
        StructuredBox.SelectedIndex = 0;
        if (preset.Local && ItemsPerRequestBox.Text.Trim() == "0") ItemsPerRequestBox.Text = "15";
    }

    private void OpenAiField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshKeys();
    }

    private void BrowseClaude_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "claude.exe|claude.exe|Programs (*.exe)|*.exe", Title = "Find claude.exe" };
        if (dialog.ShowDialog(this) == true) ClaudePathBox.Text = dialog.FileName;
    }

    private void AnthropicKey_Click(object sender, RoutedEventArgs e)
    {
        new ApiKeyWindow(ApiKeyStore.Anthropic, "Anthropic", "console.anthropic.com") { Owner = this }.ShowDialog();
        RefreshKeys();
    }

    private void OpenAiKey_Click(object sender, RoutedEventArgs e)
    {
        var name = (PresetBox.SelectedItem as ProviderPreset)?.Name ?? "Server";
        new ApiKeyWindow(ApiKeyStore.OpenAiCompatible, name, $"your {name} account") { Owner = this }.ShowDialog();
        RefreshKeys();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var (settings, error) = ReadForm();
        if (settings is null)
        {
            MessageBox.Show(this, error, "Scoring settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        settings.Save();
        DialogResult = true;
    }

    /// <summary>Scores one sample PRA notice; the profile's guard should cap it at 1.</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var (settings, error) = ReadForm();
        error ??= ScorerFactory.Problem(settings!);
        if (error is not null)
        {
            TestResult.SetTone(Tone.Critical);
            TestResult.Text = error;
            return;
        }

        TestButton.IsEnabled = false;
        TestResult.SetTone(Tone.Secondary);
        TestResult.Text = "Testing… (a local model can take a minute)";
        var watch = Stopwatch.StartNew();
        try
        {
            var scorer = ScorerFactory.Create(settings!);
            var profile = await File.ReadAllTextAsync(_profilePath);
            var result = await Task.Run(() => scorer.ScoreAsync(profile, SampleRun()));
            var item = result.Items.FirstOrDefault();

            TestResult.SetTone(item?.Score == 1 ? Tone.Success : Tone.Caution);
            TestResult.Text = item is null
                ? $"Connected in {watch.Elapsed.TotalSeconds:0}s, but the model returned no items."
                : $"Works ({watch.Elapsed.TotalSeconds:0}s): scored the sample PRA notice {item.Score}" +
                  (item.Score == 1 ? " as expected (PRA guard caps it at 1)." : "; the profile expects 1, so this model may need checking.");
        }
        catch (Exception ex)
        {
            TestResult.SetTone(Tone.Critical);
            TestResult.Text = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static FetchResult SampleRun() => new(
        [],
        [
            new FedRegDocument(
                "2026-00001", "internal-revenue-service", "Notice",
                "Agency Information Collection Activities; Comment Request on Form 3520-A, Annual Information Return of Foreign Trust With a U.S. Owner",
                "In accordance with the Paperwork Reduction Act of 1995, the IRS is inviting comments on the information collection request outlined in this notice.",
                "Notice and request for comments.", "2026-10-28", null, "2026-12-28",
                "https://www.federalregister.gov/documents/2026/10/28/2026-00001/sample", []),
        ],
        []);
}
