using System.IO;
using System.Windows;

namespace RegWatch.App;

/// <summary>The user guide (docs/GUIDE.md, embedded in the app), opened from Help or F1.</summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await MarkdownView.InitAsync(Viewer);
            MarkdownView.ShowDocument(Viewer, Guide());
        };
    }

    private static string Guide()
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream("RegWatch.Guide.md")!;
        return new StreamReader(stream).ReadToEnd();
    }
}
