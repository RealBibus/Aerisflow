using System;
using System.Runtime.InteropServices;

namespace Aerisflow.Installer;

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

    public static void ApplyAcrylic(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var policy = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = unchecked((int)0x66141418),
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
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
        catch { }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void ApplyModernChrome(IntPtr hwnd)
    {
        SetInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        SetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, 2);
        SetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, 3);
    }

    private static void SetInt(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch { }
    }
}
