using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Aerisflow.Native;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace Aerisflow;

public sealed class GpuSnowOverlay : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private readonly SnowEngine _engine;
    private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
    private Thread? _thread;

    private NativeMethods.WndProcDelegate? _wndProc;
    private IntPtr _hwnd;
    private IntPtr _instance;
    private string? _className;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1Bitmap1? _targetBitmap;
    private IDCompositionDevice? _composition;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private ID2D1SolidColorBrush? _bankBrush;
    private ID2D1RadialGradientBrush? _flakeBrush;

    private WindowTracker? _tracker;

    private double _lastCursorX = double.NaN;
    private double _lastCursorY = double.NaN;

    private int _monitorLeft;
    private int _monitorTop;
    private int _monitorWidth;
    private int _monitorHeight;
    private double _dipPerPixel = 1.0;
    private double _pixelsPerDip = 1.0;

    private double _workLeft;
    private double _workTop;
    private double _workWidth;
    private double _workHeight;
    private double _workBottom;

    public event Action? Failed;

    public IntPtr Handle => _hwnd;

    private GpuSnowOverlay(SnowEngine engine)
    {
        _engine = engine;
    }

    public static GpuSnowOverlay? TryCreate(SnowEngine engine)
    {
        var overlay = new GpuSnowOverlay(engine);
        try
        {
            overlay.Initialize();
            return overlay;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GPU overlay unavailable: " + ex);
            overlay.Dispose();
            return null;
        }
    }

    private void Initialize()
    {
        NativeMethods.TimeBeginPeriod(1);

        _instance = NativeMethods.GetModuleHandle(null);
        _className = "AerisflowOverlay_" + Guid.NewGuid().ToString("N");
        _wndProc = WndProc;

        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = _className,
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED,
            _className,
            "Aerisflow",
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "AerisflowRender" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RenderLoop()
    {
        try
        {
            var dpi = NativeMethods.GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            _dipPerPixel = 96.0 / dpi;
            _pixelsPerDip = dpi / 96.0;

            MeasureMonitor();
            ApplyOverlayGeometry();
            CreateDeviceResources();
            NativeMethods.ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

            _engine.SetScreen(0, _monitorHeight * _dipPerPixel);
            _engine.Width = _monitorWidth * _dipPerPixel;
            _engine.SetGround(_workLeft, _workBottom, _workLeft + _workWidth);

            _tracker = new WindowTracker(_hwnd, _monitorLeft, _monitorTop, _dipPerPixel);
            _tracker.Start();

            OverlayNative.QueryPerformanceFrequency(out long freq);
            OverlayNative.QueryPerformanceCounter(out long last);
            long lastGeometry = last;
            long lastRaise = last;
            long lastFullscreen = last;
            double geometryInterval = freq;
            double raiseInterval = freq * 0.2;
            double fullscreenInterval = freq * 0.2;

            while (!_stop.IsSet)
            {
                PumpMessages();

                OverlayNative.QueryPerformanceCounter(out long now);
                double frameDt = (double)(now - last) / freq;
                last = now;

                if (now - lastGeometry >= geometryInterval)
                {
                    lastGeometry = now;
                    CheckGeometry();
                }

                if (now - lastRaise >= raiseInterval)
                {
                    lastRaise = now;
                    RaiseOverlay();
                }

                if (now - lastFullscreen >= fullscreenInterval)
                {
                    lastFullscreen = now;
                    UpdateFullscreenState();
                }

                if (frameDt > 0)
                {
                    double stepDt = frameDt;
                    if (stepDt > 0.05) stepDt = 0.05;
                    _tracker?.Poll();
                    _engine.Advance(stepDt, _tracker?.Windows ?? Array.Empty<WindowRect>());
                    SprayAtCursor(stepDt);
                    RenderFrame();
                }

                int targetFps = _engine.TargetFps;
                if (targetFps > 0)
                {
                    OverlayNative.QueryPerformanceCounter(out long frameEnd);
                    double workDt   = (double)(frameEnd - now) / freq;
                    double targetDt = 1.0 / targetFps;
                    double sleepDt  = targetDt - workDt;
                    if (sleepDt > 0.001)
                    {
                        int sleepMs = (int)Math.Round(sleepDt * 1000);
                        if (sleepMs > 0) Thread.Sleep(sleepMs);
                    }
                }
                else if (frameDt < 0.002)
                {
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("GPU overlay stopped: " + ex);
            var failed = Failed;
            if (failed != null)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(failed);
        }
        finally
        {
            DisposeResources();
        }
    }

    private void UpdateFullscreenState()
    {
        int hr = SHQueryUserNotificationState(out int state);
        _engine.IsFullscreenAppActive = hr == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
    }

    private static void PumpMessages()
    {
        while (OverlayNative.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            OverlayNative.TranslateMessage(ref msg);
            OverlayNative.DispatchMessage(ref msg);
        }
    }

    private void MeasureMonitor()
    {
        var point = new NativeMethods.POINT { X = 0, Y = 0 };
        var monitor = NativeMethods.MonitorFromPoint(point, MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (monitor != IntPtr.Zero
            && NativeMethods.GetMonitorInfoW(monitor, ref info)
            && info.rcMonitor.Right > info.rcMonitor.Left
            && info.rcMonitor.Bottom > info.rcMonitor.Top)
        {
            _monitorLeft = info.rcMonitor.Left;
            _monitorTop = info.rcMonitor.Top;
            _monitorWidth = info.rcMonitor.Right - info.rcMonitor.Left;
            _monitorHeight = info.rcMonitor.Bottom - info.rcMonitor.Top;

            if (info.rcWork.Right - info.rcWork.Left > 0
                && info.rcWork.Bottom > info.rcWork.Top)
            {
                _workLeft = (info.rcWork.Left - _monitorLeft) * _dipPerPixel;
                _workTop = (info.rcWork.Top - _monitorTop) * _dipPerPixel;
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

        _monitorLeft = 0;
        _monitorTop = 0;
        _monitorWidth = NativeMethods.GetSystemMetrics(0);
        _monitorHeight = NativeMethods.GetSystemMetrics(1);
        if (_monitorWidth <= 0) _monitorWidth = 1920;
        if (_monitorHeight <= 0) _monitorHeight = 1080;

        _workLeft = 0;
        _workTop = 0;
        _workWidth = _monitorWidth * _dipPerPixel;
        _workHeight = _monitorHeight * _dipPerPixel;
        _workBottom = _monitorHeight * _dipPerPixel;
    }

    private void CheckGeometry()
    {
        int oldMonitorWidth = _monitorWidth;
        int oldMonitorHeight = _monitorHeight;

        MeasureMonitor();

        if (_monitorWidth != oldMonitorWidth || _monitorHeight != oldMonitorHeight)
        {
            ApplyOverlayGeometry();
            _engine.SetScreen(0, _monitorHeight * _dipPerPixel);
            _engine.Width = _monitorWidth * _dipPerPixel;
        }

        _engine.SetGround(_workLeft, _workBottom, _workLeft + _workWidth);
    }

    private void ApplyOverlayGeometry()
    {
        if (_hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            _monitorLeft, _monitorTop,
            _monitorWidth, _monitorHeight,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void RaiseOverlay()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (NativeMethods.OwnWindowIsForeground(_hwnd)) return;

        IntPtr above = LowestOpenPopup();
        NativeMethods.SetWindowPos(
            _hwnd,
            above != IntPtr.Zero ? above : NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private static IntPtr LowestOpenPopup()
    {
        IntPtr taskbar = OverlayNative.FindWindow("Shell_TrayWnd", null);
        uint shellProcess = 0;
        if (taskbar != IntPtr.Zero) OverlayNative.GetWindowThreadProcessId(taskbar, out shellProcess);

        IntPtr lowest = IntPtr.Zero;
        IntPtr window = OverlayNative.GetTopWindow(IntPtr.Zero);
        while (window != IntPtr.Zero)
        {
            uint ex = (uint)OverlayNative.GetWindowLongPtr(window, -20).ToInt64();
            if ((ex & WS_EX_TOPMOST) == 0) break;

            if ((ex & WS_EX_TRANSPARENT) == 0 && OverlayNative.IsWindowVisible(window) && !IsCloaked(window))
            {
                string cls = ClassName(window);
                OverlayNative.GetWindowThreadProcessId(window, out uint process);
                bool popup = cls == "#32768"
                    || cls == "tooltips_class32"
                    || (shellProcess != 0 && process == shellProcess
                        && cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd");
                if (!popup) break;
                lowest = window;
            }

            window = OverlayNative.GetWindow(window, 2);
        }
        return lowest;
    }

    private static bool IsCloaked(IntPtr window)
    {
        int cloaked = 0;
        int hr = OverlayNative.DwmGetWindowAttribute(window, 14, ref cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static string ClassName(IntPtr window)
    {
        var sb = new StringBuilder(256);
        int n = OverlayNative.GetClassName(window, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    private void CreateDeviceResources()
    {
        D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[]
            {
                D3DFeatureLevel.Level_11_1,
                D3DFeatureLevel.Level_11_0,
                D3DFeatureLevel.Level_10_1,
                D3DFeatureLevel.Level_10_0,
            },
            out _device,
            out _deviceContext).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)_monitorWidth,
            Height = (uint)_monitorHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, description, null);

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        RebindTargetBitmap();

        _bankBrush = _d2dContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));

        var stops = new[]
        {
            new Vortice.Direct2D1.GradientStop(0f, new Color4(1f, 1f, 1f, 1f)),
            new Vortice.Direct2D1.GradientStop(0.55f, new Color4(1f, 1f, 1f, 0.7f)),
            new Vortice.Direct2D1.GradientStop(1f, new Color4(1f, 1f, 1f, 0f)),
        };
        using var stopCollection = _d2dContext.CreateGradientStopCollection(stops);
        _flakeBrush = _d2dContext.CreateRadialGradientBrush(
            new RadialGradientBrushProperties(new Vector2(0.5f, 0.5f), Vector2.Zero, 0.5f, 0.5f),
            stopCollection);

        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _composition.CreateTargetForHwnd(_hwnd, true, out _compositionTarget).CheckError();
        _composition.CreateVisual(out _compositionVisual).CheckError();
        _compositionVisual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        _composition.Commit();
    }

    private void RebindTargetBitmap()
    {
        if (_d2dContext == null || _swapChain == null) return;

        _d2dContext.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;

        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, properties);
        _d2dContext.Target = _targetBitmap;
    }

    private void RenderFrame()
    {
        var context = _d2dContext;
        var swapChain = _swapChain;
        var bankBrush = _bankBrush;
        var flakeBrush = _flakeBrush;
        if (context == null || swapChain == null || bankBrush == null || flakeBrush == null) return;

        double opacity = Math.Clamp(_engine.Opacity, 0, 1);
        double alpha = _engine.RenderAlpha;

        bool hideAll = _engine.IsFullscreenAppActive &&
                       _engine.FullscreenMode == FullscreenOverlayMode.On;
        bool hideDriftsOnly = _engine.IsFullscreenAppActive &&
                              _engine.FullscreenMode == FullscreenOverlayMode.Off;

        context.BeginDraw();
        context.Clear(new Color4(0f, 0f, 0f, 0f));

        if (hideAll)
        {
            var empty = context.EndDraw();
            if (empty.Failure) throw new InvalidOperationException("D2D EndDraw failed: " + empty);
            swapChain.Present(1, PresentFlags.None);
            return;
        }

        context.Transform = Matrix3x2.CreateScale((float)_pixelsPerDip);

        if (!hideDriftsOnly)
        {
            bankBrush.Opacity = (float)(0.96 * opacity);
            var drifts = _engine.Drifts;
            for (int i = 0; i < drifts.Count; i++)
            {
                var drift = drifts[i];
                if (!drift.HasSnow) continue;
                DrawDrift(context, drift, bankBrush, alpha);
            }
        }

        var flakes = _engine.Flakes;
        for (int i = 0; i < flakes.Count; i++)
            DrawFlake(context, flakes[i], flakeBrush, opacity, alpha);

        context.Transform = Matrix3x2.Identity;
        var drawResult = context.EndDraw();
        if (drawResult.Failure)
            throw new InvalidOperationException("Direct2D EndDraw failed: " + drawResult);

        swapChain.Present(1, PresentFlags.None);
    }

    private void DrawDrift(ID2D1DeviceContext context, SnowDrift drift, ID2D1SolidColorBrush brush, double alpha)
    {
        int n = drift.Heights.Length;
        if (n == 0) return;

        double left = drift.RenderLeft(alpha);
        double top = drift.RenderTop(alpha);
        double width = drift.Right - drift.Left;

        var visible = drift.Visible;
        for (int s = 0; s < visible.Count; s++)
        {
            double from = Math.Max(0, visible[s].Left);
            double to = Math.Min(width, visible[s].Right);
            if (to - from < 0.5) continue;
            DrawBank(context, drift, brush, left, top, from, to);
        }
    }

    private void DrawBank(
        ID2D1DeviceContext context,
        SnowDrift drift,
        ID2D1SolidColorBrush brush,
        double left,
        double top,
        double from,
        double to)
    {
        int n = drift.Heights.Length;
        if (n == 0 || _d2dFactory == null) return;

        double Height(int i)
        {
            if (i < 0) i = 0;
            if (i >= n) i = n - 1;
            double middle = drift.Heights[i];
            double before = drift.Heights[i > 0 ? i - 1 : i];
            double after = drift.Heights[i < n - 1 ? i + 1 : i];
            return before * 0.25 + middle * 0.5 + after * 0.25;
        }

        int first = (int)Math.Ceiling(from / SnowDrift.ColumnWidth - 0.5);
        int last = (int)Math.Floor(to / SnowDrift.ColumnWidth - 0.5);
        if (first < 0) first = 0;
        if (last >= n) last = n - 1;
        if (first > last) return;

        const double threshold = 0.035;
        using var geometry = _d2dFactory.CreatePathGeometry();
        using var sink = geometry.Open();
        bool any = false;

        int i = first;
        while (i <= last)
        {
            while (i <= last && Height(i) <= threshold) i++;
            if (i > last) break;

            int start = i;
            while (i + 1 <= last && Height(i + 1) > threshold) i++;
            int end = i;

            double xStart = left + Math.Max(from, start * SnowDrift.ColumnWidth);
            double xEnd = left + Math.Min(to, (end + 1) * SnowDrift.ColumnWidth);
            double baseY = top + 1.0;

            sink.BeginFigure(new Vector2((float)xStart, (float)baseY), FigureBegin.Filled);
            sink.AddLine(new Vector2(
                (float)(left + (start + 0.5) * SnowDrift.ColumnWidth),
                (float)(top - Height(start))));
            for (int k = start + 1; k <= end; k++)
                sink.AddLine(new Vector2(
                    (float)(left + (k + 0.5) * SnowDrift.ColumnWidth),
                    (float)(top - Height(k))));
            sink.AddLine(new Vector2((float)xEnd, (float)baseY));
            sink.EndFigure(FigureEnd.Closed);
            any = true;
            i++;
        }

        sink.Close();
        if (any) context.FillGeometry(geometry, brush);
    }

    private void DrawFlake(
        ID2D1DeviceContext context,
        Flake f,
        ID2D1RadialGradientBrush brush,
        double opacity,
        double alpha)
    {
        double a = Math.Clamp(f.Alpha * 0.85 * opacity, 0, 1);
        if (a <= 0.01) return;

        double x = f.PrevX + (f.X - f.PrevX) * alpha;
        double y = f.PrevY + (f.Y - f.PrevY) * alpha;

        double stretch = f.Lump ? 1.0 : 1.0 + Math.Min(0.8, Math.Max(0, f.VY) / 240.0 * 0.55);
        float rx = (float)f.R;
        float ry = (float)(f.R * stretch);
        if (rx < 0.3f) rx = 0.3f;
        if (ry < 0.3f) ry = 0.3f;

        brush.Opacity = (float)a;
        brush.Transform =
            Matrix3x2.CreateScale(rx * 2f, ry * 2f)
            * Matrix3x2.CreateTranslation((float)x - rx, (float)y - ry);

        context.FillEllipse(new Ellipse(new Vector2((float)x, (float)y), rx, ry), brush);
    }

    private void SprayAtCursor(double dt)
    {
        if (!NativeMethods.GetCursorPos(out var point)) return;

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

        if (x < 0 || y < 0 || x > _engine.Width || y > _engine.Bottom)
        {
            _lastCursorX = x;
            _lastCursorY = y;
            return;
        }

        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist < 0.5)
        {
            _engine.Spray(x, y, 10, 0);
            _lastCursorX = x;
            _lastCursorY = y;
            return;
        }

        int steps = (int)Math.Ceiling(dist / 8.0);
        if (steps < 1) steps = 1;
        if (steps > 20) steps = 20;

        double seconds = Math.Max(dt, 0.008);
        double vx = Math.Clamp(dx / seconds, -800, 800);
        double vy = Math.Clamp(dy / seconds, -800, 800);

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double px = _lastCursorX + dx * t;
            double py = _lastCursorY + dy * t;
            _engine.Spray(px, py, vx, vy);
        }

        _lastCursorX = x;
        _lastCursorY = y;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void DisposeResources()
    {
        _tracker?.Stop();
        _tracker = null;

        try { _composition?.Commit(); } catch { }

        _compositionTarget?.Dispose(); _compositionTarget = null;
        _compositionVisual?.Dispose(); _compositionVisual = null;
        _composition?.Dispose(); _composition = null;
        _flakeBrush?.Dispose(); _flakeBrush = null;
        _bankBrush?.Dispose(); _bankBrush = null;
        _targetBitmap?.Dispose(); _targetBitmap = null;
        _d2dContext?.Dispose(); _d2dContext = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        _swapChain?.Dispose(); _swapChain = null;
        _deviceContext?.Dispose(); _deviceContext = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread != null && _thread.IsAlive) _thread.Join(3000);
        _thread = null;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_className != null && _instance != IntPtr.Zero)
        {
            NativeMethods.UnregisterClass(_className, _instance);
            _className = null;
        }

        NativeMethods.TimeEndPeriod(1);
        _stop.Dispose();
    }
}

internal static class OverlayNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll")]
    public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    public static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}