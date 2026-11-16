using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Aerisflow.Native;

namespace Aerisflow;

public partial class MainWindow : Window
{
    public SnowEngine Engine { get; }

    private readonly SnowCanvas _canvas;
    private WindowTracker? _tracker;
    private DispatcherTimer? _topmostTimer;
    private DateTime _lastRender = DateTime.UtcNow;

    private IntPtr _hwnd;
    private int _monitorLeft;
    private int _monitorTop;
    private int _monitorWidth;
    private int _monitorHeight;
    private double _dipPerPixel = 1.0;
    private double _workLeft;
    private double _workTop;
    private double _workWidth;
    private double _workHeight;
    private double _workBottom;

    private double _lastCursorX = double.NaN;
    private double _lastCursorY = double.NaN;

    public MainWindow(SnowEngine engine)
    {
        InitializeComponent();
        Engine = engine;
        _canvas = SnowCanvas;
        _canvas.Engine = engine;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += (s, e) => ApplyOverlayGeometry();
        Closing += MainWindow_Closing;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        MakeClickThroughAndTopmost(_hwnd);
        MeasureMonitor();
        ApplyOverlayGeometry();

        // The engine works in the overlay's DIPs, so the reported windows are converted
        // with the same scale. The ground is the work area: the top edge of a bottom
        // taskbar, the floor of the desktop, or just above a vertical task bar.
        Engine.SetScreen(0, _monitorHeight * _dipPerPixel);
        Engine.Width = _monitorWidth * _dipPerPixel;
        Engine.SetGround(_workLeft, _workBottom, _workLeft + _workWidth);

        _tracker = new WindowTracker(_hwnd, _monitorLeft, _monitorTop, _dipPerPixel);
        _tracker.Start();
        // Other topmost windows (the taskbar after a click, apps that call SetWindowPos)
        // can stack above ours, which would hide the snow behind them. Put it back on
        // top the way every snow overlay has to.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _topmostTimer.Tick += (s, e) => CheckGeometry();
        _topmostTimer.Start();

        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// The overlay must never take a click, a focus or an Alt+Tab slot: the desktop
    /// underneath stays fully usable while the snow is drawn over it.
    /// </summary>
    private static void MakeClickThroughAndTopmost(IntPtr hwnd)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        RaiseOverlay(hwnd);
    }

    private static void RaiseOverlay(IntPtr hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void RaiseOverlay()
    {
        if (_hwnd != IntPtr.Zero)
        {
            RaiseOverlay(_hwnd);
        }
    }

    private void MeasureMonitor()
    {
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var gotMonitor = monitor != IntPtr.Zero
            && NativeMethods.GetMonitorInfoW(monitor, ref info)
            && info.rcMonitor.Right > info.rcMonitor.Left
            && info.rcMonitor.Bottom > info.rcMonitor.Top;

        var dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        _dipPerPixel = 96.0 / dpi;

        if (gotMonitor)
        {
            _monitorLeft = info.rcMonitor.Left;
            _monitorTop = info.rcMonitor.Top;
            _monitorWidth = info.rcMonitor.Right - info.rcMonitor.Left;
            _monitorHeight = info.rcMonitor.Bottom - info.rcMonitor.Top;

            if (info.rcWork.Right - info.rcWork.Left > 0
                && info.rcWork.Bottom > info.rcWork.Top)
            {
                _workLeft = (info.rcWork.Left - info.rcMonitor.Left) * _dipPerPixel;
                _workTop = (info.rcWork.Top - info.rcMonitor.Top) * _dipPerPixel;
                _workWidth = (info.rcWork.Right - info.rcWork.Left) * _dipPerPixel;
                _workHeight = (info.rcWork.Bottom - info.rcWork.Top) * _dipPerPixel;
            }
            else
            {
                _workLeft = 0;
                _workTop = 0;
                _workWidth = _monitorWidth * _dipPerPixel;
                _workHeight = _monitorHeight * _dipPerPixel;
            }
            _workBottom = _workTop + _workHeight;
            return;
        }

        // Fallback: the primary display, in physical pixels.
        _monitorLeft = 0;
        _monitorTop = 0;
        _monitorWidth = (int)Math.Round(SystemParameters.PrimaryScreenWidth / _dipPerPixel);
        _monitorHeight = (int)Math.Round(SystemParameters.PrimaryScreenHeight / _dipPerPixel);
        _workLeft = 0;
        _workTop = 0;
        _workWidth = _monitorWidth * _dipPerPixel;
        _workHeight = _monitorHeight * _dipPerPixel;
        _workBottom = _monitorHeight * _dipPerPixel;
    }

    private void ApplyOverlayGeometry()
    {
        if (_hwnd == IntPtr.Zero || _monitorWidth <= 0 || _monitorHeight <= 0)
        {
            return;
        }

        // WPF sizes transparent windows in DIPs; the monitor is measured in physical
        // pixels. Keep both in sync, then force the exact physical rectangle so DPI
        // rounding can't leave an uncovered strip along an edge.
        Left = _monitorLeft * _dipPerPixel;
        Top = _monitorTop * _dipPerPixel;
        Width = _monitorWidth * _dipPerPixel;
        Height = _monitorHeight * _dipPerPixel;

        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            _monitorLeft,
            _monitorTop,
            _monitorWidth,
            _monitorHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Re-checks the monitor and work area once a second so a moved, re-docked or
    /// auto-hiding taskbar moves the ground the snow settles on.
    /// </summary>
    private void CheckGeometry()
    {
        int oldMonitorWidth = _monitorWidth;
        int oldMonitorHeight = _monitorHeight;
        double oldWorkBottom = _workBottom;

        MeasureMonitor();

        if (_monitorWidth != oldMonitorWidth || _monitorHeight != oldMonitorHeight)
        {
            ApplyOverlayGeometry();
            Engine.SetScreen(0, _monitorHeight * _dipPerPixel);
            Engine.Width = _monitorWidth * _dipPerPixel;
        }

        Engine.SetGround(_workLeft, _workBottom, _workLeft + _workWidth);
        RaiseOverlay();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _topmostTimer?.Stop();
        _topmostTimer = null;
        _tracker?.Stop();
        _tracker = null;
        Engine.Clear();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (Engine.TargetFps > 0)
        {
            var intervalMs = Math.Max(4, (int)Math.Ceiling(1000.0 / Engine.TargetFps));
            if ((now - _lastRender).TotalMilliseconds < intervalMs) return;
        }

        var dt = (now - _lastRender).TotalSeconds;
        _lastRender = now;
        if (dt < 0) dt = 0;
        if (dt > 0.05) dt = 0.05;

        // Re-read the windows every frame so a dragged window carries its snow with
        // it, then let the engine bind the drifts to the fresh rectangles.
        _tracker?.Poll();
        Engine.SyncWindows(_tracker?.Windows ?? Array.Empty<WindowRect>(), dt);
        Engine.Update(dt);
        SprayAtCursor(dt);
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// The overlay is click-through, so it gets no mouse messages. The cursor is read
    /// from the system instead, which lets the snow part anywhere on screen - over
    /// windows, the taskbar and the desktop alike.
    /// </summary>
    private void SprayAtCursor(double dt)
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        double x = (point.X - _monitorLeft) * _dipPerPixel;
        double y = (point.Y - _monitorTop) * _dipPerPixel;

        if (double.IsNaN(_lastCursorX))
        {
            _lastCursorX = x;
            _lastCursorY = y;
            return;
        }

        double dx = x - _lastCursorX;
        double dy = y - _lastCursorY;
        _lastCursorX = x;
        _lastCursorY = y;

        if (x < 0 || y < 0 || x > Engine.Width || y > Engine.Bottom) return;
        if (Math.Abs(dx) + Math.Abs(dy) < 0.5) return; // the pointer is resting

        double seconds = Math.Max(dt, 0.001);
        double vx = Math.Clamp(dx / seconds, -4000, 4000);
        double vy = Math.Clamp(dy / seconds, -4000, 4000);

        Engine.Spray(x, y, vx, vy);
    }
}
