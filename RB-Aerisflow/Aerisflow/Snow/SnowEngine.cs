using System;
using System.Collections.Generic;

namespace Aerisflow;

public enum FullscreenOverlayMode { On, Off }

public sealed class SnowEngine
{
    private const double MissingGrace = 0.05;
    private const double JumpDistance = 240.0;
    private const int    MaxFlakes   = 2500;
    private const double Shaking = 2.0;
    private const double FixedPiece = 10.0;

    public double MeltAfterSeconds { get; set; } = 0;

    private readonly List<Flake> _flakes = new List<Flake>();
    private readonly List<WindowRect> _windows = new List<WindowRect>();
    private readonly List<SnowDrift> _drifts = new List<SnowDrift>();
    private readonly List<SnowChunk> _chunks = new List<SnowChunk>();
    private readonly Random _rand = new Random();

    private HashSet<IntPtr> _seen = new HashSet<IntPtr>();
    private List<(double Left, double Right)> _segmentsA = new List<(double Left, double Right)>();
    private List<(double Left, double Right)> _segmentsB = new List<(double Left, double Right)>();

    private double _time;
    private double _spawnAcc;
    private double _frameDt = 1.0 / 60.0;
    private double _width;
    private int _recycleIndex;
    private SnowDrift? _ground;

    private double _groundLeft;
    private double _groundBottom;
    private double _groundRight;
    private bool _hasGround;

    public double Top { get; private set; }
    public double Bottom { get; private set; }
    public double ScreenHeight => Bottom - Top;
    public double RenderAlpha => 1.0;

    public double Width
    {
        get => _width;
        set
        {
            if (Math.Abs(_width - value) < 0.5) return;
            _width = value;
            ResetDrifts();
        }
    }

    public double Intensity { get; set; } = 0.6;
    public double Opacity   { get; set; } = 1.0;
    public bool   Melt      { get; set; }
    public double Wind      { get; set; } = 0;

    public double SpeedMultiplier    { get; set; } = 1.0;
    public double ModeIntensityBoost { get; set; } = 1.0;
    public double ModeSpeedBoost     { get; set; } = 1.0;

    public int TargetFps { get; set; } = 60;

    public FullscreenOverlayMode FullscreenMode { get; set; } = FullscreenOverlayMode.On;
    public bool IsFullscreenAppActive { get; set; }

    public IReadOnlyList<Flake> Flakes   => _flakes;
    public IReadOnlyList<SnowDrift> Drifts => _drifts;

    public bool IsEmpty => _flakes.Count == 0 && _drifts.TrueForAll(d => !d.HasSnow);

    public void Advance(double frameDt, IReadOnlyList<WindowRect> windows)
    {
        if (frameDt <= 0) return;
        if (frameDt > 0.05) frameDt = 0.05;
        _frameDt = frameDt;
        SyncWindows(windows, frameDt);
        Update(frameDt);
    }

    public void SetScreen(double top, double bottom)
    {
        if (Math.Abs(Top - top) < 0.01 && Math.Abs(Bottom - bottom) < 0.01) return;
        Top = top;
        Bottom = bottom;
        ResetDrifts();
    }

    public void SyncWindows(IReadOnlyList<WindowRect> windows, double dt)
    {
        if (dt <= 0) dt = 1.0 / 60.0;

        _windows.Clear();
        _seen.Clear();
        if (windows != null)
        {
            foreach (var w in windows)
            {
                if (w != null && w.Handle != IntPtr.Zero && w.Bottom > w.Top && w.Right > w.Left)
                    _windows.Add(w);
            }
        }

        EnsureGround();

        var seen = _seen;
        for (int wi = 0; wi < _windows.Count; wi++)
        {
            var w = _windows[wi];
            seen.Add(w.Handle);

            var visible = VisibleSegments(wi);

            var drift = FindDrift(w.Handle);
            if (drift == null)
            {
                drift = new SnowDrift(w.Handle, w.ClassName, w.Left, w.Top, w.Right, SnowDrift.LedgeDepth,
                    _rand.NextDouble() * Math.PI * 2, _rand.NextDouble() * Math.PI * 2);
                drift.Born = _time;
                drift.SetVisible(visible);
                _drifts.Add(drift);
                continue;
            }

            drift.SetVisible(visible);

            double dx = w.Left - drift.Left;
            double dy = w.Top - drift.Top;
            double seconds = Math.Max(dt, 1.0 / 240.0);
            double currentVX = dx / seconds;
            double currentVY = dy / seconds;
            drift.LastSpeed = Math.Sqrt(dx * dx + dy * dy) / Math.Max(dt, 1.0 / 120.0);

            if (Math.Abs(dx) > JumpDistance || Math.Abs(dy) > JumpDistance)
            {
                if (drift.HasSnow)
                {
                    int bias = dx > 0 ? -1 : (dx < 0 ? 1 : 0);
                    Shed(drift, drift.Volume, 0, 0, 0, bias);
                }
                drift.ResetMotion(w.Left, w.Top, w.Right);
            }
            else
            {
                drift.MoveTo(w.Left, w.Top, w.Right);
            }

            drift.LastVX = currentVX;
            drift.LastVY = currentVY;
        }

        for (int i = _drifts.Count - 1; i >= 0; i--)
        {
            var drift = _drifts[i];
            if (drift.Owner == IntPtr.Zero) continue;

            if (seen.Contains(drift.Owner))
            {
                drift.MissingFor = 0;
                continue;
            }

            drift.MissingFor += dt;
            if (drift.MissingFor < MissingGrace) continue;

            Shed(drift, drift.Volume, 0.15, drift.LastVX, drift.LastVY, 0);
            _drifts.RemoveAt(i);
        }
    }

    public void Update(double dt)
    {
        if (dt <= 0) return;

        _time += dt;

        double rate = 45.0 * Intensity * Math.Max(0.1, ModeIntensityBoost);
        _spawnAcc += dt * rate;
        if (_spawnAcc > 4) _spawnAcc = 4;
        int guard = 0;
        while (_spawnAcc >= 1.0 && guard < 24 && _flakes.Count < MaxFlakes)
        {
            _spawnAcc -= 1.0;
            Spawn();
            guard++;
        }

        double gust = 8.0 * Math.Sin(_time * 0.35) + 5.0 * Math.Sin(_time * 1.1 + 1.7);

        int write = 0;
        int count = _flakes.Count;
        for (int read = 0; read < count; read++)
        {
            var f = _flakes[read];
            if (UpdateFlake(ref f, dt, gust)) _flakes[write++] = f;
        }
        if (write < count) _flakes.RemoveRange(write, count - write);

        double relaxStrength = Math.Min(1.0, dt * 60.0);

        foreach (var drift in _drifts)
        {
            if (Melt || (MeltAfterSeconds > 0 && _time - drift.Born >= MeltAfterSeconds))
                drift.Melt(dt);

            drift.Step(dt);

            if (drift.BrokeOff)
            {
                drift.BreakVelocity(out double vx, out double vy);
                Shed(drift, drift.Volume, 1.0, vx, vy, 0);
                drift.Shedding = false;
            }
            else
            {
                StepShedding(drift, dt);
                PeelShaken(drift, dt);
            }

            _chunks.Clear();
            drift.TakeChunks(_chunks);
            for (int i = 0; i < _chunks.Count; i++)
                ReleaseChunk(drift, _chunks[i]);

            drift.Relax(relaxStrength);

            drift.TakeSpill(out double spillLeft, out double spillRight);
            if (spillLeft > 1.5) ReleaseEdge(drift, true, spillLeft);
            if (spillRight > 1.5) ReleaseEdge(drift, false, spillRight);
        }
    }

    private void PeelShaken(SnowDrift drift, double dt)
    {
        if (drift.Owner == IntPtr.Zero) return;
        if (drift.Agitation <= Shaking || !drift.HasSnow) return;

        double rate = Math.Min(1.2, 0.15 * (drift.Agitation - Shaking));
        int picks = (int)(drift.Heights.Length * rate * dt * 3.0 + _rand.NextDouble());
        if (picks <= 0) return;

        drift.BreakVelocity(out double cvx, out double cvy);
        double fixedR = Math.Clamp(Math.Sqrt(FixedPiece / Math.PI), 0.6, 2.2);

        for (int p = 0; p < picks; p++)
        {
            int col = _rand.Next(drift.Heights.Length);
            double h = drift.Heights[col];
            if (h <= 0.2) continue;

            double amount = Math.Min(h, FixedPiece / SnowDrift.ColumnWidth);
            drift.Heights[col] = h - amount;

            double x = drift.SurfaceLeft + (col + 0.5) * SnowDrift.ColumnWidth;
            double y = drift.SurfaceTop - h + amount;

            var f = NewFlake(ParticlePreset.Lump, x, y);
            f.R = fixedR * (0.8 + _rand.NextDouble() * 0.4);
            f.Volume = FixedPiece;
            f.Alpha = 0.95;
            f.IgnoreOwner = drift.Owner;
            f.Lump = true;
            f.FallSpeed = 0;
            f.SwayAmp = 0;
            f.SwaySpeed = 0;

            double dirBoost = Math.Sign(cvx) * Math.Min(120, Math.Abs(cvx) * 0.4);
            f.VX = cvx + (_rand.NextDouble() - 0.5) * 160 + dirBoost;
            f.VY = cvy - (50 + _rand.NextDouble() * 150);
            AddFlake(f);
        }
    }

    private void StepShedding(SnowDrift drift, double dt)
    {
        if (drift.Owner == IntPtr.Zero) return;

        double vx = drift.TrackVX;
        double vy = drift.TrackVY;
        double speed = Math.Sqrt(vx * vx + vy * vy);

        if (drift.Shedding)
        {
            if (speed < 350) drift.Shedding = false;
        }
        else if (speed > 500)
        {
            drift.Shedding = true;
        }

        if (!drift.Shedding || !drift.HasSnow) return;

        double dV = drift.Volume * Math.Min(8.0, (speed - 500) * 0.006) * dt;
        if (dV < 0.3) return;

        int bias = vx > 0 ? -1 : (vx < 0 ? 1 : 0);
        Shed(drift, dV, 0.6, vx, vy, bias);
    }

    private void AddFlake(Flake f)
    {
        if (_flakes.Count < MaxFlakes)
        {
            _flakes.Add(f);
            return;
        }
        if (_recycleIndex >= _flakes.Count) _recycleIndex = 0;
        _flakes[_recycleIndex] = f;
        _recycleIndex++;
    }

    private Flake NewFlake(ParticlePreset preset, double x, double y)
    {
        var data = ParticlePresetData.Of(preset);
        return new Flake
        {
            X = x,
            Y = y,
            PrevX = x,
            PrevY = y,
            R = data.Radius(_rand),
            FallSpeed = data.Fall(_rand),
            Alpha = data.Alpha(_rand),
            Phase = _rand.NextDouble() * Math.PI * 2,
            SwayAmp = data.Sway(_rand),
            SwaySpeed = data.SwaySpeed(_rand),
            Preset = preset,
        };
    }

    private void Spawn()
    {
        if (_flakes.Count >= MaxFlakes) return;

        double u = _rand.NextDouble();
        var preset = u < 0.62 ? ParticlePreset.Far
            : u < 0.92 ? ParticlePreset.Mid
            : ParticlePreset.Near;

        var f = NewFlake(preset, _rand.NextDouble() * Math.Max(10, Width), 0);

        double speedFactor = Math.Max(0.1, SpeedMultiplier * ModeSpeedBoost);
        f.FallSpeed *= speedFactor;

        f.Y = Top - f.R - _rand.NextDouble() * 40;
        f.PrevY = f.Y;
        f.VX = 0;
        f.VY = f.FallSpeed;
        _flakes.Add(f);
    }

    private bool UpdateFlake(ref Flake f, double dt, double gust)
    {
        if (f.IgnoreOwner != IntPtr.Zero && f.VY >= 0)
            f.IgnoreOwner = IntPtr.Zero;

        f.PrevX = f.X;
        f.PrevY = f.Y;

        if (f.MoteLife > 0)
        {
            f.MoteLife -= dt;
            f.VX *= Math.Exp(-dt * 3.2);
            f.VY *= Math.Exp(-dt * 3.2);
            f.VY += 30 * dt;
            f.X += (f.VX + Wind * 0.4) * dt;
            f.Y += f.VY * dt;
            f.Alpha *= Math.Exp(-dt * 2.5);
            return f.MoteLife > 0 && f.Alpha > 0.02;
        }

        double prevBottom = f.Y + f.R;

        if (f.Lump)
        {
            f.VY += 2500 * dt;
            f.X += f.VX * dt;
            f.Y += f.VY * dt;
            f.VX *= Math.Exp(-dt * 1.2);

            double newBottom = f.Y + f.R;
            SnowDrift? target = FindLanding(f, prevBottom, newBottom);
            if (target != null)
            {
                double volume = f.Volume > 0 ? f.Volume : Math.PI * f.R * f.R;
                target.Add(f.X - target.SurfaceLeft, volume);
                double surface = target.SurfaceTop - target.HeightAtWorld(f.X);
                double impact = Math.Max(0, f.VY);
                if (impact > 500 && volume > 40)
                    Puff(f.X, surface, f.VX * 0.3, -impact * 0.1, volume * 0.08);
                return false;
            }

            if (f.Y > Bottom)
            {
                var ground = _drifts.Count > 0 ? _drifts[0] : null;
                if (ground != null && ground.Owner == IntPtr.Zero)
                {
                    double volume = f.Volume > 0 ? f.Volume : Math.PI * f.R * f.R;
                    ground.Add(f.X - ground.SurfaceLeft, volume);
                    return false;
                }
            }

            if (f.X < 0) { f.X = 0; f.VX = Math.Abs(f.VX) * 0.3; }
            if (f.X > Width) { f.X = Width; f.VX = -Math.Abs(f.VX) * 0.3; }
            return true;
        }

        double sway = Math.Sin(_time * f.SwaySpeed * 1.2 + f.Phase) * f.SwayAmp;
        f.VX *= Math.Exp(-dt * 2.0);
        f.VY += (f.FallSpeed * 1.5 - f.VY) * (1.0 - Math.Exp(-dt * 4.0));

        f.X += (f.VX + sway + Wind + gust) * dt * 1.3;
        f.Y += f.VY * dt;

        if (f.X < 0) { f.X = 0; f.VX = Math.Abs(f.VX) * 0.3; }
        if (f.X > Width) { f.X = Width; f.VX = -Math.Abs(f.VX) * 0.3; }

        double newBottom2 = f.Y + f.R;
        SnowDrift? target2 = FindLanding(f, prevBottom, newBottom2);
        if (target2 != null)
        {
            double volume = f.Volume > 0 ? f.Volume : Math.PI * f.R * f.R;
            target2.Add(f.X - target2.SurfaceLeft, volume);
            return false;
        }

        if (f.Y > Bottom)
        {
            var ground = _drifts.Count > 0 ? _drifts[0] : null;
            if (ground != null && ground.Owner == IntPtr.Zero)
            {
                double volume = f.Volume > 0 ? f.Volume : Math.PI * f.R * f.R;
                ground.Add(f.X - ground.SurfaceLeft, volume);
                return false;
            }
        }

        return true;
    }

    private List<(double Left, double Right)> VisibleSegments(int index)
    {
        var w = _windows[index];
        var segments = _segmentsA;
        segments.Clear();
        segments.Add((0, w.Width));

        List<(double Left, double Right)> scratch = _segmentsB;
        for (int j = 0; j < index; j++)
        {
            var above = _windows[j];

            if (above.Bottom <= w.Top - SnowDrift.MaxHeight * 1.15) continue;
            if (above.Top >= w.Top) continue;

            double left = above.Left - w.Left;
            double right = above.Right - w.Left;
            if (right <= 0 || left >= w.Width) continue;

            scratch.Clear();
            var remaining = scratch;
            foreach (var (segLeft, segRight) in segments)
            {
                if (right <= segLeft || left >= segRight)
                {
                    remaining.Add((segLeft, segRight));
                    continue;
                }
                if (left > segLeft) remaining.Add((segLeft, left));
                if (right < segRight) remaining.Add((right, segRight));
            }

            segments = scratch;
            scratch = _segmentsA;
            if (segments.Count == 0) break;
        }

        return segments;
    }

    private SnowDrift? FindLanding(Flake f, double prevBottom, double newBottom)
    {
        SnowDrift? target = null;
        double highest = double.PositiveInfinity;

        foreach (var drift in _drifts)
        {
            if (f.Lump && f.IgnoreOwner != IntPtr.Zero && drift.Owner == f.IgnoreOwner)
            {
                if (f.VY < 0) continue;
            }
            if (f.X + f.R <= drift.SurfaceLeft || f.X - f.R >= drift.SurfaceRight) continue;
            if (!drift.IsVisibleAtWorld(f.X)) continue;

            double surface = drift.SurfaceTop - drift.HeightAtWorld(f.X);
            if (surface < 2) continue;
            if (surface + 0.5 < prevBottom) continue;
            if (surface > newBottom + 0.5) continue;

            if (surface < highest)
            {
                highest = surface;
                target = drift;
            }
        }

        return target;
    }

    private void ReleaseChunk(SnowDrift drift, SnowChunk chunk)
    {
        if (chunk.Volume < 0.4) return;

        double radius = Math.Clamp(Math.Sqrt(chunk.Volume / Math.PI), 0.6, 2.5);
        var lump = NewFlake(ParticlePreset.Lump, chunk.X, chunk.Y);
        lump.R = radius;
        lump.Volume = chunk.Volume;
        lump.Alpha = 0.95;
        lump.IgnoreOwner = drift.Owner;
        lump.Lump = true;
        lump.FallSpeed = 0;
        lump.SwayAmp = 0;
        lump.SwaySpeed = 0;
        lump.VX = chunk.VX * 0.35;
        lump.VY = chunk.VY * 0.2;
        AddFlake(lump);
    }

    private void ReleaseEdge(SnowDrift drift, bool left, double volume)
    {
        if (volume < 0.5) return;

        int count = Math.Clamp((int)Math.Ceiling(volume / 8.0), 1, 4);
        double piece = volume / count;
        double r = Math.Clamp(Math.Sqrt(piece / Math.PI), 0.6, 2.0);

        double x = left ? drift.SurfaceLeft + 0.5 : drift.SurfaceRight - 0.5;
        double y = drift.SurfaceTop - 0.5;

        for (int i = 0; i < count; i++)
        {
            var f = NewFlake(ParticlePreset.Powder,
                x + (_rand.NextDouble() - 0.5) * 4,
                y - _rand.NextDouble() * 2);
            f.R = r;
            f.Volume = piece;
            f.Alpha = 0.8;
            f.IgnoreOwner = drift.Owner;
            f.FallSpeed = 90 + _rand.NextDouble() * 60;
            f.VX = (left ? -1 : 1) * (6 + _rand.NextDouble() * 12) + drift.TrackVX * 0.2;
            f.VY = 8 + _rand.NextDouble() * 10;
            f.SwayAmp = 2 + _rand.NextDouble() * 6;
            f.SwaySpeed = 0.6 + _rand.NextDouble() * 1.2;
            AddFlake(f);
        }
    }

    private void Shed(SnowDrift drift, double volume, double inherit, double vx, double vy, int edgeBias)
    {
        if (volume <= 0.2) return;
        double total = drift.Volume;
        if (total <= 0.2) return;
        if (volume > total) volume = total;

        vx = Math.Clamp(vx, -1200, 1200);
        vy = Math.Clamp(vy, -1200, 1200);

        int count = Math.Clamp((int)Math.Ceiling(volume / FixedPiece), 2, 600);

        var xs = new double[count];
        var ys = new double[count];
        for (int i = 0; i < count; i++)
        {
            double localX = edgeBias != 0 ? SampleColumnBias(drift, edgeBias) : SampleColumn(drift);
            double depth = drift.HeightAt(localX);
            xs[i] = drift.SurfaceLeft + localX;
            ys[i] = drift.SurfaceTop - depth * _rand.NextDouble();
        }

        double removed = drift.RemoveVolume(volume, edgeBias);
        if (removed <= 0.01) return;

        double baseRadius = Math.Clamp(Math.Sqrt(FixedPiece / Math.PI), 0.6, 2.2);

        for (int i = 0; i < count; i++)
        {
            double r = baseRadius * (0.7 + _rand.NextDouble() * 0.6);
            double inheritScale = 0.7 + _rand.NextDouble() * 0.3;

            var f = NewFlake(ParticlePreset.Lump, xs[i], ys[i] - r - 0.5);
            f.R = r;
            f.Volume = FixedPiece;
            f.Alpha = 0.95;
            f.IgnoreOwner = drift.Owner;
            f.Lump = true;
            f.FallSpeed = 0;
            f.SwayAmp = 0;
            f.SwaySpeed = 0;
            f.VX = vx * inherit * inheritScale + (_rand.NextDouble() - 0.5) * 160;
            double rawVY = vy * inherit * inheritScale + (_rand.NextDouble() - 0.5) * 110;
            f.VY = vy < -80 ? rawVY : Math.Max(0, rawVY);
            AddFlake(f);
        }
    }

    private double SampleColumnBias(SnowDrift drift, double bias)
    {
        int n = drift.Heights.Length;
        if (n == 0) return 0;

        double width = n * SnowDrift.ColumnWidth;
        double u = _rand.NextDouble();

        double localX;
        if (bias > 0) localX = width * (0.7 + u * 0.3);
        else          localX = width * u * 0.3;

        return Math.Clamp(localX, 0, width - 0.001);
    }

    private void Puff(double x, double y, double vx, double vy, double volume)
    {
        if (volume <= 0.2) return;

        int count = Math.Clamp((int)Math.Ceiling(volume / 6.0), 1, 8);
        double baseRadius = Math.Clamp(Math.Sqrt(volume / count / Math.PI), 0.6, 3.0);

        for (int i = 0; i < count; i++)
        {
            var f = NewFlake(ParticlePreset.Powder,
                x + (_rand.NextDouble() - 0.5) * 18,
                y - _rand.NextDouble() * 6);
            f.R = baseRadius * (0.7 + _rand.NextDouble() * 0.6);
            f.Volume = 0;
            f.Alpha = 0.18 + _rand.NextDouble() * 0.12;
            f.MoteLife = 0.45 + _rand.NextDouble() * 0.45;
            f.VX = vx * 0.12 + (_rand.NextDouble() - 0.5) * 30;
            f.VY = vy * 0.12 - 6 - _rand.NextDouble() * 22;
            f.SwayAmp = 0;
            f.SwaySpeed = 0;
            AddFlake(f);
        }
    }

    private double SampleColumn(SnowDrift drift)
    {
        int n = drift.Heights.Length;
        if (n == 0) return 0;

        double total = drift.Volume;
        if (total <= 0.001) return _rand.NextDouble() * n * SnowDrift.ColumnWidth;

        double pick = _rand.NextDouble() * total;
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            acc += drift.Heights[i] * SnowDrift.ColumnWidth;
            if (acc >= pick) return (i + 0.5) * SnowDrift.ColumnWidth;
        }

        return (n - 0.5) * SnowDrift.ColumnWidth;
    }

    private void ResetDrifts()
    {
        _drifts.Clear();
        _ground = null;
        EnsureGround();
    }

    public void SetGround(double left, double bottom, double right)
    {
        if (right < left + SnowDrift.ColumnWidth) return;
        _groundLeft = left;
        _groundBottom = bottom;
        _groundRight = right;
        _hasGround = true;
        EnsureGround();
    }

    private void EnsureGround()
    {
        if (_width <= 0 || Bottom <= Top) return;

        if (_ground == null)
        {
            _ground = new SnowDrift(IntPtr.Zero, string.Empty, _hasGround ? _groundLeft : 0,
                _hasGround ? _groundBottom : Bottom, _hasGround ? _groundRight : _width, SnowDrift.MaxHeight,
                _rand.NextDouble() * Math.PI * 2, _rand.NextDouble() * Math.PI * 2);
            _ground.Born = _time;
            _drifts.Insert(0, _ground);
            return;
        }

        _ground.SetSurface(_hasGround ? _groundLeft : 0, _hasGround ? _groundBottom : Bottom, _hasGround ? _groundRight : _width);
    }

    private SnowDrift? FindDrift(IntPtr owner)
    {
        foreach (var drift in _drifts)
        {
            if (drift.Matches(owner)) return drift;
        }
        return null;
    }

    public void Spray(double x, double y, double vx, double vy)
    {
        double scale = Math.Clamp(_frameDt * 60.0, 0.25, 1.0);
        double speed = Math.Sqrt(vx * vx + vy * vy);
        double radius = 20.0 + Math.Min(30.0, speed * 0.015);

        SnowDrift? target = null;
        double targetSurface = double.MaxValue;
        double targetDistance = double.MaxValue;

        foreach (var drift in _drifts)
        {
            if (!drift.HasSnow) continue;
            if (x < drift.SurfaceLeft || x > drift.SurfaceRight) continue;

            double top = drift.SurfaceTop;
            double surface = top - drift.HeightAtWorld(x);
            if (y < surface - 15 || y > top + 20) continue;

            double distance = Math.Abs(y - surface);
            if (distance < targetDistance)
            {
                targetDistance = distance;
                targetSurface = surface;
                target = drift;
            }
        }

        if (target == null)
        {
            PushFlakes(x, y, vx, vy, radius, scale);
            return;
        }

        if (speed < 400)
        {
            target.Push(x, vx, _frameDt);
            PushFlakes(x, y, vx, vy, radius, scale);
        }
        else
        {
            double depth = (6.0 + Math.Min(10.0, speed * 0.005)) * scale;
            double removed = target.Shave(x, radius, depth);
            if (removed <= 0.1) return;

            int lumps = Math.Clamp((int)Math.Ceiling(removed / FixedPiece), 2, 24);
            double r = Math.Clamp(Math.Sqrt(FixedPiece / Math.PI), 0.6, 2.2);

            double launchVY = -Math.Min(550, speed * 0.35);
            double spreadX = Math.Sign(vx) * Math.Min(250, speed * 0.25);

            for (int i = 0; i < lumps; i++)
            {
                var lump = NewFlake(ParticlePreset.Lump,
                    x + (_rand.NextDouble() - 0.5) * radius,
                    targetSurface - r - 1.0);
                lump.R = r * (0.8 + _rand.NextDouble() * 0.4);
                lump.Volume = FixedPiece;
                lump.Alpha = 0.95;
                lump.IgnoreOwner = target.Owner;
                lump.Lump = true;
                lump.FallSpeed = 0;
                lump.SwayAmp = 0;
                lump.SwaySpeed = 0;
                lump.VX = vx * 0.5 + spreadX + (_rand.NextDouble() - 0.5) * 120;
                lump.VY = launchVY + (_rand.NextDouble() - 0.5) * 80;
                AddFlake(lump);
            }

            int powderCount = Math.Clamp((int)(removed / 15), 2, 10);
            for (int i = 0; i < powderCount; i++)
            {
                var f = NewFlake(ParticlePreset.Powder,
                    x + (_rand.NextDouble() - 0.5) * radius * 1.5,
                    targetSurface - _rand.NextDouble() * 8);
                f.R = 0.8 + _rand.NextDouble() * 1.2;
                f.Volume = 0;
                f.Alpha = 0.4 + _rand.NextDouble() * 0.2;
                f.MoteLife = 0;
                f.FallSpeed = 160 + _rand.NextDouble() * 140;
                f.VX = vx * 0.35 + spreadX * 0.4 + (_rand.NextDouble() - 0.5) * 60;
                f.VY = launchVY * 0.6 - 15 - _rand.NextDouble() * 30;
                f.SwayAmp = 2 + _rand.NextDouble() * 4;
                f.SwaySpeed = 0.5 + _rand.NextDouble() * 0.8;
                AddFlake(f);
            }
        }
    }

    private void PushFlakes(double x, double y, double vx, double vy, double radius, double scale)
    {
        double speed = Math.Sqrt(vx * vx + vy * vy);
        int count = _flakes.Count;
        for (int i = 0; i < count; i++)
        {
            var f = _flakes[i];
            if (f.MoteLife > 0) continue;
            double dx = f.X - x;
            double dy = f.Y - y;
            double distSq = dx * dx + dy * dy;
            double sumR = radius + f.R;
            if (distSq >= sumR * sumR) continue;

            double m = Math.Sqrt(distSq);
            if (m < 0.001) m = 0.001;

            double push = (15 + speed * 0.03) * scale;
            f.VX += (dx / m) * push + vx * 0.03 * scale;
            f.VY += (dy / m) * push * 0.4 - 4 * scale;
            _flakes[i] = f;
        }
    }

    public void Clear()
    {
        _flakes.Clear();
        _recycleIndex = 0;
        ResetDrifts();
    }
}