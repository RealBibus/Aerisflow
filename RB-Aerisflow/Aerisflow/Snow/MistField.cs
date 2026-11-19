using System;

namespace Aerisflow;

public sealed class MistField
{
    public struct Blob
    {
        public float X, Y, RX, RY, VX, VY, Phase, Wob, Alpha, Presence, Evap;
    }

    private const int Capacity = 34;
    private readonly Blob[] _blobs = new Blob[Capacity];
    private readonly Random _random = new Random(unchecked(Environment.TickCount * 31 ^ Guid.NewGuid().GetHashCode()));
    private double _time;
    private double _width;
    private double _height;
    private bool _built;

    public Blob[] Blobs => _blobs;
    public double Level { get; private set; }
    public double Density { get; private set; } = 1.0;
    public double SizeScale { get; private set; } = 1.0;
    public double Time => _time;

    public bool Matches(double width, double height)
    => _built && Math.Abs(_width - width) < 0.5 && Math.Abs(_height - height) < 0.5;

    public void Build(double width, double height)
    {
        if (width < 50 || height < 50) return;
        _width = width;
        _height = height;
        _built = true;
        for (int i = 0; i < Capacity; i++) _blobs[i] = Create();
    }

    private Blob Create()
    {
        return new Blob
        {
            X = (float)((_random.NextDouble() * 1.4 - 0.2) * _width),
            Y = (float)(_random.NextDouble() * _height),
            RX = (float)(_width * (0.45 + _random.NextDouble() * 0.5)),
            RY = (float)(_height * (0.30 + _random.NextDouble() * 0.3)),
            VX = (float)(6.0 + _random.NextDouble() * 22.0),
            VY = (float)(-4.0 + _random.NextDouble() * 8.0),
            Phase = (float)(_random.NextDouble() * Math.PI * 2.0),
            Wob = (float)(_height * (0.012 + _random.NextDouble() * 0.03)),
            Alpha = (float)(0.026 + _random.NextDouble() * 0.030),
            Presence = 0f,
            Evap = 1f,
        };
    }

    public void Update(double dt, double width, double height, double wind,
    double intensity, bool storm, bool enabled, double bass)
    {
        if (dt <= 0) return;
        if (!Matches(width, height)) Build(width, height);
        if (!_built) return;

        _time += dt;
        double want = enabled ? 1.0 : 0.0;
        Level += (want - Level) * (1.0 - Math.Exp(-dt / 6.0));
        Density += ((storm ? 0.85 : 1.0) - Density) * (1.0 - Math.Exp(-dt / 4.0));
        SizeScale += ((storm ? 0.8 : 1.0) - SizeScale) * (1.0 - Math.Exp(-dt / 4.0));
        if (Level <= 0.001) return;

        int active = storm
        ? (int)Math.Clamp(16 + intensity * 4.0, 16, Capacity)
        : (int)Math.Clamp(4 + intensity * 2.2, 3, 10);

        float presenceStep = (float)(1.0 - Math.Exp(-dt / 5.0));
        float lift = (float)(bass * 10.0);

        for (int i = 0; i < Capacity; i++)
        {
            ref var b = ref _blobs[i];
            float target = i < active ? 1f : 0f;
            b.Presence += (target - b.Presence) * presenceStep;
            if (b.Presence <= 0.002f && target == 0f) continue;

            b.X += (float)((b.VX + wind * 0.35) * dt);
            b.Y += (float)(b.VY * dt) - lift * (float)dt;
            if (b.Evap < 1f) b.Evap = Math.Min(1f, b.Evap + (float)(dt * 0.09));

            float padX = b.RX * 1.25f;
            float padY = b.RY * 1.25f;
            if (b.X - padX > width)
            {
                b.X = -padX;
                b.Y = (float)(_random.NextDouble() * height);
            }
            else if (b.X + padX < 0)
            {
                b.X = (float)(width + padX);
                b.Y = (float)(_random.NextDouble() * height);
            }
            if (b.Y - padY > height) b.Y = -padY;
            else if (b.Y + padY < 0) b.Y = (float)(height + padY);
        }
    }

    public void Scrape(double x, double y, double vx, double vy)
    {
        if (!_built || Level <= 0.001) return;
        double speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed < 90.0) return;

        double loss = Math.Clamp((speed - 90.0) / 1400.0, 0, 0.35);
        const double radius = 260.0;
        float fx = (float)x, fy = (float)y;

        for (int i = 0; i < Capacity; i++)
        {
            ref var b = ref _blobs[i];
            if (b.Presence <= 0.01f || b.Evap <= 0.01f) continue;
            double dx = fx - b.X, dy = fy - b.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist >= radius) continue;
            double falloff = 1.0 - dist / radius;
            double scaled = loss * falloff * (0.4 + 0.6 * b.Presence);
            b.Evap = (float)Math.Max(0.0, b.Evap - scaled);
        }
    }
}