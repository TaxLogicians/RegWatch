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
        if (AppSettings.LoadProblem is { } settingsProblem)
            MessageBox.Show(settingsProblem, "RegWatch", MessageBoxButton.OK, MessageBoxImage.Warning);

        var root = RunPipeline.DefaultRoot(rootArg);
        if (!File.Exists(Path.Combine(root, "PROFILE.md")))
        {
            // Only the default location is set up automatically; a folder named with --root or
            // REGWATCH_ROOT that has no profile is more likely a typo than a new data folder.
            var explicitRoot = rootArg is not null || Environment.GetEnvironmentVariable("REGWATCH_ROOT") is not null;
            if (explicitRoot || !CreateDataFolder(root))
            {
                if (explicitRoot)
                    MessageBox.Show($"No PROFILE.md in {root}.\n\nCheck the --root argument or the REGWATCH_ROOT variable.", "RegWatch");
                Shutdown(1);
                return;
            }
        }

        new MainWindow(root).Show();
    }

    /// <summary>First run after install: offers to create the data folder with the default PROFILE.md shipped beside the app.</summary>
    private static bool CreateDataFolder(string root)
    {
        var template = Path.Combine(AppContext.BaseDirectory, "Defaults", "PROFILE.md");
        if (!File.Exists(template))
        {
            MessageBox.Show($"No PROFILE.md in {root}, and no default profile was found next to the app ({template}).\n\n" +
                            "Reinstall RegWatch, or start it with --root <folder>.", "RegWatch");
            return false;
        }

        var answer = MessageBox.Show(
            $"RegWatch keeps its profile, digests and run state in\n{root}\n\n" +
            "That folder has no PROFILE.md yet. Create it with the default profile? You can change the profile afterwards with Edit profile.",
            "Set up RegWatch", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return false;

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "digests"));
            File.Copy(template, Path.Combine(root, "PROFILE.md"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show($"Could not create {root}:\n{ex.Message}", "RegWatch", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
