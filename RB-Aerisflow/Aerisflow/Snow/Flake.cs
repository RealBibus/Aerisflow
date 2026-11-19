using System;

namespace Aerisflow;

public enum ParticlePreset : byte
{
    Far,
    Mid,
    Near,
    Powder,
    Lump,
    Blizzard,
}

public readonly struct ParticlePresetData
{
    public readonly double RadiusMin;
    public readonly double RadiusMax;
    public readonly double FallMin;
    public readonly double FallMax;
    public readonly double AlphaMin;
    public readonly double AlphaMax;
    public readonly double SwayMin;
    public readonly double SwayMax;
    public readonly double SwaySpeedMin;
    public readonly double SwaySpeedMax;

    private ParticlePresetData(
    double radiusMin, double radiusMax,
    double fallMin, double fallMax,
    double alphaMin, double alphaMax,
    double swayMin, double swayMax,
    double swaySpeedMin, double swaySpeedMax)
    {
        RadiusMin = radiusMin;
        RadiusMax = radiusMax;
        FallMin = fallMin;
        FallMax = fallMax;
        AlphaMin = alphaMin;
        AlphaMax = alphaMax;
        SwayMin = swayMin;
        SwayMax = swayMax;
        SwaySpeedMin = swaySpeedMin;
        SwaySpeedMax = swaySpeedMax;
    }

    public static ParticlePresetData Of(ParticlePreset preset) => preset switch
    {
        ParticlePreset.Far => new ParticlePresetData(0.6, 1.6, 20, 48, 0.30, 0.48, 4, 16, 0.25, 1.1),
        ParticlePreset.Mid => new ParticlePresetData(1.6, 2.6, 32, 66, 0.42, 0.66, 5, 18, 0.3, 1.0),
        ParticlePreset.Near => new ParticlePresetData(2.6, 4.0, 46, 88, 0.56, 0.82, 6, 20, 0.2, 0.8),
        ParticlePreset.Powder => new ParticlePresetData(0.6, 2.4, 28, 64, 0.46, 0.74, 2, 8, 0.3, 0.9),
        ParticlePreset.Blizzard => new ParticlePresetData(0.35, 1.25, 26, 58, 0.30, 0.58, 8, 26, 0.35, 1.5),
        _ => new ParticlePresetData(1.0, 8.0, 34, 78, 0.55, 0.9, 2, 10, 0.25, 0.8),
    };

    public double Radius(Random random) => RadiusMin + random.NextDouble() * (RadiusMax - RadiusMin);
    public double Fall(Random random) => FallMin + random.NextDouble() * (FallMax - FallMin);
    public double Alpha(Random random) => AlphaMin + random.NextDouble() * (AlphaMax - AlphaMin);
    public double Sway(Random random) => SwayMin + random.NextDouble() * (SwayMax - SwayMin);
    public double SwaySpeed(Random random) => SwaySpeedMin + random.NextDouble() * (SwaySpeedMax - SwaySpeedMin);
}

public struct Flake
{
    public double X;
    public double Y;
    public double PrevX;
    public double PrevY;
    public double R;
    public double VX;
    public double VY;
    public double FallSpeed;
    public double Alpha;
    public double Volume;
    public bool Lump;
    public double Phase;
    public double SwayAmp;
    public double SwaySpeed;
    public double LandDelay;
    public double MoteLife;
    public IntPtr IgnoreOwner;
    public ParticlePreset Preset;
    public bool Smoke;
    public double SmokeLife;
    public double SmokeBaseAlpha;
    public double SpawnY;
    public double Wet;
}