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

        var settings = AppSettings.Load();
        _engine.Intensity = settings.Intensity;
        _engine.Opacity = settings.Opacity;
        _engine.Melt = settings.Melt;
        _engine.Wind = settings.Wind;
        _engine.SpeedMultiplier = settings.Speed;
        _engine.TargetFps = settings.TargetFps;
        _engine.MeltAfterSeconds = settings.MeltAfterSeconds;
        _engine.FullscreenMode = settings.FullscreenOn ? FullscreenOverlayMode.On : FullscreenOverlayMode.Off;

        switch (settings.ModeIndex)
        {
            case 1:
                _engine.ModeIntensityBoost = 0.5;
                _engine.ModeSpeedBoost = 0.7;
                _engine.Wind = 0;
                break;
            case 2:
                _engine.ModeIntensityBoost = 2.0;
                _engine.ModeSpeedBoost = 2.0;
                _engine.Wind = 40;
                break;
            default:
                _engine.ModeIntensityBoost = 1.0;
                _engine.ModeSpeedBoost = 1.0;
                _engine.Wind = 0;
                break;
        }

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

        if (settings.AutoStart)
        {
            UpdateManager.ApplyAutoStart(true);
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