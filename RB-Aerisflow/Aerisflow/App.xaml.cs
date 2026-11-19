using System;
using System.Threading;
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
    private Mutex? _instanceMutex;
    private static AudioPulse? _audio;

    public static SnowEngine? Engine { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\Aerisflow.SingleInstance", out bool created);
        if (!created)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        Engine = _engine;
        var settings = AppSettings.Load();
        _engine.Intensity = settings.Intensity;
        _engine.Opacity = settings.Opacity;
        _engine.Melt = settings.Melt;
        _engine.Wind = settings.Wind;
        _engine.WindSwing = settings.WindSwing;
        _engine.BassReaction = settings.BassReaction;
        _engine.MistEnabled = settings.Mist;
        _engine.SpeedMultiplier = settings.Speed;
        _engine.TargetFps = settings.TargetFps;
        _engine.MeltAfterSeconds = settings.MeltAfterSeconds;
        _engine.FullscreenMode = settings.FullscreenOn ? FullscreenOverlayMode.On : FullscreenOverlayMode.Off;
        _engine.FrostEnabled = settings.Frost;

        _engine.Wetness = settings.Wetness;
        _engine.SnowHue = settings.SnowHue;
        _engine.SnowSat = settings.SnowSat;
        _engine.RainHue = settings.SeparateRainColor ? settings.RainHue : settings.SnowHue;
        _engine.RainSat = settings.SeparateRainColor ? settings.RainSat : settings.SnowSat;
        _engine.MistHue = settings.MistHue;
        _engine.MistSat = settings.MistSat;

        switch (settings.ModeIndex)
        {
            case 1:
            _engine.ModeIntensityBoost = 0.5;
            _engine.ModeSpeedBoost = 0.7;
            _engine.Wind = 0;
            _engine.Storm = false;
            break;
            case 2:
            _engine.ModeIntensityBoost = 2.0;
            _engine.ModeSpeedBoost = 2.0;
            _engine.Wind = 40;
            _engine.Storm = true;
            break;
            default:
            _engine.ModeIntensityBoost = 1.0;
            _engine.ModeSpeedBoost = 1.0;
            _engine.Wind = 0;
            _engine.Storm = false;
            break;
        }

        ApplyBass(settings.BassReaction, settings.BassSensitivity);

        _tray = new TrayIcon { OnOpenMenu = ToggleTrayMenu };
        _tray.Start(SnowIcon.Create());

        _overlay = GpuSnowOverlay.TryCreate(_engine);
        if (_overlay != null) _overlay.Failed += OnOverlayFailed;
        else ShowWpfOverlay();

        UpdateManager.Attach(this);
    }

    public static void ApplyBass(double reaction, double sensitivity)
    {
        var engine = Engine;
        if (engine == null) return;
        engine.BassReaction = reaction;

        if (reaction <= 0.001)
        {
            _audio?.Stop();
            engine.Audio = null;
            return;
        }

        if (_audio == null) _audio = new AudioPulse();
        _audio.Sensitivity = sensitivity <= 0 ? 1.0 : sensitivity;
        engine.Audio = _audio;
        _audio.Start();
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
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        _menu?.Close();
        _menu = null;

        if (_overlay != null)
        {
            _overlay.Failed -= OnOverlayFailed;
            _overlay.Dispose();
            _overlay = null;
        }

        _audio?.Dispose();
        _audio = null;

        _tray?.Shutdown();
        _tray?.Dispose();
        _tray = null;

        base.OnExit(e);
    }
}