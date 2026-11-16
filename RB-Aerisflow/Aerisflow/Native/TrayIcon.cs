using System;
using System.Windows.Forms;
using Aerisflow;

namespace Aerisflow.Native;

/// <summary>
/// Owns the shell's notification icon. Clicking it asks the app to open the custom
/// flyout; there is deliberately no WinForms context menu any more, so the whole
/// tray UI is the WPF card and can be unloaded when it closes.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private NotifyIcon? _notifyIcon;

    public Action? OnOpenMenu;

    public void Start(Icon icon)
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Aerisflow",
            Visible = true,
        };

        _notifyIcon.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                OnOpenMenu?.Invoke();
            }
        };
    }

    public void Shutdown()
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
