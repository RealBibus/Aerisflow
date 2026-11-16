using System.Windows;
using System.Windows.Media;

namespace Aerisflow;

public class SnowCanvas : FrameworkElement
{
    public SnowEngine? Engine { get; set; }
    protected override void OnRender(DrawingContext dc) { }
}