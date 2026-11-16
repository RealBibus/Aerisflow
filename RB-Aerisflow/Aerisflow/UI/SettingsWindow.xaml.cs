using System.Windows;
using System.Windows.Controls;

namespace Aerisflow.UI;

public partial class SettingsWindow : Window
{
    private readonly SnowEngine _engine;
    private bool _isInitializing = true;

    public SettingsWindow(SnowEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        // Загружаем текущие значения
        FpsSlider.Value = _engine.TargetFps;
        FpsText.Text = $"{_engine.TargetFps} FPS";
        
        IntensitySlider.Value = _engine.Intensity;
        OpacitySlider.Value = _engine.Opacity;
        
        _isInitializing = false;
    }

    private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        int fps = (int)e.NewValue;
        _engine.TargetFps = fps;
        FpsText.Text = fps == 0 ? "Без лимита" : $"{fps} FPS";
    }

    private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        _engine.Intensity = e.NewValue;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        _engine.Opacity = e.NewValue;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}