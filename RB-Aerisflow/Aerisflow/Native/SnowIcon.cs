using System.Drawing;

namespace Aerisflow.Native;

/// <summary>Creates a small snow icon for the tray.</summary>
public static class SnowIcon
{
    public static Icon Create()
    {
        int size = 32;
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            var bg = new SolidBrush(System.Drawing.Color.FromArgb(70, 130, 200));
            g.FillEllipse(bg, 1, 1, size - 2, size - 2);
            var white = new SolidBrush(System.Drawing.Color.White);
            g.FillEllipse(white, size / 2 - 2, size / 2 - 2, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
