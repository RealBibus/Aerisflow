using System;
using System.Windows;

namespace Aerisflow.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool uninstall = false;
        bool silent = false;
        foreach (string arg in e.Args)
        {
            if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            else if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)) silent = true;
        }

        if (silent)
        {
            try
            {
                var engine = new InstallEngine();
                if (uninstall)
                {
                    engine.Uninstall();
                }
                else
                {
                    engine.Install(new InstallOptions(
                    StartWithWindows: true,
                    DesktopShortcut: true,
                    StartMenuShortcut: true,
                    LaunchAfterInstall: false));
                }
            }
            catch
            {
                Shutdown(1);
                return;
            }
            Shutdown();
            return;
        }

        var window = new InstallerWindow(uninstall);
        MainWindow = window;
        window.Show();
    }
}