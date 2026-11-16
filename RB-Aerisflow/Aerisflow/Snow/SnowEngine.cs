using System;
using System.Collections.Generic;

namespace Aerisflow;

public sealed class SnowEngine
{
    private const double MissingGrace = 0.12;
    private const double JumpSpeed = 6000.0;

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
    private double _relaxAcc;
    private double _width;
    private SnowDrift? _ground;

    private double _groundLeft;
    private double _groundBottom;
    private double _groundRight;
    private bool _hasGround;

    public double Top { get; private set; }
    public double Bottom { get; private set; }
    public double ScreenHeight => Bottom - Top;

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
    public double Opacity { get; set; } = 1.0;
    public bool Melt { get; set; }
    public double Wind { get; set; } = 0;

    public int TargetFps { get; set; } = 60;

    public IReadOnlyList<Flake> Flakes => _flakes;
    public IReadOnlyList<SnowDrift> Drifts => _drifts;
    public bool IsEmpty => _flakes.Count == 0 && _drifts.TrueForAll(d => !d.HasSnow);

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
                {
                    _windows.Add(w);
                }
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
            double elapsed = Math.Max(dt, 1.0 / 120.0);
            double speed = Math.Sqrt(dx * dx + dy * dy) / elapsed;
            drift.LastSpeed = speed;

            if (speed > JumpSpeed)
            {
                if (drift.HasSnow)
                {
                    Spill(drift, drift.Volume, 0.7);
                    drift.ClearSnow();
                }
                drift.ResetMotion(w.Left, w.Top, w.Right);
            }
            else
            {
                drift.MoveTo(w.Left, w.Top, w.Right);
            }

            double seconds = Math.Max(dt, 1.0 / 240.0);
            drift.LastVX = dx / seconds;
            drift.LastVY = dy / seconds;
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

            Spill(drift, drift.Volume, 0.6);
            _drifts.RemoveAt(i);
        }
    }

    public void Update(double dt)
    {
        if (dt <= 0) return;
        _time += dt;

        double rate = 45.0 * Intensity;
        _spawnAcc += dt * rate;
        int guard = 0;
        while (_spawnAcc >= 1.0 && guard < 24)
        {
            _spawnAcc -= 1.0;
            Spawn();
            guard++;
        }

        // Усиленный ветер для более быстрой физики
        double gust = 8.0 * Math.Sin(_time * 0.35) + 5.0 * Math.Sin(_time * 1.1 + 1.7);

        int write = 0;
        int count = _flakes.Count;
        for (int read = 0; read < count; read++)
        {
            var f = _flakes[read];
            if (UpdateFlake(ref f, dt, gust)) _flakes[write++] = f;
        }
        if (write < count) _flakes.RemoveRange(write, count - write);

        _relaxAcc += dt;
        int relaxSteps = (int)(_relaxAcc * 60.0);
        if (relaxSteps > 2) relaxSteps = 2;
        _relaxAcc -= relaxSteps / 60.0;
        if (_relaxAcc > 0.25) _relaxAcc = 0;

        foreach (var drift in _drifts)
        {
            if (Melt || (MeltAfterSeconds > 0 && _time - drift.Born >= MeltAfterSeconds))
            {
                drift.Melt(dt);
            }

            drift.Step(dt);

            if (drift.BrokeOff)
            {
                drift.BreakVelocity(out double vx, out double vy);
                double volume = drift.Volume;
                Spill(drift, volume, 0.8, vx, vy);
                drift.ClearSnow();
            }

            _chunks.Clear();
            drift.TakeChunks(_chunks);
            for (int i = 0; i < _chunks.Count; i++)
            {
                ReleaseChunk(drift, _chunks[i]);
            }

            for (int r = 0; r < relaxSteps; r++)
            {
                drift.Relax();
            }

            drift.TakeSpill(out double spillLeft, out double spillRight);
            if (spillLeft > 1.5) ReleaseEdge(drift, true, spillLeft);
            if (spillRight > 1.5) ReleaseEdge(drift, false, spillRight);
        }
    }

    private Flake NewFlake(ParticlePreset preset, double x, double y)
    {
        var data = ParticlePresetData.Of(preset);
        return new Flake
        {
            X = x,
            Y = y,
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
        double u = _rand.NextDouble();
        var preset = u < 0.62 ? ParticlePreset.Far
            : u < 0.92 ? ParticlePreset.Mid
            : ParticlePreset.Near;

        var f = NewFlake(preset, _rand.NextDouble() * Math.Max(10, Width), 0);
        f.Y = Top - f.R - _rand.NextDouble() * 40;
        f.VX = 0;
        f.VY = f.FallSpeed;
        _flakes.Add(f);
    }

    private bool UpdateFlake(ref Flake f, double dt, double gust)
    {
        double prevBottom = f.Y + f.R;

        double sway = Math.Sin(_time * f.SwaySpeed * 1.2 + f.Phase) * f.SwayAmp;
        
        // УСКОРЕНИЕ: Снег падает быстрее и резче реагирует на ветер
        f.VX *= 1.0 - Math.Min(0.9, dt * 2.0); 
        f.VY += (f.FallSpeed * 1.5 - f.VY) * Math.Min(1.0, dt * 4.0); 

        f.X += (f.VX + sway + Wind + gust) * dt * 1.3;
        f.Y += f.VY * (1.0 + 0.05 * Math.Sin(_time * f.SwaySpeed * 1.7 + f.Phase)) * dt;

        if (f.X < -160 || f.X > Width + 160) return false;

        double newBottom = f.Y + f.R;
        SnowDrift? target = FindLanding(f, prevBottom, newBottom);
        if (target != null)
        {
            double volume = f.Volume > 0 ? f.Volume : Math.PI * f.R * f.R;
            double surface = target.SurfaceTop - target.HeightAtWorld(f.X);
            if (f.Lump)
            {
                target.Add(f.X - target.SurfaceLeft, volume * 0.85);
                Puff(f.X, surface, 0, 0, volume * 0.15);
            }
            else
            {
                target.Add(f.X - target.SurfaceLeft, volume);
            }
            return false;
        }

        if (f.Y > Bottom + 80) return false;

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

        double radius = Math.Sqrt(chunk.Volume / Math.PI);
        if (radius < 1.0) radius = 1.0;
        if (radius > 8.0) radius = 8.0;

        var f = NewFlake(ParticlePreset.Lump,
            chunk.X + (_rand.NextDouble() - 0.5) * 2,
            chunk.Y);
        f.R = radius;
        f.VX = chunk.VX * 0.35 + (_rand.NextDouble() - 0.5) * 25;
        f.VY = chunk.VY * 0.2 + f.FallSpeed * 0.3;
        f.Volume = chunk.Volume;
        f.Lump = true;
        f.SwayAmp = 3 + _rand.NextDouble() * 8;
        f.SwaySpeed = 0.5 + _rand.NextDouble() * 1.2;
        _flakes.Add(f);
    }

    private void ReleaseEdge(SnowDrift drift, bool left, double volume)
    {
        if (volume < 0.5) return;

        double x = left ? drift.SurfaceLeft + 0.5 : drift.SurfaceRight - 0.5;
        double y = drift.SurfaceTop - 0.5;
        int count = (int)Math.Ceiling(volume / 8.0);
        if (count < 1) count = 1;
        if (count > 6) count = 6;
        double piece = volume / count;

        for (int i = 0; i < count; i++)
        {
            double r = Math.Sqrt(piece / Math.PI);
            if (r < 0.6) r = 0.6;
            if (r > 2.6) r = 2.6;

            var f = NewFlake(ParticlePreset.Powder,
                x + (_rand.NextDouble() - 0.5) * 4,
                y - _rand.NextDouble() * 2);
            f.R = r;
            f.VX = (left ? -1 : 1) * (6 + _rand.NextDouble() * 12) + drift.TrackVX * 0.2;
            f.VY = f.FallSpeed * 0.25;
            f.Volume = piece;
            f.SwayAmp = 2 + _rand.NextDouble() * 6;
            f.SwaySpeed = 0.6 + _rand.NextDouble() * 1.2;
            _flakes.Add(f);
        }
    }

    private void Spill(SnowDrift drift, double volume, double inherit, double vx = 0, double vy = 0)
    {
        if (volume <= 0.4) return;

        int count = (int)Math.Ceiling(volume / 40.0);
        if (count < 2) count = 2;
        if (count > 24) count = 24; // Оптимизация: снижено для предотвращения лагов

        double lumpVolume = volume / count;
        double radius = Math.Sqrt(lumpVolume / Math.PI);
        if (radius < 1.2) radius = 1.2;
        if (radius > 9.0) radius = 9.0;

        for (int i = 0; i < count; i++)
        {
            double localX = SampleColumn(drift);
            double x = drift.SurfaceLeft + localX;
            double surface = drift.SurfaceTop - drift.HeightAt(localX);
            double r = radius * (0.75 + _rand.NextDouble() * 0.5);

            var f = NewFlake(ParticlePreset.Lump, x, surface + r * 0.5);
            f.R = r;
            f.VX = (vx + drift.LastVX) * inherit * 0.2 + (_rand.NextDouble() - 0.5) * 40;
            f.VY = f.FallSpeed + vy * inherit * 0.3;
            f.Volume = lumpVolume;
            f.Lump = true;
            f.SwayAmp = 4 + _rand.NextDouble() * 12;
            f.SwaySpeed = 0.5 + _rand.NextDouble() * 1.4;
            _flakes.Add(f);
        }
    }

    private void Puff(double x, double y, double vx, double vy, double volume)
    {
        if (volume <= 0.2) return;

        int count = (int)Math.Ceiling(volume / 8.0);
        if (count < 1) count = 1;
        if (count > 10) count = 10; // Оптимизация: снижено с 24

        double area = volume / count;
        double radius = Math.Sqrt(area / Math.PI);
        if (radius < 0.6) radius = 0.6;
        if (radius > 3.0) radius = 3.0;

        for (int i = 0; i < count; i++)
        {
            double r = radius * (0.7 + _rand.NextDouble() * 0.6);
            double piece = volume / count;

            var f = NewFlake(ParticlePreset.Powder,
                x + (_rand.NextDouble() - 0.5) * 18,
                y - _rand.NextDouble() * 6);
            f.R = r;
            f.VX = vx * 0.12 + (_rand.NextDouble() - 0.5) * 20;
            f.VY = -6 - _rand.NextDouble() * 22;
            f.Volume = piece;
            f.SwayAmp = 2 + _rand.NextDouble() * 6;
            f.SwaySpeed = 0.6 + _rand.NextDouble() * 1.2;
            _flakes.Add(f);
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

    /// <summary>
    /// ОПТИМИЗАЦИЯ И "ПЕРЕМУСОЛИВАНИЕ":
    /// Стирание снега больше не вызывает фризов, так как проверка коллизий 
    /// использует квадраты расстояний (без Math.Sqrt). А из стертого снега 
    /// создается катящийся комок (Lump).
    /// </summary>
    public void Spray(double x, double y, double vx, double vy)
    {
        double speedSq = vx * vx + vy * vy;
        double speed = Math.Sqrt(speedSq);
        double radius = 6 + Math.Min(8, speed * 0.003);
        double radiusSq = radius * radius;

        int count = _flakes.Count;
        for (int i = 0; i < count; i++)
        {
            var f = _flakes[i];
            double dx = f.X - x;
            double dy = f.Y - y;
            
            // Быстрая проверка без Math.Sqrt
            double distSq = dx * dx + dy * dy;
            double sumR = radius + f.R;
            if (distSq >= sumR * sumR) continue;

            double m = Math.Sqrt(distSq);
            if (m < 0.001) m = 0.001;

            double push = 8 + speed * 0.035;
            f.VX += (dx / m) * push + vx * 0.03;
            f.VY += (dy / m) * push * 0.4 - 6;
            _flakes[i] = f;
        }

        SnowDrift? target = null;
        double targetSurface = double.MaxValue;
        double targetDistance = double.MaxValue;

        foreach (var drift in _drifts)
        {
            if (!drift.HasSnow) continue;
            if (x < drift.SurfaceLeft || x > drift.SurfaceRight) continue;

            double top = drift.SurfaceTop;
            double surface = top - drift.HeightAtWorld(x);
            if (y < surface - 8 || y > top + 12) continue;

            double distance = Math.Abs(y - surface);
            if (distance < targetDistance)
            {
                targetDistance = distance;
                targetSurface = surface;
                target = drift;
            }
        }

        if (target == null) return;

        double depth = 0.6 + Math.Min(2.5, speed * 0.0008);
        double removed = target.Shave(x, radius, depth);
        
        if (removed > 0.2)
        {
            Puff(x, targetSurface, vx, vy, removed * 0.3);
            
            // Создаем катящийся комок
            double r = Math.Sqrt(removed / Math.PI);
            if (r < 1.0) r = 1.0;
            if (r > 6.0) r = 6.0;
            
            var lump = NewFlake(ParticlePreset.Lump, x, targetSurface - r);
            lump.R = r;
            lump.VX = vx * 0.3; 
            lump.VY = vy * 0.3 - 2; 
            lump.Volume = removed * 0.7;
            lump.Lump = true;
            lump.SwayAmp = 2;
            lump.SwaySpeed = 0.5;
            _flakes.Add(lump);
        }
    }

    public void Clear()
    {
        _flakes.Clear();
        ResetDrifts();
    }
}