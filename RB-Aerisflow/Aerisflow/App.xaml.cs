using System;
using System.Windows;
using System.Windows.Threading;
using Aerisflow.Native;
using Aerisflow.UI;
using Aerisflow.Updating;

namespace Aerisflow;

public partial class App : global::System.Windows.Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private TrayMenuWindow? _menu;
    private GpuSnowOverlay? _overlay;
    private readonly SnowEngine _engine = new SnowEngine();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _tray = new TrayIcon { OnOpenMenu = ToggleTrayMenu };
        _tray.Start(SnowIcon.Create());

        _overlay = GpuSnowOverlay.TryCreate(_engine);
        if (_overlay != null)
        {
            _overlay.Failed += OnOverlayFailed;
        }
        else
        {
            ShowWpfOverlay();
        }

        UpdateManager.Attach(this);
    }

    private void ShowWpfOverlay()
    {
        _window = new MainWindow(_engine);
        _window.Show();
    }

    private void OnOverlayFailed()
    {
        if (_overlay == null) return;
        _overlay.Failed -= OnOverlayFailed;
        _overlay.Dispose();
        _overlay = null;
        ShowWpfOverlay();
    }

    private void ToggleTrayMenu()
    {
        if (_menu != null)
        {
            _menu.Close();
            return;
        }

        var menu = new TrayMenuWindow(_engine, Shutdown);
        _menu = menu;
        menu.Closed += (s, e) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;

            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => GC.Collect(2, GCCollectionMode.Optimized, false, false)));
        };

        menu.Show();
        menu.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _menu?.Close();
        _menu = null;
        if (_overlay != null)
        {
            _overlay.Failed -= OnOverlayFailed;
            _overlay.Dispose();
            _overlay = null;
        }
        _tray?.Shutdown();
        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }
}