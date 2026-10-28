using System.Windows;
using System.Windows.Controls;
using RegWatch.Core;

namespace RegWatch.App;

/// <summary>Theme-aware colour keys from WPF's Fluent theme, for text set from code.</summary>
public static class Tone
{
    public const string Secondary = "TextFillColorSecondaryBrush";
    public const string Critical = "SystemFillColorCriticalBrush";
    public const string Caution = "SystemFillColorCautionBrush";
    public const string Success = "SystemFillColorSuccessBrush";

    /// <summary>Colours the text with a resource reference, so it follows later theme switches.</summary>
    public static void SetTone(this TextBlock text, string key) =>
        text.SetResourceReference(TextBlock.ForegroundProperty, key);
}

public static class Theme
{
    /// <summary>Switches every open and future window to the Fluent theme in light, dark or the Windows setting.</summary>
    public static void Apply(AppTheme theme)
    {
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 10
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }
}
