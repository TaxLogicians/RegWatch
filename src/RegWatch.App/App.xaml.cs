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

        Theme.Apply(AppSettings.Load().Theme);

        var root = RunPipeline.DefaultRoot(rootArg);
        if (!File.Exists(Path.Combine(root, "PROFILE.md")) && !CreateDataFolder(root))
        {
            Shutdown(1);
            return;
        }

        new MainWindow(root).Show();
    }

    /// <summary>First run after install: offers to create the data folder with the default PROFILE.md shipped beside the app.</summary>
    private static bool CreateDataFolder(string root)
    {
        var template = Path.Combine(AppContext.BaseDirectory, "Defaults", "PROFILE.md");
        if (!File.Exists(template))
        {
            MessageBox.Show($"No PROFILE.md in {root}.\n\nStart RegWatch with --root <folder> or set REGWATCH_ROOT.", "RegWatch");
            return false;
        }

        var answer = MessageBox.Show(
            $"RegWatch keeps its profile, digests and run state in\n{root}\n\n" +
            "That folder has no PROFILE.md yet. Create it with the default profile? You can change the profile afterwards with Edit profile.",
            "Set up RegWatch", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return false;

        Directory.CreateDirectory(Path.Combine(root, "digests"));
        File.Copy(template, Path.Combine(root, "PROFILE.md"));
        return true;
    }
}
