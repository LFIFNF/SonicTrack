using System.IO;
using System.Windows;
using SonicTrack.App.Shell;

namespace SonicTrack.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Same crash handling as the V6.1 component application.
        DispatcherUnhandledException += (_, a) =>
        {
            TryLog(a.Exception);
            a.Handled = true;
            MessageBox.Show(a.Exception.Message, "SonicTrack");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) TryLog(ex);
        };
        base.OnStartup(e);
        AppPreferences.Initialize();
        Exit += (_, _) => AppPreferences.Flush();
        var launcher = new LauncherWindow();
        MainWindow = launcher;
        var movement = e.Args.FirstOrDefault(a => a.StartsWith("--profile-window-move="));
        if (movement != null) WindowMoveDiagnostics.Attach(launcher, movement.Split('=', 2)[1]);
        launcher.Show();
    }

    internal static void TryLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, DateTime.Now.ToString("o") + " " + ex + Environment.NewLine);
        }
        catch { }
    }
}
