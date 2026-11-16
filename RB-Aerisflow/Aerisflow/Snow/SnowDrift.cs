using System;
using System.Collections.Generic;

namespace Aerisflow;

/// <summary>
/// A procedural snowdrift bound to a surface: the screen's bottom edge, a window's
/// top edge, or the taskbar. It is a height field of narrow columns, and it carries
/// the identity of the window it lies on. The snow rides that window as a body:
/// friction holds it until the window is jerked, then whole columns slide off the
/// ends and fall, exactly as the original Aerisflow does.
/// </summary>
public sealed class SnowDrift
{
    /// <summary>Horizontal resolution of the height field, in DIPs. The original uses 2.</summary>
    public const double ColumnWidth = 2.0;

    /// <summary>Angle of repose: the steepest rise one column may have over the next.</summary>
    public const double MaxSlope = 0.8;

    /// <summary>How much of a crest's kink is rounded away each relax step.</summary>
    public const double Rounding = 0.15;

    /// <summary>Deepest snow the open ground can hold, in DIPs.</summary>
    public const double MaxHeight = 40.0;

    /// <summary>How deep a window ledge's snow can get, in DIPs.</summary>
    public const double LedgeDepth = 14.0;

    /// <summary>How far a column must overhang before it slides off, in DIPs.</summary>
    private const double SlideStep = ColumnWidth;

    // The body riding on the window. The original constants, kept in DIPs and seconds.
    private const double Gravity = 2500.0;
    private const double Adhesion = 3500.0;
    private const double Grip = 3.0;
    private const double Slip = 2.0;
    private const double BreakLift = 18.0;
    private const double TrackTime = 0.05;
    private const double Shaking = 4.0;

    private readonly List<(double Left, double Right)> _visible = new List<(double Left, double Right)>();
    private readonly List<SnowChunk> _chunks = new List<SnowChunk>();
    private double[] _capacity = Array.Empty<double>();
    private readonly double _phase0;
    private readonly double _phase1;

    // A landed flake is not a point: its volume is spread over a few columns so
    // the drift grows in soft, narrow bumps instead of one-column spikes or a bank.
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
    private bool _brokeOff;
    private double _breakVX;
    private double _breakVY;

    public IntPtr Owner { get; }
    public string ClassName { get; }
    public double Born;

    public double Left { get; private set; }
    public double Top { get; private set; }
    public double Right { get; private set; }
    public double[] Heights { get; private set; }

    /// <summary>Deepest snow this particular surface can hold.</summary>
    public double Depth { get; }

    /// <summary>How far the snow body has slid along its support, in DIPs, right positive.</summary>
    public double OffsetX { get; private set; }

    /// <summary>How far the snow body has lifted off its support, in DIPs, up positive.</summary>
    public double LiftY { get; private set; }

    /// <summary>How violently the support has been shaken back and forth, in gravities.</summary>
    public double Agitation { get; private set; }

    /// <summary>Whether the last step shook the whole heap off its support.</summary>
    public bool BrokeOff => _brokeOff;

    /// <summary>Backwards-compatible aliases for diagnostics: the snow body's displacement.</summary>
    public double LagX => OffsetX;
    public double LagY => -LiftY;

    /// <summary>Last observed window velocity, used by the cursor and for diagnostics.</summary>
    public double LastVX { get; set; }
    public double LastVY { get; set; }
    public double LastSpeed { get; set; }

    /// <summary>How long the window has been missing from the tracker, in seconds.</summary>
    public double MissingFor { get; set; }

    public double SurfaceLeft => Left + OffsetX;
    public double SurfaceTop => Top - LiftY;
    public double SurfaceRight => SurfaceLeft + (Right - Left);

    /// <summary>
    /// The parts of the drift's top edge that aren't hidden behind a window in
    /// front, in local coordinates from the surface's left edge.
    /// </summary>
    public IReadOnlyList<(double Left, double Right)> Visible => _visible;

    public SnowDrift(IntPtr owner, string className, double left, double top, double right, double depth = MaxHeight, double phase0 = 0, double phase1 = 0)
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

    /// <summary>
    /// Moves the support under the snow. The snow body keeps its own offset and is
    /// stepped separately, so it trails, slides and spills instead of snapping along.
    /// </summary>
    public void MoveTo(double left, double top, double right)
    {
        LastVX = left - Left;
        LastVY = top - Top;
        Left = left;
        Top = top;
        Resize(right);
    }

    /// <summary>The world-space velocity of the support, for diagnostics and inheritance.</summary>
    public double TrackVX => _trackVX;
    public double TrackVY => _trackVY;

    /// <summary>Moves the surface without any inertia (used by the fixed ground).</summary>
    public void SetSurface(double left, double top, double right)
    {
        Left = left;
        Top = top;
        Resize(right);
        OffsetX = 0;
        LiftY = 0;
        _offsetVX = 0;
        _liftVY = 0;
        _trackX = left;
        _trackY = top;
        _trackVX = 0;
        _trackVY = 0;
        _visible.Clear();
        _visible.Add((0, Right - Left));
    }

    /// <summary>
    /// A window was teleported (restored, moved between monitors): nothing carried
    /// the snow, so the body starts fresh on the new support.
    /// </summary>
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
        _offsetVX = 0;
        _liftVY = 0;
        Agitation = 0;
        _swingPeak = 0;
        _previousSwingPeak = 0;
        _swingDirX = 0;
        _swingDirY = 0;
    }

    /// <summary>Records which parts of the top edge are not covered by a window in front.</summary>
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

    /// <summary>Whether the surface is actually on screen at this world x.</summary>
    public bool IsVisibleAtWorld(double worldX)
    {
        double local = worldX - SurfaceLeft;
        for (int i = 0; i < _visible.Count; i++)
        {
            if (local >= _visible[i].Left && local <= _visible[i].Right) return true;
        }
        return false;
    }

    /// <summary>
    /// Steps the snow body riding its support: friction, inertia, lift and sliding.
    /// Columns pushed past either end are queued as falling chunks.
    /// </summary>
    public void Step(double dt)
    {
        _brokeOff = false;
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
        double ax = (_trackVX - previousVX) / dt;
        double ay = (_trackVY - previousVY) / dt;

        // Agitation is vibration: two consecutive swings in opposite directions,
        // not a single start or stop.
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

        // Vertical: the heap lifts off when the window drops away faster than
        // gravity, or is yanked upward hard enough. Our y grows downward, so the
        // upward acceleration is -ay.
        double fall = -Gravity - ay;
        if (LiftY > 0 || fall > Adhesion)
        {
            _liftVY += fall * dt;
            LiftY += _liftVY * dt;
            if (LiftY <= 0)
            {
                LiftY = 0;
                _liftVY = 0;
            }
        }

        // Calm carries are rigid: unless the window is genuinely shaking or the
        // heap has left the ledge, the snow is glued down. No friction model runs,
        // so a fast drag cannot leave a trail or shed columns by accident.
        bool calm = Agitation < 1.0 && LiftY <= 0.0;

        if (!calm)
        {
            // Horizontal: static friction holds the heap, kinetic friction rubs it.
            double pressure = LiftY > 0 ? 0 : Math.Max(0, Gravity + Adhesion - ay);
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

            // Whole columns slide off whichever end the body has moved past.
            int guard = 0;
            while (Math.Abs(OffsetX) >= SlideStep && guard++ < 16)
            {
                int direction = OffsetX > 0 ? 1 : -1;
                SlideColumn(direction, _trackVX + slideVelocity, _trackVY - _liftVY);
                OffsetX -= direction * SlideStep;
            }
        }
        else
        {
            // A normal drag carries the snow as if it were glued on: any little
            // shift left by a bumpy mouse path pulls back at once.
            double glue = Math.Exp(-dt * 30.0);
            OffsetX *= glue;
            _offsetVX *= glue;
            if (Math.Abs(OffsetX) < 0.02)
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

    /// <summary>Drains the columns that slid off during the last step.</summary>
    public void TakeChunks(List<SnowChunk> destination)
    {
        for (int i = 0; i < _chunks.Count; i++) destination.Add(_chunks[i]);
        _chunks.Clear();
    }

    /// <summary>Velocity the whole heap was thrown with when it broke off, if it did.</summary>
    public void BreakVelocity(out double vx, out double vy)
    {
        vx = _breakVX;
        vy = _breakVY;
    }

    /// <summary>Empties the height field without creating particles; the caller samples it first.</summary>
    public void ClearSnow()
    {
        Array.Clear(Heights, 0, Heights.Length);
    }

    /// <summary>Moves one column off an end and remembers it as a falling chunk.</summary>
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

    /// <summary>
    /// Each column fills to its own depth, a little deeper or shallower than the
    /// next: the bank grows into rolling dunes instead of a flat plateau.
    /// </summary>
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

    /// <summary>How much room is left in a column: 1 when bare, 0 at capacity.</summary>
    public double Fill(int column)
    {
        if (column < 0 || column >= _capacity.Length) return 0;
        return Math.Max(0, 1.0 - Heights[column] / _capacity[column]);
    }

    public bool Matches(IntPtr owner) => Owner == owner;

    /// <summary>Snow depth at overlay coordinate <paramref name="worldX"/>.</summary>
    public double HeightAtWorld(double worldX) => HeightAt(worldX - SurfaceLeft);

    /// <summary>Snow depth at local coordinate <paramref name="x"/> (from the surface's left edge).</summary>
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

    /// <summary>Adds a flake's area at a local x; the drift gains that much cross-section.</summary>
    public void Add(double x, double volume)
    {
        if (volume <= 0) return;
        int n = Heights.Length;
        int centre = (int)Math.Floor(x / ColumnWidth);
        if (centre < 0 || centre >= n) return;

        // A column takes less as it fills, so a bank levels off into rolling dunes
        // instead of growing into a flat wall.
        double perColumn = volume * Fill(centre) / ColumnWidth;
        for (int k = 0; k < Spread.Length; k++)
        {
            int i = centre + k - 2;
            if (i < 0 || i >= n) continue;
            Heights[i] += perColumn * Spread[k];
        }
    }

    /// <summary>Removes a share of the whole drift (violent shaking). Returns the volume removed.</summary>
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

    /// <summary>Brushes a hollow into the drift under the cursor. Returns the volume removed.</summary>
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

    /// <summary>
    /// Evens the surface out and lets anything steeper than the angle of repose
    /// slide downhill. Shaken hard, the pile fluidizes: its repose drops and it
    /// slumps and spills off the ends instead of keeping a tower.
    /// </summary>
    public void Relax()
    {
        int n = Heights.Length;
        if (n == 0) return;

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
                    double transfer = Math.Min((difference - limit) * 0.5, Heights[i]);
                    Heights[i] -= transfer;
                    Heights[i + 1] += transfer;
                    if (transfer > 0) { shifted = true; }
                }
                else if (difference < -limit - 0.01)
                {
                    double transfer = Math.Min((-difference - limit) * 0.5, Heights[i + 1]);
                    Heights[i + 1] -= transfer;
                    Heights[i] += transfer;
                    if (transfer > 0) { shifted = true; }
                }
            }

            if (Heights[0] > limit + 0.01)
            {
                double transfer = (Heights[0] - limit) * 0.5;
                Heights[0] -= transfer;
                _spillLeft += transfer * ColumnWidth;
                shifted = true;
            }
            if (Heights[n - 1] > limit + 0.01)
            {
                double transfer = (Heights[n - 1] - limit) * 0.5;
                Heights[n - 1] -= transfer;
                _spillRight += transfer * ColumnWidth;
                shifted = true;
            }

            if (!shifted) break;
        }

        // Pull each kink toward its neighbours' average: rounds crests and notches.
        for (int i = 1; i < n - 2; i++)
        {
            double kink = (Heights[i] - Heights[i + 1])
                - 0.5 * ((Heights[i - 1] - Heights[i]) + (Heights[i + 1] - Heights[i + 2]));
            if (Math.Abs(kink) <= 0.02) continue;
            if (kink > 0)
            {
                double transfer = Math.Min(kink * Rounding, Heights[i]);
                Heights[i] -= transfer;
                Heights[i + 1] += transfer;
            }
            else
            {
                double transfer = Math.Min(-kink * Rounding, Heights[i + 1]);
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

    /// <summary>Volume that slid off each open end since the last call, in square DIPs.</summary>
    public void TakeSpill(out double left, out double right)
    {
        left = _spillLeft;
        right = _spillRight;
        _spillLeft = 0;
        _spillRight = 0;
    }

    /// <summary>Thaws the drift; the surface itself stays so snow can land on it again.</summary>
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

    public bool HasSnow => Peak > 0.3;

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

/// <summary>A column that slid off a drift and is now falling on its own.</summary>
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
