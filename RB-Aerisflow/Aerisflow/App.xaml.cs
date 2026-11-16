using System;
using System.Windows;
using System.Windows.Threading;
using Aerisflow.Native;
using Aerisflow.UI;

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

        // Hardware path first: Direct2D over DirectComposition. If the machine has no
        // usable D3D11 device (remote session, very old GPU) the WPF renderer takes
        // over with exactly the same engine and window tracker.
        _overlay = GpuSnowOverlay.TryCreate(_engine);
        if (_overlay != null)
        {
            _overlay.Failed += OnOverlayFailed;
        }
        else
        {
            ShowWpfOverlay();
        }
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

    /// <summary>
    /// Lazy UI: the flyout is built on the first click and released the moment it
    /// closes. Nothing of its XAML tree, brushes or effects stays resident while the
    /// app is just snowing in the background.
    /// </summary>
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

            // The window is gone; hand its visual tree back now instead of waiting
            // for a generation that may never come.
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
