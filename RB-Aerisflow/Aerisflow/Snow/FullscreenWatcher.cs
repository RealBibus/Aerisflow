using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Aerisflow;

public sealed class FullscreenWatcher
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint GW_HWNDNEXT = 2;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_BLOCK = WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    private static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "ForegroundStaging",
        "XamlExplorerHostIslandWindow", "ApplicationManager_ImmersiveShellWindow",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "Xaml_WindowedPopupClass", "tooltips_class32", "#32768",
        "Shell_InputSwitchTopLevelWindow", "Windows.UI.Composition.DesktopWindowContentBridge",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameHost", "ApplicationFrameWindow",
        "Windows.Internal.Shell.TabProxyWindow", "DesktopWindowXamlSource",
        "IME", "MSCTFIME UI", "Default IME", "CicMarshalWndClass", "PopupHost",
    };

    private static readonly HashSet<string> DesktopClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW",
    };

    private int _left, _top, _right, _bottom;
    private uint _shellProcess;
    private bool _last;
    private IntPtr _lastBlocker;
    private long _overrideAt;
    private bool _override;

    public string LastDetection { get; private set; } = "";

    public void SetMonitor(int left, int top, int width, int height)
    {
        _left = left;
        _top = top;
        _right = left + width;
        _bottom = top + height;
    }

    public bool Check(IntPtr own)
    {
        long now = Environment.TickCount64;
        if (now - _overrideAt > 5000)
        {
            _overrideAt = now;
            try
            {
                _override = File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aerisflow", "fullscreen.off"));
            }
            catch { }
        }
        if (_override)
        {
            Report(false, IntPtr.Zero, "override file");
            return false;
        }

        if (_shellProcess == 0)
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero) GetWindowThreadProcessId(taskbar, out _shellProcess);
        }

        bool active = false;
        IntPtr blocker = IntPtr.Zero;
        string reason = "desktop";

        if (SHQueryUserNotificationState(out int state) == 0 &&
        (state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE))
        {
            active = true;
            reason = "QUNS " + state;
        }
        else if (_right > _left && _bottom > _top)
        {
            IntPtr foreground = GetForegroundWindow();
            if (IsCandidate(foreground, own) && CoversMonitor(foreground))
            {
                active = true;
                blocker = foreground;
                reason = "foreground";
            }
            else
            {
                IntPtr window = GetTopWindow(IntPtr.Zero);
                int guard = 0;
                while (window != IntPtr.Zero && guard++ < 64)
                {
                    if (!IsWindowVisible(window))
                    {
                        window = GetWindow(window, GW_HWNDNEXT);
                        continue;
                    }
                    if (DesktopClasses.Contains(ClassName(window))) break;
                    long ex = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
                    if ((ex & WS_EX_TOPMOST) == 0 && IsCandidate(window, own))
                    {
                        if (CoversMonitor(window))
                        {
                            active = true;
                            blocker = window;
                            reason = "z-order";
                        }
                        break;
                    }
                    window = GetWindow(window, GW_HWNDNEXT);
                }
            }
        }

        Report(active, blocker, reason);
        return active;
    }

    private bool IsCandidate(IntPtr window, IntPtr own)
    {
        if (window == IntPtr.Zero || window == own) return false;
        if (!IsWindowVisible(window) || IsIconic(window)) return false;
        long ex = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_BLOCK) != 0) return false;
        if (Cloaked(window)) return false;
        if (ShellClasses.Contains(ClassName(window))) return false;
        GetWindowThreadProcessId(window, out uint process);
        if (process == 0) return false;
        if (process == (uint)Environment.ProcessId) return false;
        if (_shellProcess != 0 && process == _shellProcess) return false;
        return true;
    }

    private bool CoversMonitor(IntPtr window)
    {
        if (!Bounds(window, out var r)) return false;
        if (r.Left > _left + 2 || r.Top > _top + 2 || r.Right < _right - 2 || r.Bottom < _bottom - 2) return false;

        long style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0) return true;

        if (GetClientRect(window, out var client))
        {
            var origin = new POINT { X = 0, Y = 0 };
            if (ClientToScreen(window, ref origin))
            {
                if (Math.Abs(origin.X - _left) <= 4
                && Math.Abs(origin.Y - _top) <= 4
                && Math.Abs(origin.X + client.Right - _right) <= 4
                && Math.Abs(origin.Y + client.Bottom - _bottom) <= 4) return true;
            }
        }
        return false;
    }

    private void Report(bool active, IntPtr blocker, string reason)
    {
        if (active == _last && blocker == _lastBlocker) return;
        _last = active;
        _lastBlocker = blocker;

        string description = reason;
        if (active && blocker != IntPtr.Zero)
        {
            string title = Title(blocker);
            string cls = ClassName(blocker);
            string process = "?";
            try
            {
                GetWindowThreadProcessId(blocker, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                process = p.ProcessName;
            }
            catch { }
            Bounds(blocker, out var r);
            description = $"{reason}: {process} [{cls}] \"{title}\" {r.Left},{r.Top},{r.Right},{r.Bottom}";
        }
        LastDetection = (active ? "FULLSCREEN " : "desktop ") + description;
        Debug.WriteLine("Aerisflow fullscreen: " + LastDetection);

        try
        {
            string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aerisflow");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "fullscreen.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + LastDetection + Environment.NewLine);
        }
        catch { }
    }

    private static bool Bounds(IntPtr window, out RECT rect)
    {
        rect = new RECT();
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0
        && frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            rect = frame;
            return true;
        }
        return GetWindowRect(window, out rect);
    }

    private static bool Cloaked(IntPtr window)
    => DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string ClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(window, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    private static string Title(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }
}