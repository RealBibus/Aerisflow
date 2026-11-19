using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32;

namespace Aerisflow.Installer;

public sealed record InstallOptions(
bool StartWithWindows,
bool DesktopShortcut,
bool StartMenuShortcut,
bool LaunchAfterInstall);

public readonly record struct InstallProgress(double Fraction, string Message);

internal sealed class CurrentState
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("dir")] public string Dir { get; set; } = "";
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

public sealed class InstallEngine
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath =
    @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppKeyPath = @"Software\Aerisflow";
    private const string UninstallKeyPath =
    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Aerisflow";
    private const string RunValueName = "Aerisflow";

    public string InstallDir { get; private set; } = DefaultInstallDir;
    public string PayloadVersion { get; }

    public static string DefaultInstallDir => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aerisflow");

    public InstallEngine()
    {
        InstallDir = ResolveInstallDir();
        PayloadVersion = ResolvePayloadVersion();
    }

    public bool IsInstalled
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(InstallDir, "current.json"))
                || File.Exists(Path.Combine(InstallDir, "Aerisflow.exe"))
                || (Directory.Exists(InstallDir) && Directory.GetDirectories(InstallDir, "app-*").Length > 0);
            }
            catch
            {
                return false;
            }
        }
    }

    public string InstalledVersion
    {
        get
        {
            try
            {
                var current = ReadCurrent();
                if (!string.IsNullOrWhiteSpace(current?.Version)) return current.Version;
                using var key = Registry.CurrentUser.OpenSubKey(AppKeyPath);
                return key?.GetValue("Version") as string ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    private static string ResolveInstallDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKeyPath);
            string? saved = key?.GetValue("InstallDir") as string;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved)) return saved;
        }
        catch { }
        return DefaultInstallDir;
    }

    private static string ResolvePayloadVersion()
    {
        try
        {
            using Stream? zip = OpenPayloadZip();
            if (zip != null)
            {
                using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
                var current = archive.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, "current.json", StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    using var reader = new StreamReader(current.Open(), Encoding.UTF8);
                    using var document = JsonDocument.Parse(reader.ReadToEnd());
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("version", out var value)
                    && value.ValueKind == JsonValueKind.String)
                    {
                        string version = Normalize(value.GetString());
                        if (!string.IsNullOrWhiteSpace(version)) return version;
                    }
                }
                foreach (var entry in archive.Entries)
                {
                    string first = entry.FullName.Split('/')[0];
                    if (!first.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) continue;
                    string version = Normalize(first.Substring(4));
                    if (!string.IsNullOrWhiteSpace(version)) return version;
                }
            }

            string folder = Path.Combine(AppContext.BaseDirectory, "payload");
            if (Directory.Exists(folder))
            {
                string path = Path.Combine(folder, "current.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("version", out var value))
                    {
                        string version = Normalize(value.GetString());
                        if (!string.IsNullOrWhiteSpace(version)) return version;
                    }
                }
                foreach (string dir in Directory.GetDirectories(folder, "app-*"))
                {
                    string version = Normalize(Path.GetFileName(dir).Substring(4));
                    if (!string.IsNullOrWhiteSpace(version)) return version;
                }
            }
        }
        catch { }

        return AssemblyVersion();
    }

    private static string AssemblyVersion()
    {
        var assembly = typeof(InstallEngine).Assembly;
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return Normalize(plus > 0 ? info.Substring(0, plus) : info);
        }
        return Normalize(assembly.GetName().Version?.ToString(3));
    }

    private static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        string text = version.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut > 0) text = text.Substring(0, cut);
        return text;
    }

    public void Install(InstallOptions options, IProgress<InstallProgress>? progress = null)
    {
        Report(progress, 0.01, "Preparing…");
        InstallDir = ResolveInstallDir();
        CloseRunningApp();

        string temp = Path.Combine(Path.GetTempPath(), "AerisflowSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            PreserveUserFiles(temp);
            Report(progress, 0.06, "Clearing the previous version…");
            ClearInstallDir();
            Directory.CreateDirectory(InstallDir);

            Report(progress, 0.10, "Installing files…");
            if (!ExtractPayload(progress))
            throw new InvalidOperationException(
            "The installer payload is missing. Rebuild AerisflowSetup.exe with build-installer.ps1.");

            RestoreUserFiles(temp);

            Report(progress, 0.80, "Linking the current version…");
            string version = RepairCurrentJson();

            Report(progress, 0.86, "Creating shortcuts…");
            string target = ResolveLaunchTarget();
            ApplyShortcuts(options, target);

            Report(progress, 0.92, "Registering with Windows…");
            ApplyRegistry(options, target, version);
            WriteUninstallScript();

            Report(progress, 0.98, "Done.");
            if (options.LaunchAfterInstall) Launch();
            Report(progress, 1.0, "Aerisflow " + version + " is installed.");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    public void Uninstall(IProgress<InstallProgress>? progress = null)
    {
        Report(progress, 0.02, "Closing Aerisflow…");
        CloseRunningApp();
        InstallDir = ResolveInstallDir();

        Report(progress, 0.10, "Removing shortcuts…");
        RemoveShortcuts();

        Report(progress, 0.25, "Cleaning the registry…");
        RemoveRegistry();

        Report(progress, 0.40, "Removing files…");
        Report(progress, 0.90, "Cleaning up…");

        if (IsRunningFrom(InstallDir))
        {
            ScheduleFolderRemoval(InstallDir);
        }
        else
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
                    break;
                }
                catch
                {
                    Thread.Sleep(350);
                }
            }
        }

        Report(progress, 1.0, "Aerisflow was removed.");
    }

    public void Launch()
    {
        string target = ResolveLaunchTarget();
        if (!File.Exists(target)) return;
        Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(target) ?? InstallDir,
        });
    }

    public string ResolveLaunchTarget()
    {
        string launcher = Path.Combine(InstallDir, "Launcher.exe");
        if (File.Exists(launcher)) return launcher;

        var current = ReadCurrent();
        if (current != null && !string.IsNullOrWhiteSpace(current.Dir))
        {
            string exe = Path.Combine(InstallDir, current.Dir, "Aerisflow.exe");
            if (File.Exists(exe)) return exe;
        }

        string direct = Path.Combine(InstallDir, "Aerisflow.exe");
        if (File.Exists(direct)) return direct;

        try
        {
            var dirs = Directory.GetDirectories(InstallDir, "app-*")
            .OrderByDescending(d => Path.GetFileName(d), new VersionOrder())
            .ToArray();
            foreach (string dir in dirs)
            {
                string exe = Path.Combine(dir, "Aerisflow.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }

        return direct;
    }

    private sealed class VersionOrder : System.Collections.Generic.IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            int[] pa = Parts(a);
            int[] pb = Parts(b);
            for (int i = 0; i < pa.Length; i++)
            {
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            }
            return 0;
        }

        private static int[] Parts(string? name)
        {
            var numbers = new int[4];
            if (string.IsNullOrWhiteSpace(name)) return numbers;
            string text = name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? name.Substring(4) : name;
            string[] parts = text.Split('.', '-', '+');
            for (int i = 0; i < numbers.Length && i < parts.Length; i++) int.TryParse(parts[i], out numbers[i]);
            return numbers;
        }
    }

    private CurrentState? ReadCurrent()
    {
        try
        {
            string path = Path.Combine(InstallDir, "current.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CurrentState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public void CloseRunningApp()
    {
        string[] names = { "Aerisflow", "Launcher" };
        foreach (string name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2500);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }

    private void PreserveUserFiles(string destination)
    {
        string[] files = { "settings.json", "device.json" };
        foreach (string file in files)
        {
            try
            {
                string source = Path.Combine(InstallDir, file);
                if (File.Exists(source)) File.Copy(source, Path.Combine(destination, file), true);
            }
            catch { }
        }
    }

    private void RestoreUserFiles(string source)
    {
        foreach (string file in Directory.GetFiles(source))
        {
            try
            {
                File.Copy(file, Path.Combine(InstallDir, Path.GetFileName(file)), true);
            }
            catch { }
        }
    }

    private void ClearInstallDir()
    {
        if (!Directory.Exists(InstallDir)) return;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(InstallDir, true);
                return;
            }
            catch
            {
                try
                {
                    foreach (string file in Directory.GetFiles(InstallDir))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
                    }
                    foreach (string dir in Directory.GetDirectories(InstallDir))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
                catch { }
                Thread.Sleep(350);
            }
        }
    }

    private bool ExtractPayload(IProgress<InstallProgress>? progress)
    {
        using Stream? zip = OpenPayloadZip();
        if (zip != null)
        {
            ExtractZip(zip, progress);
            return true;
        }
        string folder = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(folder))
        {
            CopyFolder(folder, InstallDir, progress);
            return true;
        }
        return false;
    }

    private static Stream? OpenPayloadZip()
    {
        Assembly assembly = typeof(InstallEngine).Assembly;
        string? name = assembly.GetManifestResourceNames()
        .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
        if (name != null) return assembly.GetManifestResourceStream(name);

        string beside = Path.Combine(AppContext.BaseDirectory, "payload.zip");
        if (File.Exists(beside)) return File.OpenRead(beside);
        return null;
    }

    private void ExtractZip(Stream stream, IProgress<InstallProgress>? progress)
    {
        string root = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        int done = 0;
        foreach (var entry in entries)
        {
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(InstallDir, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(destination, true);
            done++;
            Report(progress, 0.10 + 0.68 * ((double)done / Math.Max(1, entries.Count)), "Installing " + entry.Name);
        }
    }

    private void CopyFolder(string source, string destination, IProgress<InstallProgress>? progress)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        int done = 0;
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            done++;
            Report(progress, 0.10 + 0.68 * ((double)done / Math.Max(1, files.Length)), "Installing " + Path.GetFileName(file));
        }
    }

    private string RepairCurrentJson()
    {
        string path = Path.Combine(InstallDir, "current.json");
        var current = ReadCurrent();
        bool valid = current != null
        && !string.IsNullOrWhiteSpace(current.Dir)
        && File.Exists(Path.Combine(InstallDir, current.Dir, "Aerisflow.exe"));

        if (valid)
        {
            string found = Normalize(current!.Version);
            if (string.IsNullOrWhiteSpace(found)) found = Normalize(current.Dir.Replace("app-", ""));
            if (string.IsNullOrWhiteSpace(found)) found = PayloadVersion;
            if (!string.Equals(found, current.Version, StringComparison.Ordinal))
            {
                current.Version = found;
                try { File.WriteAllText(path, JsonSerializer.Serialize(current)); } catch { }
            }
            return found;
        }

        string? appDir = null;
        try
        {
            appDir = Directory.GetDirectories(InstallDir, "app-*")
            .Where(d => File.Exists(Path.Combine(d, "Aerisflow.exe")))
            .OrderByDescending(d => Path.GetFileName(d), new VersionOrder())
            .FirstOrDefault();
        }
        catch { }

        string dir = appDir != null ? Path.GetFileName(appDir) : "";
        string version = !string.IsNullOrWhiteSpace(dir) ? Normalize(dir.Replace("app-", "")) : "";

        if (string.IsNullOrWhiteSpace(version) && File.Exists(Path.Combine(InstallDir, "Aerisflow.exe")))
        {
            version = Normalize(FileVersionInfo.GetVersionInfo(Path.Combine(InstallDir, "Aerisflow.exe")).FileVersion);
        }
        if (string.IsNullOrWhiteSpace(version)) version = PayloadVersion;

        var repaired = new CurrentState
        {
            Version = version,
            Dir = dir,
            Previous = current?.Dir,
        };
        try { File.WriteAllText(path, JsonSerializer.Serialize(repaired)); } catch { }
        return repaired.Version;
    }

    private void ApplyShortcuts(InstallOptions options, string target)
    {
        try
        {
            string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Aerisflow.lnk");
            string desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Aerisflow.lnk");

            if (options.StartMenuShortcut) CreateShortcut(startMenu, target);
            else TryDelete(startMenu);

            if (options.DesktopShortcut) CreateShortcut(desktop, target);
            else TryDelete(desktop);
        }
        catch { }
    }

    private void RemoveShortcuts()
    {
        try
        {
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Aerisflow.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Aerisflow.lnk"));
        }
        catch { }
    }

    private static void CreateShortcut(string path, string target)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            link.IconLocation = target + ",0";
            link.Description = "Aerisflow — snow on your desktop";
            link.Save();
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void ApplyRegistry(InstallOptions options, string target, string version)
    {
        try
        {
            using (var appKey = Registry.CurrentUser.CreateSubKey(AppKeyPath))
            {
                appKey?.SetValue("InstallDir", InstallDir);
                appKey?.SetValue("Version", version);
                appKey?.SetValue("InstalledAt", DateTime.UtcNow.ToString("O"));
            }

            using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (options.StartWithWindows) run?.SetValue(RunValueName, "\"" + target + "\"", RegistryValueKind.String);
                else run?.DeleteValue(RunValueName, false);
            }

            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, true))
            {
                approved?.DeleteValue(RunValueName, false);
            }

            using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            if (uninstall != null)
            {
                string script = Path.Combine(InstallDir, "uninstall.cmd");
                uninstall.SetValue("DisplayName", "Aerisflow");
                uninstall.SetValue("DisplayVersion", version);
                uninstall.SetValue("Publisher", "Aerisflow");
                uninstall.SetValue("DisplayIcon", target + ",0");
                uninstall.SetValue("InstallLocation", InstallDir);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                uninstall.SetValue("UninstallString", "cmd.exe /c \"\"" + script + "\"\"");
            }
        }
        catch { }
    }

    private void RemoveRegistry()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            run?.DeleteValue(RunValueName, false);
            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, true))
            approved?.DeleteValue(RunValueName, false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKeyPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        }
        catch { }
    }

    private void WriteUninstallScript()
    {
        try
        {
            string script = Path.Combine(InstallDir, "uninstall.cmd");
            var text = new StringBuilder();
            text.AppendLine("@echo off");
            text.AppendLine("setlocal");
            text.AppendLine("set \"DIR=" + InstallDir.TrimEnd('\\') + "\"");
            text.AppendLine("if /i \"%~dp0\"==\"%DIR%\\\" (");
            text.AppendLine("  copy /y \"%~f0\" \"%TEMP%\\aerisflow-uninstall.cmd\" >nul");
            text.AppendLine("  start \"\" /min cmd /c \"%TEMP%\\aerisflow-uninstall.cmd\"");
            text.AppendLine("  exit /b");
            text.AppendLine(")");
            text.AppendLine("taskkill /f /im Aerisflow.exe >nul 2>&1");
            text.AppendLine("taskkill /f /im Launcher.exe >nul 2>&1");
            text.AppendLine("reg delete \"HKCU\\Software\\Aerisflow\" /f >nul 2>&1");
            text.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Aerisflow\" /f >nul 2>&1");
            text.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v Aerisflow /f >nul 2>&1");
            text.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\" /v Aerisflow /f >nul 2>&1");
            text.AppendLine("del /f /q \"%USERPROFILE%\\Desktop\\Aerisflow.lnk\" >nul 2>&1");
            text.AppendLine("del /f /q \"%USERPROFILE%\\OneDrive\\Desktop\\Aerisflow.lnk\" >nul 2>&1");
            text.AppendLine("del /f /q \"%PUBLIC%\\Desktop\\Aerisflow.lnk\" >nul 2>&1");
            text.AppendLine("del /f /q \"%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\Aerisflow.lnk\" >nul 2>&1");
            text.AppendLine("cd /d \"%TEMP%\"");
            text.AppendLine("timeout /t 1 /nobreak >nul");
            text.AppendLine("rmdir /s /q \"%DIR%\" >nul 2>&1");
            text.AppendLine("del \"%~f0\" >nul 2>&1");
            text.AppendLine("endlocal");
            File.WriteAllText(script, text.ToString(), Encoding.ASCII);
        }
        catch { }
    }

    private static bool IsRunningFrom(string directory)
    {
        try
        {
            string? process = Environment.ProcessPath;
            if (string.IsNullOrEmpty(process)) return false;
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(process).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ScheduleFolderRemoval(string directory)
    {
        try
        {
            string batch = Path.Combine(Path.GetTempPath(),
            "aerisflow-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
            var text = new StringBuilder();
            text.AppendLine("@echo off");
            text.AppendLine("timeout /t 2 /nobreak >nul");
            text.AppendLine("rmdir /s /q \"" + directory + "\"");
            text.AppendLine("del \"%~f0\"");
            File.WriteAllText(batch, text.ToString());
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + batch + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }
    }

    private static void Report(IProgress<InstallProgress>? progress, double fraction, string message)
    => progress?.Report(new InstallProgress(fraction, message));
}