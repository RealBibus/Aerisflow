using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Aerisflow.Native;

public static class SnowIcon
{
    public static Icon Create()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Aerisflow.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }

        using var bmp = DrawFlake(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Bitmap DrawFlake(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(255, 27, 42, 74));
        g.FillEllipse(bg, 0, 0, size - 1, size - 1);

        using var pen = new Pen(Color.White, Math.Max(1f, size * 0.085f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        double c = size / 2.0;
        double R = size * 0.36;
        for (int k = 0; k < 6; k++)
        {
            double a = k * Math.PI / 3;
            g.DrawLine(pen, (float)c, (float)c, (float)(c + R * Math.Cos(a)), (float)(c + R * Math.Sin(a)));
            foreach (double t in new[] { 0.6, 0.85 })
            {
                double bx = c + R * t * Math.Cos(a);
                double by = c + R * t * Math.Sin(a);
                double len = R * 0.3;
                foreach (double s in new[] { -0.6, 0.6 })
                {
                    g.DrawLine(pen, (float)bx, (float)by,
                        (float)(bx + len * Math.Cos(a + s)), (float)(by + len * Math.Sin(a + s)));
                }
            }
        }

        using var wb = new SolidBrush(Color.White);
        float cr = size * 0.08f;
        g.FillEllipse(wb, (float)(c - cr), (float)(c - cr), cr * 2, cr * 2);
        return bmp;
    }
}