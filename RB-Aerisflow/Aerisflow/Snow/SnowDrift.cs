using System;
using System.Collections.Generic;

namespace Aerisflow;

public sealed class SnowDrift
{
    public const double ColumnWidth = 2.5;
    public const double MaxSlope    = 0.8;
    public const double Rounding    = 0.15;
    public const double MaxHeight   = 40.0;
    public const double LedgeDepth  = 14.0;

    private const double SlideStep  = ColumnWidth;
    private const double Gravity    = 2500.0;
    private const double Adhesion   = 3500.0;
    private const double Grip       = 1.2;
    private const double Slip       = 0.9;
    private const double BreakLift  = 10.0;
    private const double TrackTime  = 0.06;
    private const double Shaking    = 4.0;

    private const double MaxVisualOffsetX = 4.0;
    private const double TrackSnapDistance = 120.0;

    private readonly List<(double Left, double Right)> _visible = new List<(double Left, double Right)>();
    private readonly List<SnowChunk> _chunks = new List<SnowChunk>();
    private double[] _capacity = Array.Empty<double>();
    private readonly double _phase0;
    private readonly double _phase1;

    private static readonly double[] Spread = { 0.0625, 0.25, 0.375, 0.25, 0.0625 };

    private double _trackX;
    private double _trackY;
    private double _trackVX;
    private double _trackVY;
    private double _offsetVX;
    private double _liftVY;
    private double _swingPeak;
    private double _previousSwingPeak;
    private double _swingDirX;
    private double _swingDirY;
    private double _spillLeft;
    private double _spillRight;
    private bool   _brokeOff;
    private double _breakVX;
    private double _breakVY;

    public IntPtr Owner { get; }
    public string ClassName { get; }
    public double Born;

    public double Left  { get; private set; }
    public double Top   { get; private set; }
    public double Right { get; private set; }
    public double[] Heights { get; private set; }

    public double Depth { get; }

    public double OffsetX { get; private set; }
    public double LiftY   { get; private set; }
    public double PrevOffsetX { get; private set; }
    public double PrevLiftY   { get; private set; }
    public double Agitation   { get; private set; }
    public bool   BrokeOff    => _brokeOff;
    public bool   Shedding    { get; set; }

    public double LagX => OffsetX;
    public double LagY => -LiftY;

    public double LastVX    { get; set; }
    public double LastVY    { get; set; }
    public double LastSpeed { get; set; }

    public double MissingFor { get; set; }

    public double SurfaceLeft  => Left + OffsetX;
    public double SurfaceTop   => Top - LiftY;
    public double SurfaceRight => SurfaceLeft + (Right - Left);

    public double RenderLeft(double alpha) => Left;
    public double RenderTop(double alpha) => Top - (PrevLiftY + (LiftY - PrevLiftY) * alpha);

    public IReadOnlyList<(double Left, double Right)> Visible => _visible;

    public SnowDrift(IntPtr owner, string className, double left, double top, double right,
                     double depth = MaxHeight, double phase0 = 0, double phase1 = 0)
    {
        Owner = owner;
        ClassName = className;
        Born = 0;
        Left = left;
        Top = top;
        Right = Math.Max(left + ColumnWidth, right);
        Depth = depth;
        _phase0 = phase0;
        _phase1 = phase1;
        var count = Math.Max(1, (int)Math.Ceiling((Right - Left) / ColumnWidth));
        Heights = new double[count];
        _trackX = left;
        _trackY = top;
        BuildCapacity();
        _visible.Add((0, Right - Left));
    }

    public void MoveTo(double left, double top, double right)
    {
        LastVX = left - Left;
        LastVY = top - Top;
        Left = left;
        Top = top;
        Resize(right);
    }

    public double TrackVX => _trackVX;
    public double TrackVY => _trackVY;

    public void SetSurface(double left, double top, double right)
    {
        Left = left;
        Top = top;
        Resize(right);
        OffsetX = 0;
        LiftY = 0;
        PrevOffsetX = 0;
        PrevLiftY = 0;
        _offsetVX = 0;
        _liftVY = 0;
        _trackX = left;
        _trackY = top;
        _trackVX = 0;
        _trackVY = 0;
        Shedding = false;
        _visible.Clear();
        _visible.Add((0, Right - Left));
    }

    public void ResetMotion(double left, double top, double right)
    {
        Left = left;
        Top = top;
        Resize(right);
        _trackX = left;
        _trackY = top;
        _trackVX = 0;
        _trackVY = 0;
        OffsetX = 0;
        LiftY = 0;
        PrevOffsetX = 0;
        PrevLiftY = 0;
        _offsetVX = 0;
        _liftVY = 0;
        Agitation = 0;
        _swingPeak = 0;
        _previousSwingPeak = 0;
        _swingDirX = 0;
        _swingDirY = 0;
        Shedding = false;
    }

    public void SetVisible(IReadOnlyList<(double Left, double Right)> segments)
    {
        _visible.Clear();
        for (int i = 0; i < segments.Count; i++)
        {
            var (left, right) = segments[i];
            if (right - left > 0.5) _visible.Add((left, right));
        }
        if (_visible.Count == 0) _visible.Add((0, 0));
    }

    public bool IsVisibleAtWorld(double worldX)
    {
        double local = worldX - SurfaceLeft;
        for (int i = 0; i < _visible.Count; i++)
        {
            if (local >= _visible[i].Left && local <= _visible[i].Right) return true;
        }
        return false;
    }

    public void Step(double dt)
    {
        _brokeOff = false;
        PrevOffsetX = OffsetX;
        PrevLiftY = LiftY;
        if (dt <= 0) return;

        if (Owner == IntPtr.Zero)
        {
            OffsetX = 0;
            LiftY = 0;
            _offsetVX = 0;
            _liftVY = 0;
            return;
        }

        double previousVX = _trackVX;
        double previousVY = _trackVY;
        _trackX = SmoothDamp(_trackX, ref _trackVX, Left, TrackTime, dt);
        _trackY = SmoothDamp(_trackY, ref _trackVY, Top, TrackTime, dt);

        bool snapped = false;
        if (Math.Abs(Left - _trackX) > TrackSnapDistance) { _trackX = Left; snapped = true; }
        if (Math.Abs(Top  - _trackY) > TrackSnapDistance) { _trackY = Top;  snapped = true; }

        double ax = snapped ? 0 : (_trackVX - previousVX) / dt;
        double ay = snapped ? 0 : (_trackVY - previousVY) / dt;

        Agitation *= Math.Exp(-dt / 0.3);
        double magnitude = Math.Sqrt(ax * ax + ay * ay);
        if (magnitude > 1 && ax * _swingDirX + ay * _swingDirY < 0)
        {
            Agitation = Math.Max(Agitation, Math.Min(_previousSwingPeak, _swingPeak) / Gravity);
            _previousSwingPeak = _swingPeak;
            _swingPeak = magnitude;
            _swingDirX = ax / magnitude;
            _swingDirY = ay / magnitude;
        }
        else
        {
            if (magnitude > _swingPeak) _swingPeak = magnitude;
            if (magnitude > 1 && _swingDirX == 0 && _swingDirY == 0)
            {
                _swingDirX = ax / magnitude;
                _swingDirY = ay / magnitude;
            }
        }

        if (LiftY > 0)
        {
            _liftVY += (ay - Gravity) * dt;
            LiftY += _liftVY * dt;
            if (LiftY <= 0)
            {
                double impact = -_liftVY;
                LiftY = 0;
                _liftVY = 0;
                if (impact > 300)
                {
                    Agitation = Math.Max(Agitation, Math.Min(6.0, impact / 800.0));
                }
            }
        }
        else if (Gravity - ay < -Adhesion)
        {
            LiftY = 0.01;
            _liftVY = 0;
        }

        double pressure = LiftY > 0 ? 0 : Math.Max(0, Gravity - ay);
        double drive = -ax;
        double slideVelocity = _offsetVX;
        if (!(slideVelocity == 0 && Math.Abs(drive) <= Grip * pressure))
        {
            double direction = slideVelocity != 0
                ? Math.Sign(slideVelocity)
                : (drive > 0 ? 1 : -1);
            double next = slideVelocity + (drive - direction * Slip * pressure) * dt;
            slideVelocity = pressure > 0 && next * direction <= 0 ? 0 : next;
        }

        OffsetX += slideVelocity * dt;
        _offsetVX = slideVelocity;

        if (OffsetX > MaxVisualOffsetX)      { OffsetX = MaxVisualOffsetX;  _offsetVX = 0; }
        else if (OffsetX < -MaxVisualOffsetX){ OffsetX = -MaxVisualOffsetX; _offsetVX = 0; }

        int guard = 0;
        while (Math.Abs(OffsetX) >= SlideStep && guard++ < 16)
        {
            int direction = OffsetX > 0 ? 1 : -1;
            SlideColumn(direction, _trackVX + slideVelocity, _trackVY - _liftVY);
            OffsetX -= direction * SlideStep;
        }

        if (LiftY <= 0 && Agitation < 1.0)
        {
            double settle = Math.Exp(-dt * 20.0);
            OffsetX *= settle;
            _offsetVX *= settle;
            if (Math.Abs(OffsetX) < 0.03)
            {
                OffsetX = 0;
                _offsetVX = 0;
            }
        }

        if (LiftY > BreakLift)
        {
            _brokeOff = true;
            _breakVX = _trackVX + _offsetVX;
            _breakVY = _trackVY - _liftVY;
            OffsetX = 0;
            LiftY = 0;
            _offsetVX = 0;
            _liftVY = 0;
        }
    }

    public void TakeChunks(List<SnowChunk> destination)
    {
        for (int i = 0; i < _chunks.Count; i++) destination.Add(_chunks[i]);
        _chunks.Clear();
    }

    public void BreakVelocity(out double vx, out double vy)
    {
        vx = _breakVX;
        vy = _breakVY;
    }

    public void ClearSnow()
    {
        Array.Clear(Heights, 0, Heights.Length);
    }

    private void SlideColumn(int direction, double vx, double vy)
    {
        int n = Heights.Length;
        if (n == 0) return;

        int index = direction > 0 ? n - 1 : 0;
        double height = Heights[index];
        double x = SurfaceLeft + (index + 0.5) * ColumnWidth;
        double y = SurfaceTop - height * 0.5;

        if (direction > 0)
        {
            for (int i = n - 1; i > 0; i--) Heights[i] = Heights[i - 1];
            Heights[0] = 0;
        }
        else
        {
            for (int i = 0; i < n - 1; i++) Heights[i] = Heights[i + 1];
            Heights[n - 1] = 0;
        }

        if (height > 0.05)
        {
            _chunks.Add(new SnowChunk(x, y, height, vx, vy));
        }
    }

    private void Resize(double right)
    {
        Right = Math.Max(Left + ColumnWidth, right);
        int count = Math.Max(1, (int)Math.Ceiling((Right - Left) / ColumnWidth));
        if (count == Heights.Length) return;

        var resized = new double[count];
        Array.Copy(Heights, resized, Math.Min(count, Heights.Length));
        Heights = resized;
        BuildCapacity();
    }

    private void BuildCapacity()
    {
        _capacity = new double[Heights.Length];
        for (int i = 0; i < _capacity.Length; i++)
        {
            double x = i * ColumnWidth;
            double drift = 0.12 * Math.Sin(x * 0.0105 + _phase0)
                + 0.07 * Math.Sin(x * 0.037 + _phase1);
            _capacity[i] = Math.Max(1.0, Depth * (0.95 + drift));
        }
    }

    public double Fill(int column)
    {
        if (column < 0 || column >= _capacity.Length) return 0;
        return Math.Max(0, 1.0 - Heights[column] / _capacity[column]);
    }

    public bool Matches(IntPtr owner) => Owner == owner;

    public double HeightAtWorld(double worldX) => HeightAt(worldX - SurfaceLeft);

    public double HeightAt(double x)
    {
        int n = Heights.Length;
        if (n == 0) return 0;
        double pos = x / ColumnWidth - 0.5;
        if (pos <= 0) return Heights[0];
        if (pos >= n - 1) return Heights[n - 1];
        int i = (int)pos;
        double t = pos - i;
        return Heights[i] * (1 - t) + Heights[i + 1] * t;
    }

    public void Add(double x, double volume)
    {
        if (volume <= 0) return;
        int n = Heights.Length;
        int centre = (int)Math.Floor(x / ColumnWidth);
        if (centre < 0 || centre >= n) return;

        double perColumn = volume * Fill(centre) / ColumnWidth;
        for (int k = 0; k < Spread.Length; k++)
        {
            int i = centre + k - 2;
            if (i < 0 || i >= n) continue;
            Heights[i] += perColumn * Spread[k];
        }
    }

    public double RemoveFraction(double fraction)
    {
        if (fraction <= 0) return 0;
        if (fraction > 1) fraction = 1;

        double removed = 0;
        for (int i = 0; i < Heights.Length; i++)
        {
            double take = Heights[i] * fraction;
            Heights[i] -= take;
            removed += take * ColumnWidth;
        }
        return removed;
    }

    public double RemoveVolume(double volume, int edgeBias)
    {
        if (volume <= 0) return 0;
        double total = Volume;
        if (total <= 0) return 0;
        if (volume >= total)
        {
            Array.Clear(Heights, 0, Heights.Length);
            return total;
        }

        if (edgeBias == 0) return RemoveFraction(volume / total);

        double removed = 0;
        int n = Heights.Length;
        if (edgeBias > 0)
        {
            for (int i = n - 1; i >= 0 && removed < volume; i--)
            {
                double take = Math.Min(Heights[i], (volume - removed) / ColumnWidth);
                Heights[i] -= take;
                removed += take * ColumnWidth;
            }
        }
        else
        {
            for (int i = 0; i < n && removed < volume; i++)
            {
                double take = Math.Min(Heights[i], (volume - removed) / ColumnWidth);
                Heights[i] -= take;
                removed += take * ColumnWidth;
            }
        }
        return removed;
    }

    public double Shave(double worldX, double radius, double depth)
    {
        if (radius <= 0 || depth <= 0) return 0;

        double removed = 0;
        for (int i = 0; i < Heights.Length; i++)
        {
            double columnX = SurfaceLeft + (i + 0.5) * ColumnWidth;
            double distance = Math.Abs(columnX - worldX);
            if (distance > radius) continue;

            double bite = (1.0 - distance / radius) * depth;
            double take = Math.Min(Heights[i], bite);
            Heights[i] -= take;
            removed += take * ColumnWidth;
        }
        return removed;
    }

    public void Relax(double strength)
    {
        int n = Heights.Length;
        if (n == 0) return;
        if (strength <= 0) return;
        if (strength > 1) strength = 1;

        double limit = MaxSlope * ColumnWidth;
        if (Agitation > Shaking) limit *= 0.35;

        for (int pass = 0; pass < 6; pass++)
        {
            bool shifted = false;
            bool forward = (pass & 1) == 0;
            for (int k = 0; k < n - 1; k++)
            {
                int i = forward ? k : n - 2 - k;
                double difference = Heights[i] - Heights[i + 1];
                if (difference > limit + 0.01)
                {
                    double transfer = Math.Min((difference - limit) * 0.5 * strength, Heights[i]);
                    Heights[i] -= transfer;
                    Heights[i + 1] += transfer;
                    if (transfer > 0) { shifted = true; }
                }
                else if (difference < -limit - 0.01)
                {
                    double transfer = Math.Min((-difference - limit) * 0.5 * strength, Heights[i + 1]);
                    Heights[i + 1] -= transfer;
                    Heights[i] += transfer;
                    if (transfer > 0) { shifted = true; }
                }
            }

            if (Heights[0] > limit + 0.01)
            {
                double transfer = (Heights[0] - limit) * 0.5 * strength;
                Heights[0] -= transfer;
                _spillLeft += transfer * ColumnWidth;
                shifted = true;
            }
            if (Heights[n - 1] > limit + 0.01)
            {
                double transfer = (Heights[n - 1] - limit) * 0.5 * strength;
                Heights[n - 1] -= transfer;
                _spillRight += transfer * ColumnWidth;
                shifted = true;
            }

            if (!shifted) break;
        }

        for (int i = 1; i < n - 2; i++)
        {
            double kink = (Heights[i] - Heights[i + 1])
                - 0.5 * ((Heights[i - 1] - Heights[i]) + (Heights[i + 1] - Heights[i + 2]));
            if (Math.Abs(kink) <= 0.02) continue;
            if (kink > 0)
            {
                double transfer = Math.Min(kink * Rounding * strength, Heights[i]);
                Heights[i] -= transfer;
                Heights[i + 1] += transfer;
            }
            else
            {
                double transfer = Math.Min(-kink * Rounding * strength, Heights[i + 1]);
                Heights[i + 1] -= transfer;
                Heights[i] += transfer;
            }
        }

        double ceiling = Depth * 1.15;
        for (int i = 0; i < n; i++)
        {
            if (Heights[i] > ceiling) Heights[i] = ceiling;
            if (Heights[i] < 0) Heights[i] = 0;
        }
    }

    public void TakeSpill(out double left, out double right)
    {
        left = _spillLeft;
        right = _spillRight;
        _spillLeft = 0;
        _spillRight = 0;
    }

    public void Melt(double dt)
    {
        double keep = Math.Exp(-dt * 0.12);
        for (int i = 0; i < Heights.Length; i++)
        {
            Heights[i] *= keep;
            if (Heights[i] < 0.05) Heights[i] = 0;
        }
    }

    public double Volume
    {
        get
        {
            double sum = 0;
            foreach (var h in Heights) sum += h;
            return sum * ColumnWidth;
        }
    }

    public double Peak
    {
        get
        {
            double peak = 0;
            foreach (var h in Heights)
            {
                if (h > peak) peak = h;
            }
            return peak;
        }
    }

    public bool HasSnow => Peak > 0.12;

    private static double SmoothDamp(double current, ref double velocity, double target, double smoothTime, double dt)
    {
        double omega = 2.0 / smoothTime;
        double x = omega * dt;
        double exp = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
        double change = current - target;
        double temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }
}

public readonly struct SnowChunk
{
    public SnowChunk(double x, double y, double height, double vx, double vy)
    {
        X = x;
        Y = y;
        Height = height;
        VX = vx;
        VY = vy;
    }

    public double X { get; }
    public double Y { get; }
    public double Height { get; }
    public double VX { get; }
    public double VY { get; }
    public double Volume => Height * SnowDrift.ColumnWidth;
}