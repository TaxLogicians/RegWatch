using System.IO;
using System.Windows;
using RegWatch.Core;

namespace RegWatch.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        string? rootArg = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--root") rootArg = e.Args[i + 1];

        var root = RunPipeline.DefaultRoot(rootArg);
        if (!File.Exists(Path.Combine(root, "PROFILE.md")))
        {
            MessageBox.Show($"No PROFILE.md in {root}.\n\nStart RegWatch with --root <folder> or set REGWATCH_ROOT.", "RegWatch");
            Shutdown(1);
            return;
        }

        new MainWindow(root).Show();
    }
}
