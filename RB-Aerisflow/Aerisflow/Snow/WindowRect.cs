using System;

namespace Aerisflow;

/// <summary>Visible bounds of a top-level window, in the overlay's DIP coordinates,
/// together with its handle so snow can stay bound to it as it moves.</summary>
public sealed class WindowRect
{
    public IntPtr Handle;
    public string ClassName = string.Empty;
    public double Left;
    public double Top;
    public double Right;
    public double Bottom;

    public double Width => Right - Left;
    public double Height => Bottom - Top;
}
