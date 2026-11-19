using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aerisflow.Native;
using Aerisflow.Updating;

namespace Aerisflow.UI;

public partial class TrayMenuWindow : Window
{
    private static readonly string[] Modes = { "Auto", "Calm", "Storm" };
    private static readonly string[] PrecipNames = { "Snow", "Sleet", "Rain" };
    private const double DefSnowHue = 210;
    private const double DefSnowSat = 0.45;
    private const double DefRainHue = 215;
    private const double DefMistHue = 210;
    private readonly SnowEngine _engine;
    private readonly Action _onExit;
    private readonly AppSettings _settings;
    private bool _ready;
    private bool _closing;
    private bool _opacityOpen;
    private int _modeIndex;
    private int _precipIndex;
    private bool _checking;

    public TrayMenuWindow(SnowEngine engine, Action onExit)
    {
        InitializeComponent();
        _engine = engine;
        _onExit = onExit;
        _settings = AppSettings.Load();

        _engine.Intensity = _settings.Intensity;
        _engine.Opacity = _settings.Opacity;
        _engine.Melt = _settings.Melt;
        _engine.Wind = _settings.Wind;
        _engine.WindSwing = _settings.WindSwing;
        _engine.BassReaction = _settings.BassReaction;
        _engine.MistEnabled = _settings.Mist;
        _engine.SpeedMultiplier = _settings.Speed;
        _engine.TargetFps = _settings.TargetFps;
        _engine.MeltAfterSeconds = _settings.MeltAfterSeconds;
        _engine.FullscreenMode = _settings.FullscreenOn ? FullscreenOverlayMode.On : FullscreenOverlayMode.Off;
        _engine.FrostEnabled = _settings.Frost;
        _engine.SnowHue = _settings.SnowHue;
        _engine.SnowSat = _settings.SnowSat;
        _engine.RainHue = _settings.RainHue;
        _engine.RainSat = _settings.RainSat;
        _engine.MistHue = _settings.MistHue;
        _engine.MistSat = _settings.MistSat;
        _precipIndex = WetnessToIndex(_settings.Wetness);
        _engine.Wetness = IndexToWetness(_precipIndex);
        _modeIndex = Math.Clamp(_settings.ModeIndex, 0, Modes.Length - 1);
        UpdateManager.Service.Channel = _settings.Channel;

        IntensitySlider.Value = Math.Clamp(_engine.Intensity, IntensitySlider.Minimum, IntensitySlider.Maximum);
        IntensitySlider.ValueChanged += (_, e) =>
        {
            _engine.Intensity = e.NewValue;
            IntensityValue.Text = FormatIntensity(e.NewValue);
            SaveSettings();
        };
        IntensityValue.Text = FormatIntensity(IntensitySlider.Value);

        SpeedSlider.Value = Math.Clamp(_engine.SpeedMultiplier, SpeedSlider.Minimum, SpeedSlider.Maximum);
        SpeedSlider.ValueChanged += (_, e) =>
        {
            _engine.SpeedMultiplier = e.NewValue;
            SpeedValue.Text = $"{e.NewValue:0.0}x";
            SaveSettings();
        };
        SpeedValue.Text = $"{SpeedSlider.Value:0.0}x";

        PrecipValue.Text = PrecipNames[_precipIndex];

        WindSwingSlider.Value = Math.Clamp(_settings.WindSwing, 0, 1);
        WindSwingSlider.ValueChanged += (_, e) =>
        {
            _engine.WindSwing = e.NewValue;
            WindSwingValue.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            SaveSettings();
        };
        WindSwingValue.Text = $"{(int)Math.Round(WindSwingSlider.Value * 100)}%";

        BassSlider.Value = Math.Clamp(_settings.BassReaction, 0, 1);
        BassSlider.ValueChanged += (_, e) =>
        {
            App.ApplyBass(e.NewValue, _settings.BassSensitivity);
            BassValue.Text = e.NewValue <= 0.001 ? "Off" : $"{(int)Math.Round(e.NewValue * 100)}%";
            SaveSettings();
        };
        BassValue.Text = BassSlider.Value <= 0.001 ? "Off" : $"{(int)Math.Round(BassSlider.Value * 100)}%";

        OpacitySlider.Value = Math.Clamp(_engine.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        OpacitySlider.ValueChanged += (_, e) =>
        {
            _engine.Opacity = e.NewValue;
            OpacityValue.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            SaveSettings();
        };
        OpacityValue.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";

        if (_engine.TargetFps == 0) _engine.TargetFps = 60;
        PerformanceValue.Text = _engine.TargetFps >= 60 ? "High (60 FPS)" : "Normal (30 FPS)";
        FullscreenValue.Text = _engine.FullscreenMode == FullscreenOverlayMode.On ? "Hide all" : "Snow only";
        ModeValue.Text = Modes[_modeIndex];
        ApplyMode();

        FrostToggle.IsChecked = _engine.FrostEnabled;
        FrostToggle.Checked += (_, _) => { _engine.FrostEnabled = true; SaveSettings(); };
        FrostToggle.Unchecked += (_, _) => { _engine.FrostEnabled = false; SaveSettings(); };

        MistToggle.IsChecked = _settings.Mist;
        MistToggle.Checked += (_, _) => { _engine.MistEnabled = true; SaveSettings(); };
        MistToggle.Unchecked += (_, _) => { _engine.MistEnabled = false; SaveSettings(); };

        MeltToggle.IsChecked = _engine.Melt;
        MeltToggle.Checked += (_, _) => { _engine.Melt = true; SaveSettings(); };
        MeltToggle.Unchecked += (_, _) => { _engine.Melt = false; SaveSettings(); };
        MeltAfterValue.Text = _engine.MeltAfterSeconds > 0 ? _engine.MeltAfterSeconds + "s" : "Off";

        SnowHueSlider.Value = Math.Clamp(_settings.SnowHue, 0, 360);
        SnowHueSlider.ValueChanged += (_, e) =>
        {
            _engine.SnowHue = e.NewValue;
            if (SeparateRainToggle.IsChecked != true) SyncRainToSnow();
            SnowHueValue.Text = $"{(int)e.NewValue}°";
            SaveSettings();
        };
        SnowHueValue.Text = $"{(int)SnowHueSlider.Value}°";

        SnowSatSlider.Value = Math.Clamp(_settings.SnowSat, 0, 1);
        SnowSatSlider.ValueChanged += (_, e) =>
        {
            _engine.SnowSat = e.NewValue;
            if (SeparateRainToggle.IsChecked != true) SyncRainToSnow();
            SnowSatValue.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            SaveSettings();
        };
        SnowSatValue.Text = $"{(int)Math.Round(SnowSatSlider.Value * 100)}%";

        SeparateRainToggle.IsChecked = _settings.SeparateRainColor;
        RainColorBlock.Visibility = _settings.SeparateRainColor ? Visibility.Visible : Visibility.Collapsed;
        SeparateRainToggle.Checked += (_, _) =>
        {
            RainColorBlock.Visibility = Visibility.Visible;
            SaveSettings();
        };
        SeparateRainToggle.Unchecked += (_, _) =>
        {
            RainColorBlock.Visibility = Visibility.Collapsed;
            SyncRainToSnow();
            SaveSettings();
        };

        RainHueSlider.Value = Math.Clamp(_settings.RainHue, 0, 360);
        RainHueSlider.ValueChanged += (_, e) =>
        {
            _engine.RainHue = e.NewValue;
            RainHueValue.Text = $"{(int)e.NewValue}°";
            SaveSettings();
        };
        RainHueValue.Text = $"{(int)RainHueSlider.Value}°";

        MistHueSlider.Value = Math.Clamp(_settings.MistHue, 0, 360);
        MistHueSlider.ValueChanged += (_, e) =>
        {
            _engine.MistHue = e.NewValue;
            MistHueValue.Text = $"{(int)e.NewValue}°";
            SaveSettings();
        };
        MistHueValue.Text = $"{(int)MistHueSlider.Value}°";

        AutoStartToggle.IsChecked = _settings.AutoStart;
        AutoStartToggle.Checked += (_, _) => { _settings.AutoStart = true; UpdateManager.ApplyAutoStart(true); _settings.Save(); };
        AutoStartToggle.Unchecked += (_, _) => { _settings.AutoStart = false; UpdateManager.ApplyAutoStart(false); _settings.Save(); };

        BetaToggle.IsChecked = _settings.Channel == "beta";
        BetaToggle.Checked += (_, _) => { _settings.Channel = "beta"; UpdateManager.Service.Channel = "beta"; _settings.Save(); };
        BetaToggle.Unchecked += (_, _) => { _settings.Channel = "stable"; UpdateManager.Service.Channel = "stable"; _settings.Save(); };

        UpdateNowButton.Click += async (_, _) => await UpdateNowAsync();
        DeferButton.Click += async (_, _) => await DeferAsync();
        UpdateManager.UpdateAvailable += OnUpdateAvailable;
        UpdateManager.StageProgress += OnStageProgress;
        UpdateManager.StageCompleted += OnStageCompleted;
        if (UpdateManager.Pending != null) ShowUpdateCard(UpdateManager.Pending);

        ClearButton.Click += (_, _) => _engine.Clear();
        QuitButton.Click += (_, _) => { _closing = true; _onExit(); };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            UpdateManager.UpdateAvailable -= OnUpdateAvailable;
            UpdateManager.StageProgress -= OnStageProgress;
            UpdateManager.StageCompleted -= OnStageCompleted;
        };
    }

    private static int WetnessToIndex(double wetness)
    => wetness <= 0.05 ? 0 : (wetness < 0.75 ? 1 : 2);

    private static double IndexToWetness(int index)
    => index switch
    {
        0 => 0.0,
        1 => 0.5,
        _ => 1.0,
    };

    private void SyncRainToSnow()
    {
        _engine.RainHue = _engine.SnowHue;
        _engine.RainSat = _engine.SnowSat;
    }

    private void ResetColorButton_Click(object sender, RoutedEventArgs e)
    {
        SeparateRainToggle.IsChecked = false;
        RainColorBlock.Visibility = Visibility.Collapsed;

        SnowHueSlider.Value = DefSnowHue;
        SnowSatSlider.Value = DefSnowSat;
        RainHueSlider.Value = DefSnowHue;
        MistHueSlider.Value = DefMistHue;

        _engine.RainSat = 0.5;
        _engine.MistSat = 0.3;

        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.Intensity = _engine.Intensity;
        _settings.Opacity = _engine.Opacity;
        _settings.Melt = _engine.Melt;
        _settings.Wind = _engine.Wind;
        _settings.WindSwing = _engine.WindSwing;
        _settings.BassReaction = _engine.BassReaction;
        _settings.Mist = MistToggle.IsChecked == true;
        _settings.Speed = _engine.SpeedMultiplier;
        _settings.TargetFps = _engine.TargetFps;
        _settings.MeltAfterSeconds = _engine.MeltAfterSeconds;
        _settings.FullscreenOn = _engine.FullscreenMode == FullscreenOverlayMode.On;
        _settings.Frost = _engine.FrostEnabled;
        _settings.ModeIndex = _modeIndex;

        _settings.Wetness = _engine.Wetness;
        _settings.SnowHue = _engine.SnowHue;
        _settings.SnowSat = _engine.SnowSat;
        _settings.SeparateRainColor = SeparateRainToggle.IsChecked == true;
        _settings.RainHue = _settings.SeparateRainColor ? _engine.RainHue : _engine.SnowHue;
        _settings.RainSat = _settings.SeparateRainColor ? _engine.RainSat : _engine.SnowSat;
        _settings.MistHue = _engine.MistHue;
        _settings.MistSat = _engine.MistSat;
        _settings.Save();
    }

    private static string FormatIntensity(double value)
    {
        string level = value < 0.7 ? "Light"
        : value < 1.4 ? "Normal"
        : value < 2.3 ? "Heavy"
        : "Whiteout";
        return $"{level} · {value:0.0}x";
    }

    private void OnUpdateAvailable(UpdateInfo info) => Dispatcher.Invoke(() => ShowUpdateCard(info));
    private void OnStageProgress(double v) => Dispatcher.Invoke(() => UpdateProgress.Value = v);

    private void OnStageCompleted() => Dispatcher.Invoke(() =>
    {
        UpdateProgress.Value = 1;
        UpdateStatusValue.Text = "Ready · installs on shutdown";
        UpdateNotesText.Text = "The update is downloaded. It will be installed automatically " +
        "when you sign out, shut down or exit Aerisflow.";
    });

    private void ShowUpdateCard(UpdateInfo info)
    {
        UpdateVersionText.Text = "Version " + info.Manifest.Version;
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(info.Manifest.ReleaseNotes)
        ? "No release notes."
        : info.Manifest.ReleaseNotes;
        UpdateStatusValue.Text = UpdateManager.IsStaged
        ? "Ready · installs on shutdown"
        : "Downloading in background…";
        UpdateCard.Visibility = Visibility.Visible;
    }

    private async void CheckUpdateHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_checking) return;
        if (!UpdateManager.UpdatesEnabled)
        {
            UpdateStatusValue.Text = "Only in installed builds";
            return;
        }
        _checking = true;
        UpdateStatusValue.Text = "Checking…";
        await UpdateManager.CheckNowAsync();
        if (UpdateManager.Pending == null) UpdateStatusValue.Text = "Up to date";
        else if (UpdateManager.IsStaged) UpdateStatusValue.Text = "Ready · installs on shutdown";
        _checking = false;
    }

    private async System.Threading.Tasks.Task UpdateNowAsync()
    {
        UpdateNowButton.IsEnabled = false;
        DeferButton.IsEnabled = false;
        UpdateStatusValue.Text = "Downloading…";
        try
        {
            await UpdateManager.StagePendingAsync();
            await UpdateManager.RestartWithVeilAsync();
        }
        catch
        {
            UpdateStatusValue.Text = "Failed";
            UpdateNowButton.IsEnabled = true;
            DeferButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task DeferAsync()
    {
        UpdateNowButton.IsEnabled = false;
        DeferButton.IsEnabled = false;
        UpdateStatusValue.Text = "Downloading…";
        try
        {
            await UpdateManager.StagePendingAsync();
            UpdateManager.MarkDeferred();
            UpdateStatusValue.Text = "On shutdown";
        }
        catch
        {
            UpdateStatusValue.Text = "Failed";
            UpdateNowButton.IsEnabled = true;
            DeferButton.IsEnabled = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.UseDarkMode(hwnd);
        WindowEffects.UseRoundCorners(hwnd);
        WindowEffects.EnableStrongBlur(hwnd);
        WindowEffects.ApplyAcrylic(hwnd);
        WindowEffects.TrySystemBackdrop(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateLayout();
            PlaceAtCursor();
            var border = RootBorder;
            var translate = new TranslateTransform(0, 8);
            border.RenderTransform = translate;
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            border.BeginAnimation(UIElement.OpacityProperty, fade);
            var move = new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(150)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            translate.BeginAnimation(TranslateTransform.YProperty, move);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ready = true));
        }));
    }

    private void PlaceAtCursor()
    {
        var mousePos = System.Windows.Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        double mouseDipX = mousePos.X / dpi.DpiScaleX;
        double mouseDipY = mousePos.Y / dpi.DpiScaleY;
        double x = mouseDipX - ActualWidth;
        double y = mouseDipY - ActualHeight;
        var screen = System.Windows.Forms.Screen.FromPoint(mousePos);
        double workLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
        double workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
        double workRight = screen.WorkingArea.Right / dpi.DpiScaleX;
        double workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;
        if (x < workLeft) x = workLeft;
        if (y < workTop) y = workTop;
        if (x + ActualWidth > workRight) x = workRight - ActualWidth;
        if (y + ActualHeight > workBottom) y = workBottom - ActualHeight;
        Left = x;
        Top = y;
    }

    private void OpacityHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _opacityOpen = !_opacityOpen;
        OpacitySlider.Visibility = _opacityOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyMode()
    {
        switch (Modes[_modeIndex])
        {
            case "Storm":
            _engine.ModeIntensityBoost = 2.0;
            _engine.ModeSpeedBoost = 2.0;
            _engine.Wind = 40;
            _engine.Storm = true;
            break;
            case "Calm":
            _engine.ModeIntensityBoost = 0.5;
            _engine.ModeSpeedBoost = 0.7;
            _engine.Wind = 0;
            _engine.Storm = false;
            break;
            default:
            _engine.ModeIntensityBoost = 1.0;
            _engine.ModeSpeedBoost = 1.0;
            _engine.Wind = 0;
            _engine.Storm = false;
            break;
        }
    }

    private void ModeHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _modeIndex = (_modeIndex + 1) % Modes.Length;
        ModeValue.Text = Modes[_modeIndex];
        ApplyMode();
        SaveSettings();
    }

    private void PrecipHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _precipIndex = (_precipIndex + 1) % PrecipNames.Length;
        _engine.Wetness = IndexToWetness(_precipIndex);
        PrecipValue.Text = PrecipNames[_precipIndex];
        SaveSettings();
    }

    private void PerformanceHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _engine.TargetFps = _engine.TargetFps >= 60 ? 30 : 60;
        PerformanceValue.Text = _engine.TargetFps >= 60 ? "High (60 FPS)" : "Normal (30 FPS)";
        SaveSettings();
    }

    private void FullscreenHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _engine.FullscreenMode = _engine.FullscreenMode == FullscreenOverlayMode.On
        ? FullscreenOverlayMode.Off
        : FullscreenOverlayMode.On;
        FullscreenValue.Text = _engine.FullscreenMode == FullscreenOverlayMode.On ? "Hide all" : "Snow only";
        SaveSettings();
    }

    private void MeltAfterHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        double[] values = { 0, 30, 120, 600 };
        int index = Array.IndexOf(values, _engine.MeltAfterSeconds);
        if (index < 0) index = 0;
        index = (index + 1) % values.Length;
        _engine.MeltAfterSeconds = values[index];
        MeltAfterValue.Text = index == 0 ? "Off" : values[index] + "s";
        SaveSettings();
    }

    private void CloseMenu()
    {
        if (_ready && !_closing) Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => CloseMenu();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CloseMenu(); }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _closing = true;
}