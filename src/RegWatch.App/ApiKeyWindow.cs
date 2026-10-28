using System.Windows;
using System.Windows.Controls;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>Small dialog that stores the Anthropic API key with DPAPI via <see cref="ApiKeyStore"/>.</summary>
public sealed class ApiKeyWindow : Window
{
    private readonly PasswordBox _box = new() { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(4) };

    public ApiKeyWindow()
    {
        Title = "Anthropic API key";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var envNote = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 }
            ? "\n\nANTHROPIC_API_KEY is set in your environment and takes precedence over a saved key."
            : "";

        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(14, 4, 14, 4) };
        var clear = new Button { Content = "Remove saved key", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };

        save.Click += (_, _) =>
        {
            if (_box.Password.Trim().Length == 0) return;
            ApiKeyStore.Save(_box.Password);
            DialogResult = true;
        };
        clear.Click += (_, _) =>
        {
            ApiKeyStore.Clear();
            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(clear);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Paste an API key from console.anthropic.com. It is encrypted for your Windows account " +
                   $"(DPAPI) and saved to {ApiKeyStore.KeyPath}. A weekly run costs well under a dollar." + envNote,
        });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _box.Focus();
    }
}
