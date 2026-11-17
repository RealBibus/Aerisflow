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
    private readonly SnowEngine _engine;
    private readonly Action _onExit;
    private readonly AppSettings _settings;
    private bool _ready;
    private bool _closing;
    private bool _opacityOpen;
    private int _modeIndex;
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
        _engine.SpeedMultiplier = _settings.Speed;
        _engine.TargetFps = _settings.TargetFps;
        _engine.MeltAfterSeconds = _settings.MeltAfterSeconds;
        _engine.FullscreenMode = _settings.FullscreenOn ? FullscreenOverlayMode.On : FullscreenOverlayMode.Off;
        _modeIndex = Math.Clamp(_settings.ModeIndex, 0, Modes.Length - 1);
        UpdateManager.Service.Channel = _settings.Channel;

        IntensitySlider.Value = Math.Clamp(engine.Intensity, IntensitySlider.Minimum, IntensitySlider.Maximum);
        IntensitySlider.ValueChanged += (_, e) =>
        {
            _engine.Intensity = e.NewValue;
            IntensityValue.Text = $"{e.NewValue:0.00}";
            SaveSettings();
        };
        IntensityValue.Text = $"{IntensitySlider.Value:0.00}";

        SpeedSlider.Value = Math.Clamp(engine.SpeedMultiplier, SpeedSlider.Minimum, SpeedSlider.Maximum);
        SpeedSlider.ValueChanged += (_, e) =>
        {
            _engine.SpeedMultiplier = e.NewValue;
            SpeedValue.Text = $"{e.NewValue:0.0}x";
            SaveSettings();
        };
        SpeedValue.Text = $"{SpeedSlider.Value:0.0}x";

        OpacitySlider.Value = Math.Clamp(engine.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        OpacitySlider.ValueChanged += (_, e) =>
        {
            _engine.Opacity = e.NewValue;
            OpacityValue.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
            SaveSettings();
        };
        OpacityValue.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";

        if (_engine.TargetFps == 0) _engine.TargetFps = 60;
        PerformanceValue.Text = _engine.TargetFps >= 60 ? "High (60 FPS)" : "Normal (30 FPS)";
        FullscreenValue.Text = _engine.FullscreenMode == FullscreenOverlayMode.On ? "On" : "Off";
        ModeValue.Text = Modes[_modeIndex];
        ApplyMode();

        MeltToggle.IsChecked = _engine.Melt;
        MeltToggle.Checked   += (_, _) => { _engine.Melt = true; SaveSettings(); };
        MeltToggle.Unchecked += (_, _) => { _engine.Melt = false; SaveSettings(); };
        MeltAfterValue.Text = _engine.MeltAfterSeconds > 0 ? _engine.MeltAfterSeconds + "s" : "Off";

        AutoStartToggle.IsChecked = _settings.AutoStart;
        AutoStartToggle.Checked   += (_, _) => { _settings.AutoStart = true; UpdateManager.ApplyAutoStart(true); _settings.Save(); };
        AutoStartToggle.Unchecked += (_, _) => { _settings.AutoStart = false; UpdateManager.ApplyAutoStart(false); _settings.Save(); };

        BetaToggle.IsChecked = _settings.Channel == "beta";
        BetaToggle.Checked   += (_, _) => { _settings.Channel = "beta"; UpdateManager.Service.Channel = "beta"; _settings.Save(); };
        BetaToggle.Unchecked += (_, _) => { _settings.Channel = "stable"; UpdateManager.Service.Channel = "stable"; _settings.Save(); };

        UpdateNowButton.Click += async (_, _) => await UpdateNowAsync();
        DeferButton.Click += async (_, _) => await DeferAsync();
        UpdateManager.UpdateAvailable += OnUpdateAvailable;
        UpdateManager.StageProgress += OnStageProgress;
        if (UpdateManager.Pending != null) ShowUpdateCard(UpdateManager.Pending);

        ClearButton.Click += (_, _) => _engine.Clear();
        QuitButton.Click  += (_, _) => { _closing = true; _onExit(); };

        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        Closed            += (_, _) =>
        {
            UpdateManager.UpdateAvailable -= OnUpdateAvailable;
            UpdateManager.StageProgress -= OnStageProgress;
        };
    }

    private void SaveSettings()
    {
        _settings.Intensity = _engine.Intensity;
        _settings.Opacity = _engine.Opacity;
        _settings.Melt = _engine.Melt;
        _settings.Wind = _engine.Wind;
        _settings.Speed = _engine.SpeedMultiplier;
        _settings.TargetFps = _engine.TargetFps;
        _settings.MeltAfterSeconds = _engine.MeltAfterSeconds;
        _settings.FullscreenOn = _engine.FullscreenMode == FullscreenOverlayMode.On;
        _settings.ModeIndex = _modeIndex;
        _settings.Save();
    }

    private void OnUpdateAvailable(UpdateInfo info) => Dispatcher.Invoke(() => ShowUpdateCard(info));

    private void OnStageProgress(double v) => Dispatcher.Invoke(() => UpdateProgress.Value = v);

    private void ShowUpdateCard(UpdateInfo info)
    {
        UpdateVersionText.Text = "Version " + info.Manifest.Version;
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(info.Manifest.ReleaseNotes) ? "No release notes." : info.Manifest.ReleaseNotes;
        UpdateStatusValue.Text = "v" + info.Manifest.Version;
        UpdateCard.Visibility = Visibility.Visible;
    }

    private async void CheckUpdateHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_checking) return;
        _checking = true;
        UpdateStatusValue.Text = "Checking…";
        await UpdateManager.CheckNowAsync();
        if (UpdateManager.Pending == null) UpdateStatusValue.Text = "Up to date";
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
        double workLeft   = screen.WorkingArea.Left   / dpi.DpiScaleX;
        double workTop    = screen.WorkingArea.Top    / dpi.DpiScaleY;
        double workRight  = screen.WorkingArea.Right  / dpi.DpiScaleX;
        double workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;

        if (x < workLeft) x = workLeft;
        if (y < workTop)  y = workTop;
        if (x + ActualWidth  > workRight)  x = workRight  - ActualWidth;
        if (y + ActualHeight > workBottom) y = workBottom - ActualHeight;

        Left = x;
        Top  = y;
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
                _engine.ModeSpeedBoost     = 2.0;
                _engine.Wind               = 40;
                break;
            case "Calm":
                _engine.ModeIntensityBoost = 0.5;
                _engine.ModeSpeedBoost     = 0.7;
                _engine.Wind               = 0;
                break;
            default:
                _engine.ModeIntensityBoost = 1.0;
                _engine.ModeSpeedBoost     = 1.0;
                _engine.Wind               = 0;
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
        FullscreenValue.Text = _engine.FullscreenMode == FullscreenOverlayMode.On ? "On" : "Off";
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