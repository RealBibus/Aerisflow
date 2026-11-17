using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Threading;
using Aerisflow.Native;

namespace Aerisflow;

public sealed class WindowTracker
{
    private sealed class TrackedWindow
    {
        public IntPtr Hwnd;
        public string ClassName = string.Empty;
        public int InsetLeft;
        public int InsetTop;
        public int InsetRight;
        public int InsetBottom;
    }

    private static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "tooltips_class32",
        "SysShadow",
        "EdgeUiInputTopWndClass",
        "EdgeUiInputWndClass",
        "ApplicationManager_ImmersiveShellWindow",
        "Internet Explorer_Hidden",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "ForegroundStaging",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "Xaml_WindowedPopupClass",
        "Windows.UI.Core.CoreWindow",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private readonly IntPtr _overlayHwnd;
    private readonly int _monitorLeft;
    private readonly int _monitorTop;
    private readonly double _dipPerPixel;
    private readonly List<TrackedWindow> _tracked = new List<TrackedWindow>();
    private readonly List<WindowRect> _windows = new List<WindowRect>();
    private readonly DispatcherTimer _fullTimer;

    public IReadOnlyList<WindowRect> Windows => _windows;

    public WindowTracker(IntPtr overlayHwnd, int monitorLeft, int monitorTop, double dipPerPixel)
    {
        _overlayHwnd = overlayHwnd;
        _monitorLeft = monitorLeft;
        _monitorTop = monitorTop;
        _dipPerPixel = dipPerPixel;

        _fullTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _fullTimer.Tick += (s, e) => Refresh();
        _fullTimer.Start();
        Refresh();
    }

    public void Start()
    {
        Refresh();
    }

    public void Stop()
    {
        _fullTimer.Stop();
    }

    public void Poll()
    {
        _windows.Clear();
        foreach (var tracked in _tracked)
        {
            if (!NativeMethods.IsWindowVisible(tracked.Hwnd)) continue;
            if (NativeMethods.IsIconic(tracked.Hwnd)) continue;
            if (!NativeMethods.GetWindowRect(tracked.Hwnd, out var r)) continue;

            r.Left += tracked.InsetLeft;
            r.Top += tracked.InsetTop;
            r.Right += tracked.InsetRight;
            r.Bottom += tracked.InsetBottom;
            AddWindow(tracked, r);
        }
    }

    public void Refresh()
    {
        var found = new List<TrackedWindow>();
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            var tracked = Describe(hwnd);
            if (tracked != null) found.Add(tracked);
            return true;
        }, IntPtr.Zero);

        _tracked.Clear();
        _tracked.AddRange(found);
        Poll();
    }

    private void AddWindow(TrackedWindow tracked, NativeMethods.RECT r)
    {
        if (r.Right - r.Left < 2 || r.Bottom - r.Top < 2) return;
        if (r.Bottom <= -30000 || r.Right <= -30000) return;

        _windows.Add(new WindowRect
        {
            Handle = tracked.Hwnd,
            ClassName = tracked.ClassName,
            Left = (r.Left - _monitorLeft) * _dipPerPixel,
            Top = (r.Top - _monitorTop) * _dipPerPixel,
            Right = (r.Right - _monitorLeft) * _dipPerPixel,
            Bottom = (r.Bottom - _monitorTop) * _dipPerPixel,
        });
    }

    private TrackedWindow? Describe(IntPtr hwnd)
    {
        if (hwnd == _overlayHwnd) return null;
        if (!NativeMethods.IsWindowVisible(hwnd)) return null;
        if (NativeMethods.IsIconic(hwnd)) return null;

        if (NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0)
        {
            return null;
        }

        var className = ClassName(hwnd);
        if (IgnoredClasses.Contains(className)) return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var plain)) return null;

        int insetLeft = 0, insetTop = 0, insetRight = 0, insetBottom = 0;
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            insetLeft = frame.Left - plain.Left;
            insetTop = frame.Top - plain.Top;
            insetRight = frame.Right - plain.Right;
            insetBottom = frame.Bottom - plain.Bottom;
        }

        var visible = plain;
        visible.Left += insetLeft;
        visible.Top += insetTop;
        visible.Right += insetRight;
        visible.Bottom += insetBottom;
        if (visible.Right - visible.Left < 2 || visible.Bottom - visible.Top < 2) return null;

        return new TrackedWindow
        {
            Hwnd = hwnd,
            ClassName = className,
            InsetLeft = insetLeft,
            InsetTop = insetTop,
            InsetRight = insetRight,
            InsetBottom = insetBottom,
        };
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }
}