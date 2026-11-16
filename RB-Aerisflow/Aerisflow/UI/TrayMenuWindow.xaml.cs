using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aerisflow.Native;

namespace Aerisflow.UI;

public partial class TrayMenuWindow : Window
{
    private static readonly string[] Modes = { "Auto", "Calm", "Storm" };
    private readonly SnowEngine _engine;
    private readonly Action _onExit;
    private bool _ready;
    private bool _closing;
    private bool _opacityOpen;
    private int _modeIndex;

    public TrayMenuWindow(SnowEngine engine, Action onExit)
    {
        InitializeComponent();
        _engine = engine;
        _onExit = onExit;

        IntensitySlider.Value = Math.Clamp(engine.Intensity, IntensitySlider.Minimum, IntensitySlider.Maximum);
        OpacitySlider.Value = Math.Clamp(engine.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);

        IntensitySlider.ValueChanged += (s, e) =>
        {
            _engine.Intensity = e.NewValue;
            IntensityValue.Text = $"{e.NewValue:0.00}";
        };
        OpacitySlider.ValueChanged += (s, e) =>
        {
            _engine.Opacity = e.NewValue;
            OpacityValue.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        };

        MeltToggle.IsChecked = _engine.Melt;
        MeltToggle.Checked += (s, e) => _engine.Melt = true;
        MeltToggle.Unchecked += (s, e) => _engine.Melt = false;

        ClearButton.Click += (s, e) => _engine.Clear();
        
        // ИСПРАВЛЕНИЕ: Открываем окно настроек
        SettingsButton.Click += (s, e) =>
        {
            CloseMenu(); 
            var settingsWindow = new SettingsWindow(_engine);
            settingsWindow.Show();
        };

        QuitButton.Click += (s, e) =>
        {
            _closing = true;
            _onExit();
        };

        IntensityValue.Text = $"{IntensitySlider.Value:0.00}";
        OpacityValue.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";
        MeltAfterValue.Text = _engine.MeltAfterSeconds > 0 ? _engine.MeltAfterSeconds + "s" : "Off";

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.UseDarkMode(hwnd);
        WindowEffects.UseRoundCorners(hwnd);
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
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            border.BeginAnimation(UIElement.OpacityProperty, fade);

            var move = new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
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

        // Окно открывается так, чтобы правый нижний угол был возле курсора
        double x = mouseDipX - this.ActualWidth;
        double y = mouseDipY - this.ActualHeight;

        var screen = System.Windows.Forms.Screen.FromPoint(mousePos);
        double workLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
        double workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
        double workRight = screen.WorkingArea.Right / dpi.DpiScaleX;
        double workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;

        if (x < workLeft) x = workLeft;
        if (y < workTop) y = workTop;
        if (x + this.ActualWidth > workRight) x = workRight - this.ActualWidth;
        if (y + this.ActualHeight > workBottom) y = workBottom - this.ActualHeight;

        this.Left = x;
        this.Top = y;
    }

    private void OpacityHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _opacityOpen = !_opacityOpen;
        OpacitySlider.Visibility = _opacityOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ModeHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _modeIndex = (_modeIndex + 1) % Modes.Length;
        var mode = Modes[_modeIndex];
        ModeValue.Text = mode;
        _engine.Wind = mode == "Calm" ? 0 : mode == "Storm" ? 30 : _engine.Wind;
    }

    private void MeltAfterHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        double[] values = { 0, 30, 120, 600 };
        int index = Array.IndexOf(values, _engine.MeltAfterSeconds);
        if (index < 0) index = 0;
        index = (index + 1) % values.Length;
        _engine.MeltAfterSeconds = values[index];
        MeltAfterValue.Text = index == 0 ? "Off" : values[index] + "s";
    }

    private void CloseMenu()
    {
        if (_ready && !_closing) Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        CloseMenu();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CloseMenu();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
    }
}