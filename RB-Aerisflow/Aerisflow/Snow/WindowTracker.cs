using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Threading;
using Aerisflow.Native;

namespace Aerisflow;

/// <summary>
/// Finds the top-level windows that can hold snow and keeps their visible frames
/// up to date. A full enumeration runs a few times a second to see windows opening
/// and closing; between them the known handles are re-read every frame so a window
/// being dragged moves its snow smoothly instead of in 150 ms steps.
/// </summary>
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

    // Windows that aren't real surfaces: the desktop/wallpaper, shell flyouts,
    // tooltips and shadows. Taskbars are deliberately excluded too - snow settles
    // on the taskbar's top edge through the work area instead, and a vertical or
    // auto-hiding taskbar can sit anywhere.
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

    /// <param name="overlayHwnd">The overlay itself, which must not count as a window.</param>
    /// <param name="monitorLeft">Left edge of the overlay's display, in physical pixels.</param>
    /// <param name="monitorTop">Top edge of the overlay's display, in physical pixels.</param>
    /// <param name="dipPerPixel">Overlay DIPs per physical pixel of that display (96 / DPI).</param>
    public WindowTracker(IntPtr overlayHwnd, int monitorLeft, int monitorTop, double dipPerPixel)
    {
        _overlayHwnd = overlayHwnd;
        _monitorLeft = monitorLeft;
        _monitorTop = monitorTop;
        _dipPerPixel = dipPerPixel;

        _fullTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
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

    /// <summary>
    /// Re-reads the bounds of the windows already known. Cheap enough for every frame;
    /// a window dragged around moves its snow without waiting for the next full scan.
    /// </summary>
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

    /// <summary>Full enumeration: new and closed windows, z-order, and fresh frame insets.</summary>
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
        // Minimized windows sit far off-screen; don't let stale rects catch snow.
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

        // Windows on another virtual desktop, or UWP shells not on screen, are invisible.
        if (NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        // Click-through windows are overlays like ours, not surfaces snow can rest on.
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0)
        {
            return null;
        }

        var className = ClassName(hwnd);
        if (IgnoredClasses.Contains(className)) return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var plain)) return null;

        // GetWindowRect includes the invisible resize borders Windows 10/11 draw
        // around windows. Cache the difference to DWM's visible frame once, then
        // apply it to every cheap per-frame GetWindowRect.
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
