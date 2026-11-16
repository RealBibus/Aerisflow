using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;

namespace Aerisflow;

public class SnowCanvas : FrameworkElement
{
    public SnowEngine? Engine { get; set; }

    private readonly Dictionary<byte, Brush> _flakeBrushes = new Dictionary<byte, Brush>();
    private readonly Dictionary<byte, Brush> _driftBrushes = new Dictionary<byte, Brush>();

    protected override void OnRender(DrawingContext dc)
    {
        var engine = Engine;
        if (engine == null) return;

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        var drifts = engine.Drifts;
        for (int i = 0; i < drifts.Count; i++)
        {
            var drift = drifts[i];
            if (!drift.HasSnow) continue;
            DrawDrift(dc, drift, engine.Opacity);
        }

        var flakes = engine.Flakes;
        for (int i = 0; i < flakes.Count; i++)
        {
            DrawFlake(dc, flakes[i], engine.Opacity);
        }
    }

    private void DrawDrift(DrawingContext dc, SnowDrift drift, double opacity)
    {
        double a = Math.Max(0, Math.Min(1, 0.96 * opacity));
        if (a <= 0.01) return;
        byte key = (byte)(a * 255);
        if (!_driftBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(key, 255, 255, 255));
            brush.Freeze();
            _driftBrushes[key] = brush;
        }

        int n = drift.Heights.Length;
        if (n == 0) return;

        double left = drift.SurfaceLeft;
        double top = drift.SurfaceTop;
        double width = drift.Right - drift.Left;

        foreach (var (visibleLeft, visibleRight) in drift.Visible)
        {
            double from = Math.Max(0, visibleLeft);
            double to = Math.Min(width, visibleRight);
            if (to - from < 0.5) continue;
            DrawBank(dc, drift, brush, left, top, from, to);
        }
    }

    private static void DrawBank(
        DrawingContext dc,
        SnowDrift drift,
        Brush brush,
        double left,
        double top,
        double from,
        double to)
    {
        int n = drift.Heights.Length;
        if (n == 0) return;

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
        var geometry = new StreamGeometry();
        bool any = false;
        using (var ctx = geometry.Open())
        {
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

                ctx.BeginFigure(new Point(xStart, baseY), true, true);
                ctx.LineTo(new Point(left + (start + 0.5) * SnowDrift.ColumnWidth, top - Height(start)), true, false);
                for (int k = start + 1; k <= end; k++)
                {
                    ctx.LineTo(new Point(left + (k + 0.5) * SnowDrift.ColumnWidth, top - Height(k)), true, false);
                }
                ctx.LineTo(new Point(xEnd, baseY), true, false);
                any = true;
                i++;
            }
        }

        if (!any) return;
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private void DrawFlake(DrawingContext dc, Flake f, double opacity)
    {
        double a = Math.Max(0, Math.Min(1, f.Alpha * 0.85 * opacity));
        if (a <= 0.01) return;

        byte key = (byte)Math.Round(a * 255);
        if (!_flakeBrushes.TryGetValue(key, out var brush))
        {
            var gradient = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(key, 255, 255, 255), 0.0));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(key * 0.7), 250, 252, 255), 0.55));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
            gradient.Freeze();
            brush = gradient;
            _flakeBrushes[key] = brush;
        }

        double stretch = 1.0 + Math.Min(0.8, Math.Max(0, f.VY) / 240.0 * 0.55);
        double rx = f.R;
        double ry = f.R * stretch;
        dc.DrawEllipse(brush, null, new Point(f.X, f.Y), rx, ry);
    }
}