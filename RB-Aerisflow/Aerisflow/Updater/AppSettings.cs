using System.IO;
using System.Text.Json;

namespace Aerisflow.Updating;

public sealed class AppSettings
{
    public string Channel { get; set; } = "stable";
    public bool AutoStart { get; set; } = true;
    public double Intensity { get; set; } = 0.6;
    public double Opacity { get; set; } = 1.0;
    public bool Melt { get; set; }
    public double Wind { get; set; }
    public double WindSwing { get; set; } = 0.35;
    public double BassReaction { get; set; } = 0.6;
    public double BassSensitivity { get; set; } = 1.0;
    public bool Mist { get; set; } = true;
    public double Speed { get; set; } = 1.0;
    public int TargetFps { get; set; } = 60;
    public double MeltAfterSeconds { get; set; }
    public bool FullscreenOn { get; set; } = true;
    public bool Frost { get; set; } = true;
    public int ModeIndex { get; set; }

    public double Wetness { get; set; } = 0;
    public double SnowHue { get; set; } = 210;
    public double SnowSat { get; set; } = 0.45;
    public bool SeparateRainColor { get; set; }
    public double RainHue { get; set; } = 215;
    public double RainSat { get; set; } = 0.5;
    public double MistHue { get; set; } = 210;
    public double MistSat { get; set; } = 0.3;

    public static string PathFile => Path.Combine(UpdateConfig.RootDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string tmp = PathFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PathFile, true);
        }
        catch { }
    }
}