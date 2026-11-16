using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Aerisflow.Updating;

public static class UpdateConfig
{
    public const string GitHubOwner = "OWNER";
    public const string GitHubRepo = "Aerisflow";
    public const string PublicKeySpki = "PASTE_PUBLIC_KEY";
    public const string AppExeName = "Aerisflow.exe";

    public static string RootDir
    {
        get
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string parent = Directory.GetParent(baseDir)?.FullName ?? baseDir;
            return File.Exists(Path.Combine(parent, "current.json")) ? parent : baseDir;
        }
    }
}

public sealed class ManifestFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";
}

public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("rollout_percent")] public int RolloutPercent { get; set; } = 100;
    [JsonPropertyName("release_notes")] public string ReleaseNotes { get; set; } = "";
    [JsonPropertyName("entry")] public string Entry { get; set; } = "Aerisflow.exe";
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
}

public sealed class CurrentState
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("dir")] public string Dir { get; set; } = "";
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

public sealed class UpdateInfo
{
    public UpdateManifest Manifest { get; init; } = new();
    public IReadOnlyDictionary<string, string> AssetUrls { get; init; } = new Dictionary<string, string>();
}

public static class UpdateCrypto
{
    public static bool VerifyManifest(byte[] data, byte[] sig)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateConfig.PublicKeySpki), out _);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static int DeviceBucket(string deviceId)
    {
        using var sha = SHA256.Create();
        byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
        return (int)(BitConverter.ToUInt32(h, 0) % 100);
    }
}

public sealed class UpdateService
{
    private readonly HttpClient _http = new();
    public string Channel { get; set; } = "stable";

    public string Root => UpdateConfig.RootDir;
    public string CurrentAppDir => Path.Combine(Root, ReadCurrent()?.Dir ?? "");

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Aerisflow-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string DeviceId()
    {
        string p = Path.Combine(Root, "device.json");
        if (File.Exists(p))
        {
            try { return JsonSerializer.Deserialize<DeviceDoc>(File.ReadAllText(p))?.id ?? NewId(p); }
            catch { return NewId(p); }
        }
        return NewId(p);
    }

    private string NewId(string p)
    {
        string id = Guid.NewGuid().ToString("N");
        File.WriteAllText(p, JsonSerializer.Serialize(new DeviceDoc { id = id }));
        return id;
    }

    private sealed class DeviceDoc { public string id { get; set; } = ""; }

    public CurrentState? ReadCurrent()
    {
        string p = Path.Combine(Root, "current.json");
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<CurrentState>(File.ReadAllText(p)); }
        catch { return null; }
    }

    public void WriteCurrent(CurrentState s)
    {
        string p = Path.Combine(Root, "current.json");
        string tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, p, true);
    }

    public async Task<UpdateInfo?> CheckAsync()
    {
        var releases = await GetReleasesAsync();
        if (releases == null) return null;

        var rel = Channel == "beta"
            ? releases.FirstOrDefault(r => r.prerelease)
            : releases.FirstOrDefault(r => !r.prerelease && !r.draft);
        if (rel == null) return null;

        var assets = rel.assets.ToDictionary(a => a.name, a => a.browser_download_url);
        if (!assets.TryGetValue("manifest.json", out var mUrl) || !assets.TryGetValue("manifest.sig", out var sUrl))
            return null;

        byte[] mBytes = await _http.GetByteArrayAsync(mUrl);
        byte[] sBytes = await _http.GetByteArrayAsync(sUrl);
        if (!UpdateCrypto.VerifyManifest(mBytes, sBytes)) return null;

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(mBytes);
        if (manifest == null) return null;

        var cur = ReadCurrent();
        if (cur != null && CompareVersion(manifest.Version, cur.Version) <= 0) return null;

        int bucket = UpdateCrypto.DeviceBucket(DeviceId());
        if (Channel != "beta" && bucket >= manifest.RolloutPercent) return null;

        return new UpdateInfo { Manifest = manifest, AssetUrls = assets };
    }

    private async Task<List<ReleaseDoc>?> GetReleasesAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{UpdateConfig.GitHubOwner}/{UpdateConfig.GitHubRepo}/releases?per_page=20";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<List<ReleaseDoc>>(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private sealed class ReleaseDoc
    {
        public bool prerelease { get; set; }
        public bool draft { get; set; }
        public List<AssetDoc> assets { get; set; } = new();
    }
    private sealed class AssetDoc
    {
        public string name { get; set; } = "";
        public string browser_download_url { get; set; } = "";
    }

    public static int CompareVersion(string a, string b)
    {
        var pa = a.Split('.').Select(int.Parse).ToArray();
        var pb = b.Split('.').Select(int.Parse).ToArray();
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public async Task<string> StageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        string target = Path.Combine(Root, "app-" + info.Manifest.Version);
        Directory.CreateDirectory(target);
        string source = CurrentAppDir;

        int done = 0;
        foreach (var f in info.Manifest.Files)
        {
            string dst = Path.Combine(target, f.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            string src = Path.Combine(source, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(src) && UpdateCrypto.Sha256File(src) == f.Sha256)
            {
                File.Copy(src, dst, true);
            }
            else
            {
                if (!info.AssetUrls.TryGetValue(f.Asset, out var url))
                    throw new InvalidOperationException("asset missing: " + f.Asset);
                string tmp = dst + ".dl";
                await using (var fs = File.Create(tmp))
                await using (var stream = await _http.GetStreamAsync(url))
                    await stream.CopyToAsync(fs);
                if (UpdateCrypto.Sha256File(tmp) != f.Sha256)
                {
                    File.Delete(tmp);
                    throw new InvalidOperationException("hash mismatch: " + f.Path);
                }
                File.Move(tmp, dst, true);
            }
            done++;
            progress?.Report((double)done / info.Manifest.Files.Count);
        }
        return target;
    }

    public void ApplyStaged(string stagedDir)
    {
        var cur = ReadCurrent();
        WriteCurrent(new CurrentState
        {
            Version = Path.GetFileName(stagedDir).Replace("app-", ""),
            Dir = Path.GetFileName(stagedDir),
            Previous = cur?.Dir,
        });
    }

    public void MarkHealthy()
    {
        try
        {
            string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            File.WriteAllText(Path.Combine(dir, "healthy.ok"), DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }
}

public static class UpdateManager
{
    public static UpdateService Service { get; } = new();
    public static event Action<UpdateInfo>? UpdateAvailable;
    public static event Action<double>? StageProgress;
    public static UpdateInfo? Pending { get; private set; }
    public static string? StagedDir { get; private set; }
    public static bool Deferred { get; private set; }

    private static System.Windows.Threading.DispatcherTimer? _timer;

    public static void Attach(Application app)
    {
        Service.Channel = AppSettings.Load().Channel;
        Service.MarkHealthy();
        ApplyAutoStart(AppSettings.Load().AutoStart);

        SystemEvents.SessionEnding += (s, e) =>
        {
            if (StagedDir != null)
            {
                try { Service.ApplyStaged(StagedDir); } catch { }
            }
        };

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += async (s, e) => await CheckNowAsync();
        _timer.Start();
        _ = CheckNowAsync();
    }

    public static async Task CheckNowAsync()
    {
        var info = await Service.CheckAsync();
        if (info != null)
        {
            Pending = info;
            UpdateAvailable?.Invoke(info);
        }
    }

    public static async Task StagePendingAsync(IProgress<double>? p = null)
    {
        if (Pending == null) return;
        var progress = new Progress<double>(v => { p?.Report(v); StageProgress?.Invoke(v); });
        StagedDir = await Service.StageAsync(Pending, progress);
    }

    public static void MarkDeferred() => Deferred = true;

    public static void RestartWithUpdate()
    {
        if (StagedDir != null) Service.ApplyStaged(StagedDir);
        RestartApp();
    }

    public static async Task RestartWithVeilAsync()
    {
        var veil = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(0, 10, 14, 24)),
            Topmost = true,
            ShowInTaskbar = false,
            WindowState = WindowState.Maximized,
        };
        veil.Show();
        var anim = new ColorAnimation(Color.FromArgb(235, 10, 14, 24), TimeSpan.FromSeconds(0.9))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        ((SolidColorBrush)veil.Background).BeginAnimation(SolidColorBrush.ColorProperty, anim);
        await Task.Delay(950);
        RestartWithUpdate();
        veil.Close();
    }

    public static void RestartApp()
    {
        string launcher = Path.Combine(UpdateConfig.RootDir, "Launcher.exe");
        if (File.Exists(launcher))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    public static void ApplyAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on) key.SetValue("Aerisflow", "\"" + Path.Combine(UpdateConfig.RootDir, "Launcher.exe") + "\"");
            else key.DeleteValue("Aerisflow", false);
        }
        catch { }
    }
}

public sealed class AppSettings
{
    public string Channel { get; set; } = "stable";
    public bool AutoStart { get; set; } = true;
    public double Intensity { get; set; } = 0.6;
    public double Opacity { get; set; } = 1.0;
    public bool Melt { get; set; }
    public double Wind { get; set; }
    public double Speed { get; set; } = 1.0;
    public int TargetFps { get; set; } = 60;
    public double MeltAfterSeconds { get; set; }
    public bool FullscreenOn { get; set; } = true;
    public int ModeIndex { get; set; }

    public static string PathFile => System.IO.Path.Combine(UpdateConfig.RootDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}