using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RegWatch.Runner;

/// <summary>
/// A Windows notification at the end of a scheduled run. Windows 10 and 11 show the tray balloon
/// as a toast; clicking it opens the RegWatch app. The tray icon needs an STA thread with a
/// message loop, so the notification runs on its own thread for a few seconds.
/// </summary>
internal static class Notifier
{
    public static void Show(string title, string text, string root)
    {
        var thread = new Thread(() =>
        {
            using var icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Information,
                Text = "RegWatch",
                Visible = true,
            };
            icon.BalloonTipClicked += (_, _) => OpenApp(root);
            icon.ShowBalloonTip(15_000, title, text, ToolTipIcon.None);

            // Keep the icon (and so the toast's click handler) alive long enough to be clicked.
            var until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            icon.Visible = false;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void OpenApp(string root)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "RegWatch.App.exe");
        if (!File.Exists(app)) return;
        var psi = new ProcessStartInfo(app) { UseShellExecute = false };
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root);
        Process.Start(psi);
    }
}
