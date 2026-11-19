using System;
using System.Collections.Generic;
using System.Numerics;

namespace Aerisflow;

public sealed class FrostBranch
{
    public Vector2[] Points = Array.Empty<Vector2>();
    public float[] Cumulative = Array.Empty<float>();
    public float Length;
    public int Shape;
    public float Head;
    public float Fade;
    public float Health = 1f;
    public float MinX, MinY, MaxX, MaxY;
    internal float Delay;
    internal float Duration;
}

public struct ScrapeSpark
{
    public double X, Y, VX, VY, Life, MaxLife, R;
}

public sealed class FrostField
{
    private sealed class Cluster
    {
        public readonly List<FrostBranch> Branches = new List<FrostBranch>();
        public int Edge;
        public float Slot;
        public float Age;
        public float Span;
        public float Hold;
        public float Melt;
        public bool Forced;
    }

    private readonly List<Cluster> _clusters = new List<Cluster>();
    private readonly List<FrostBranch> _branches = new List<FrostBranch>();
    private readonly Random _random = new Random(unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode()));
    private bool _dirty = true;
    private int _shape;
    private double _shatterAcc;

    public double Width { get; private set; }
    public double Height { get; private set; }

    public IReadOnlyList<FrostBranch> Branches
    {
        get
        {
            if (_dirty) Flatten();
            return _branches;
        }
    }

    public bool Matches(double width, double height)
    => Math.Abs(Width - width) < 0.5 && Math.Abs(Height - height) < 0.5;

    public void Build(double width, double height)
    {
        Width = width;
        Height = height;
        _clusters.Clear();
        _branches.Clear();
        _dirty = true;
        _shatterAcc = 0;
        if (width < 60 || height < 60) return;

        double shortest = Math.Min(width, height);
        int across = EdgeCount(width);
        int down = EdgeCount(height);

        for (int i = 0; i < across; i++) Add(0, (i + 0.5) / across, shortest);
        for (int i = 0; i < across; i++) Add(1, (i + 0.5) / across, shortest);
        for (int i = 0; i < down; i++) Add(2, (i + 0.5) / down, shortest);
        for (int i = 0; i < down; i++) Add(3, (i + 0.5) / down, shortest);
        for (int corner = 0; corner < 4; corner++)
        {
            Add(4 + corner, 0.25, shortest);
            Add(4 + corner, 0.75, shortest);
        }
    }

    private void Add(int edge, double slot, double shortest)
    {
        var cluster = new Cluster { Edge = edge, Slot = (float)slot };
        Regenerate(cluster, shortest);
        cluster.Age = -(float)(_random.NextDouble() * 45.0);
        _clusters.Add(cluster);
        _dirty = true;
    }

    public void Update(double dt) => Update(dt, 0, null);

    public void Update(double dt, double kick, List<ScrapeSpark>? sparks)
    {
        if (dt <= 0 || _clusters.Count == 0) return;
        double shortest = Math.Min(Width, Height);

        for (int i = 0; i < _clusters.Count; i++)
        {
            var c = _clusters[i];
            c.Age += (float)dt;
            float meltStart = c.Span + c.Hold;

            if (c.Age >= meltStart + c.Melt)
            {
                Regenerate(c, shortest);
                c.Age = -(float)(2.0 + _random.NextDouble() * 12.0);
                _dirty = true;
                continue;
            }

            float u = c.Age > meltStart ? Math.Clamp((c.Age - meltStart) / c.Melt, 0f, 1f) : 0f;
            float melt = u * u * (3f - 2f * u);
            int dead = 0;

            for (int b = 0; b < c.Branches.Count; b++)
            {
                var branch = c.Branches[b];
                float g = c.Age <= 0 ? 0f : Math.Clamp((c.Age - branch.Delay) / branch.Duration, 0f, 1f);
                float reveal = MathF.Pow(g, 0.85f);
                branch.Head = reveal * (1f - 0.4f * melt);
                branch.Fade = c.Age <= 0 ? 0f : 1f - melt * melt;

                if (branch.Health > 0.35f) branch.Health = Math.Min(1f, branch.Health + (float)dt * 0.05f);
                else if (branch.Health > 0f) branch.Health = Math.Max(0f, branch.Health - (float)dt * 0.35f);

                if (branch.Health <= 0.02f) dead++;
            }

            if (!c.Forced && c.Branches.Count > 0 && dead >= Math.Max(2, (int)(c.Branches.Count * 0.4)))
            {
                c.Forced = true;
                c.Age = Math.Max(c.Age, meltStart);
                c.Melt = Math.Min(c.Melt, 4f);
            }
        }

        if (sparks != null && kick > 0.3)
        {
            _shatterAcc += (kick - 0.3) * dt * 60.0;
            if (_shatterAcc > 4) _shatterAcc = 4;
            while (_shatterAcc >= 1.0)
            {
                _shatterAcc -= 1.0;
                ShatterWave(kick, sparks);
            }
        }
    }

    private void ShatterWave(double kick, List<ScrapeSpark> sparks)
    {
        var branches = Branches;
        if (branches.Count == 0) return;

        int toBreak = kick > 0.7
        ? branches.Count
        : Math.Max(4, (int)(branches.Count * 0.3));

        int broken = 0;
        for (int attempt = 0; attempt < toBreak * 3 && broken < toBreak && sparks.Count < 400; attempt++)
        {
            var b = branches[_random.Next(branches.Count)];
            if (b.Health <= 0.1f || b.Head <= 0.2f || b.Points.Length < 2) continue;
            b.Health = (float)Math.Max(0.0, b.Health - (0.5 + 0.5 * kick));
            EmitSparksOn(b, kick, sparks);
            broken++;
        }
    }

    private void EmitSparksOn(FrostBranch b, double kick, List<ScrapeSpark> sparks)
    {
        float reach = b.Length * b.Head;
        int emitted = 0;
        for (int s = 0; s + 1 < b.Points.Length && emitted < 4 && sparks.Count < 400; s++)
        {
            if (b.Cumulative[s] > reach) break;
            if (_random.NextDouble() > 0.5) continue;
            var p = b.Points[s];
            double ang = (_random.NextDouble() - 0.5) * 2.0;
            double sp = 40 + _random.NextDouble() * 120 * kick;
            sparks.Add(new ScrapeSpark
            {
                X = p.X,
                Y = p.Y,
                VX = Math.Cos(ang) * sp,
                VY = -40 - _random.NextDouble() * 120 * kick,
                Life = 0.3 + _random.NextDouble() * 0.4,
                MaxLife = 0.7,
                R = 0.7 + _random.NextDouble() * 1.3,
            });
            emitted++;
        }
    }

    public void Scrape(double x, double y, double radius, double damage,
    double vx, double vy, List<ScrapeSpark> sparks)
    {
        if (radius <= 0 || damage <= 0) return;
        float fx = (float)x, fy = (float)y;
        var branches = Branches;

        for (int i = 0; i < branches.Count && sparks.Count < 300; i++)
        {
            var b = branches[i];
            if (b.Health <= 0.01f || b.Head <= 0.02f) continue;
            if (fx < b.MinX - radius || fx > b.MaxX + radius ||
            fy < b.MinY - radius || fy > b.MaxY + radius) continue;

            float reach = b.Length * b.Head;
            var pts = b.Points;
            for (int s = 0; s + 1 < pts.Length; s++)
            {
                if (b.Cumulative[s] > reach) break;
                ClosestOnSegment(pts[s], pts[s + 1], new Vector2(fx, fy), out var c, out double dist);
                if (dist > radius) continue;
                double falloff = 1.0 - dist / radius;
                b.Health = (float)Math.Max(0.0, b.Health - damage * falloff);
                if (sparks.Count < 300 && falloff > 0.25)
                {
                    double ang = (_random.NextDouble() - 0.5) * 1.2;
                    double sp = 30 + _random.NextDouble() * 50;
                    sparks.Add(new ScrapeSpark
                    {
                        X = c.X,
                        Y = c.Y,
                        VX = vx * 0.18 + Math.Cos(ang) * sp,
                        VY = vy * 0.18 - 20 - _random.NextDouble() * 30,
                        Life = 0.4 + _random.NextDouble() * 0.35,
                        MaxLife = 0.75,
                        R = 0.8 + _random.NextDouble() * 1.1,
                    });
                }
            }
        }
    }

    private void Flatten()
    {
        _branches.Clear();
        for (int i = 0; i < _clusters.Count; i++)
        {
            var list = _clusters[i].Branches;
            for (int b = 0; b < list.Count; b++) _branches.Add(list[b]);
        }
        _dirty = false;
    }

    private void Regenerate(Cluster c, double shortest)
    {
        c.Branches.Clear();
        c.Forced = false;

        Vector2 origin, direction;
        float length;
        int depth;

        if (c.Edge >= 4)
        {
            int corner = c.Edge - 4;
            float cx = (corner == 1 || corner == 3) ? (float)Width : 0f;
            float cy = corner >= 2 ? (float)Height : 0f;
            var inward = Vector2.Normalize(new Vector2(cx == 0 ? 1f : -1f, cy == 0 ? 1f : -1f));
            direction = Rotate(inward, (_random.NextDouble() - 0.5) * 1.15);
            origin = new Vector2(cx, cy) + direction * (float)(shortest * (0.01 + _random.NextDouble() * 0.035));
            length = (float)Math.Clamp(shortest * (0.045 + _random.NextDouble() * 0.13), 20.0, 190.0);
            depth = 3;
        }
        else
        {
            double t = Math.Clamp(c.Slot + (_random.NextDouble() - 0.5) * 0.07, 0.02, 0.98);
            Vector2 inward, along;
            switch (c.Edge)
            {
                case 0:
                origin = new Vector2((float)(t * Width), 0f);
                inward = new Vector2(0f, 1f);
                along = new Vector2(1f, 0f);
                break;
                case 1:
                origin = new Vector2((float)(t * Width), (float)Height);
                inward = new Vector2(0f, -1f);
                along = new Vector2(1f, 0f);
                break;
                case 2:
                origin = new Vector2(0f, (float)(t * Height));
                inward = new Vector2(1f, 0f);
                along = new Vector2(0f, 1f);
                break;
                default:
                origin = new Vector2((float)Width, (float)(t * Height));
                inward = new Vector2(-1f, 0f);
                along = new Vector2(0f, 1f);
                break;
            }
            double lean = (_random.NextDouble() - 0.5) * 0.9;
            direction = Vector2.Normalize(inward + along * (float)lean);
            length = (float)Math.Clamp(shortest * (0.035 + _random.NextDouble() * 0.12), 16.0, 170.0);
            depth = _random.NextDouble() < 0.3 ? 3 : 2;
        }

        float speed = (float)(13.0 + _random.NextDouble() * 14.0);
        Grow(c, origin, direction, length, depth, 0f, speed, 0);

        float span = 0f;
        for (int i = 0; i < c.Branches.Count; i++)
        {
            float end = c.Branches[i].Delay + c.Branches[i].Duration;
            if (end > span) span = end;
        }
        c.Span = Math.Max(1f, span);
        c.Hold = (float)(25.0 + _random.NextDouble() * 45.0);
        c.Melt = (float)(14.0 + _random.NextDouble() * 20.0);
        _dirty = true;
    }

    private void Grow(Cluster c, Vector2 start, Vector2 direction, float length,
    int depth, float delay, float speed, int recursion)
    {
        if (recursion > 4 || length < 4f) return;

        int segments = 3 + _random.Next(3);
        float step = length / segments;
        float duration = Math.Max(0.6f, length / speed);
        var points = new Vector2[segments + 1];
        var cumulative = new float[segments + 1];
        points[0] = start;

        Vector2 point = start;
        Vector2 heading = Vector2.Normalize(direction);
        float total = 0f;

        for (int i = 0; i < segments; i++)
        {
            heading = Rotate(heading, (_random.NextDouble() - 0.5) * 0.55);
            point += heading * step;
            total += step;
            points[i + 1] = point;
            cumulative[i + 1] = total;

            if (depth > 0 && i >= 1 && _random.NextDouble() < 0.75)
            {
                double side = (_random.NextDouble() < 0.5 ? -1.0 : 1.0) * (0.42 + _random.NextDouble() * 0.55);
                float childDelay = delay + duration * (cumulative[i + 1] / total);
                Grow(c, point, Rotate(heading, side), length * (float)(0.32 + _random.NextDouble() * 0.38),
                depth - 1, childDelay, speed * 0.92f, recursion + 1);
            }
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i].X < minX) minX = points[i].X;
            if (points[i].Y < minY) minY = points[i].Y;
            if (points[i].X > maxX) maxX = points[i].X;
            if (points[i].Y > maxY) maxY = points[i].Y;
        }

        c.Branches.Add(new FrostBranch
        {
            Points = points,
            Cumulative = cumulative,
            Length = total,
            Shape = ++_shape,
            Head = 0f,
            Fade = 0f,
            Health = 1f,
            Delay = delay,
            Duration = duration,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
        });
    }

    private static int EdgeCount(double length) => (int)Math.Clamp(length / 150.0, 4, 12);

    private static void ClosestOnSegment(Vector2 a, Vector2 b, Vector2 p, out Vector2 closest, out double dist)
    {
        Vector2 ab = b - a;
        float len2 = Vector2.Dot(ab, ab);
        if (len2 < 1e-6f)
        {
            closest = a;
            dist = Vector2.Distance(p, a);
            return;
        }
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        closest = a + ab * t;
        dist = Vector2.Distance(p, closest);
    }

    private static Vector2 Rotate(Vector2 vector, double radians)
    {
        float cos = (float)Math.Cos(radians);
        float sin = (float)Math.Sin(radians);
        return new Vector2(vector.X * cos - vector.Y * sin, vector.X * sin + vector.Y * cos);
    }
}