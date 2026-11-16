using System;

namespace Aerisflow;

/// <summary>Which preset a particle was spawned from. The presets keep the
/// simulation data-only: adding or removing a flake never allocates an object.</summary>
public enum ParticlePreset : byte
{
    Far,
    Mid,
    Near,
    Powder,
    Lump,
}

/// <summary>The tunable shape of a particle preset. Spawning reads a preset and
/// writes a flat <see cref="Flake"/> struct, so the hot path allocates nothing.</summary>
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
        _ => new ParticlePresetData(1.0, 8.0, 34, 78, 0.55, 0.9, 2, 10, 0.25, 0.8),
    };

    public double Radius(Random random) => RadiusMin + random.NextDouble() * (RadiusMax - RadiusMin);
    public double Fall(Random random) => FallMin + random.NextDouble() * (FallMax - FallMin);
    public double Alpha(Random random) => AlphaMin + random.NextDouble() * (AlphaMax - AlphaMin);
    public double Sway(Random random) => SwayMin + random.NextDouble() * (SwayMax - SwayMin);
    public double SwaySpeed(Random random) => SwaySpeedMin + random.NextDouble() * (SwaySpeedMax - SwaySpeedMin);
}

/// <summary>A single falling particle in the simulation. Coordinates are in
/// device-independent pixels (1 unit = 1 DIP) of the overlay. This is a struct so
/// the particle list is one flat, reusable array with no per-flake allocations.</summary>
public struct Flake
{
    public double X;
    public double Y;          // vertical center
    public double R;          // radius (DIP)
    public double VX;         // horizontal velocity (px/s, + = right)
    public double VY;         // current vertical velocity (px/s, + = down)
    public double FallSpeed;  // terminal fall speed the flake eases back to
    public double Alpha;      // 0..1, big clumps are brighter than specks
    public double Volume;     // drift volume carried by a falling lump (0 = flake's circle)
    public bool Lump;         // heavy chunk that bursts into powder when it lands
    public double Phase;      // sway phase
    public double SwayAmp;    // sway amplitude
    public double SwaySpeed;  // sway speed
    public ParticlePreset Preset;
}
