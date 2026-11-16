using System;
using System.Runtime.InteropServices;

namespace Aerisflow.Native;

/// <summary>
/// Windows 11 / 10 window dressing: an acrylic backdrop for the tray flyout,
/// dark immersive mode and rounded DWM corners. Every call is best-effort so an
/// older build of Windows simply gets the plain rounded card instead.
/// </summary>
internal static class WindowEffects
{
    private const int WCA_ACCENT_POLICY = 19;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Blurs whatever is behind the flyout and lays a smoky tint over it.</summary>
    public static void ApplyAcrylic(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // GradientColor is AABBGGRR: a soft, almost-black charcoal.
        var policy = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = unchecked((int)0xC01E1B1B),
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            // Not available on this build; the translucent card is the fallback.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Asks DWM for a transient (acrylic/blur) backdrop behind the flyout.</summary>
    public static void TrySystemBackdrop(IntPtr hwnd)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE = 3 (DWMSBT_TRANSIENTWINDOW) gives the flyout
        // the acrylic-style backdrop the taskbar and alt-tab surfaces use.
        SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, 3);
    }

    public static void UseDarkMode(IntPtr hwnd)
    {
        SetInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
    }

    public static void UseRoundCorners(IntPtr hwnd)
    {
        SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, 2); // DWMWCP_ROUND
    }

    private static void SetInt(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows builds ignore attributes they don't know.
        }
    }
}
