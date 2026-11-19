using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

internal static class Program
{
    private const string ExeName = "Aerisflow.exe";
    private const string AppProcessName = "Aerisflow";
    private const string HealthFile = "healthy.ok";

    private static int Main(string[] args)
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Current current = ReadCurrent(root);

        string? target = Resolve(root, current.Dir) ?? Resolve(root, current.Previous) ?? Newest(root, null);
        if (target == null) return 3;

        WaitForAppExit(10000);
        WaitForUnlock(target, 8000);
        ClearHealth(target);

        DateTime started = DateTime.UtcNow;
        Process? app = Start(target, args);
        if (app != null && Healthy(app, target, started)) return 0;

        string? fallback = Resolve(root, current.Previous) ?? Newest(root, target);
        if (fallback == null || Same(fallback, target)) return app == null ? 4 : 0;

        WaitForAppExit(10000);
        WaitForUnlock(fallback, 8000);
        ClearHealth(fallback);

        Process? back = Start(fallback, args);
        if (back == null) return 4;

        WriteCurrent(root, fallback, current.Dir);
        return 0;
    }

    private static Process? Start(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                UseShellExecute = false,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    private static bool Healthy(Process app, string exe, DateTime started)
    {
        string marker = Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, HealthFile);
        for (int i = 0; i < 100; i++)
        {
            try
            {
                if (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= started.AddSeconds(-2)) return true;
                if (app.HasExited) return false;
            }
            catch
            {
                return true;
            }
            Thread.Sleep(100);
        }
        try { return !app.HasExited; } catch { return true; }
    }

    private static void ClearHealth(string exe)
    {
        try
        {
            string marker = Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, HealthFile);
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch { }
    }

    private static void WaitForAppExit(int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            bool running = false;
            foreach (var process in Process.GetProcessesByName(AppProcessName))
            {
                try { if (process.Id != Environment.ProcessId) running = true; } catch { }
                finally { process.Dispose(); }
            }
            if (!running) return;
            Thread.Sleep(120);
            waited += 120;
        }
    }

    private static void WaitForUnlock(string exe, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            try
            {
                using var fs = File.Open(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; }
            Thread.Sleep(150);
            waited += 150;
        }
    }

    private static string? Resolve(string root, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, dir));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            string exe = Path.Combine(full, ExeName);
            return File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Newest(string root, string? exclude)
    {
        try
        {
            string? best = Directory.GetDirectories(root, "app-*")
                .Where(d => File.Exists(Path.Combine(d, ExeName)))
                .Select(d => Path.Combine(d, ExeName))
                .Where(e => exclude == null || !Same(e, exclude))
                .OrderByDescending(e => VersionOf(Path.GetFileName(Path.GetDirectoryName(e) ?? string.Empty)), new VersionOrder())
                .FirstOrDefault();
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static bool Same(string a, string b)
    => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string VersionOf(string dirName)
    => dirName.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? dirName.Substring(4) : dirName;

    private static Current ReadCurrent(string root)
    {
        var state = new Current();
        try
        {
            string path = Path.Combine(root, "current.json");
            if (!File.Exists(path)) return state;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return state;
            state.Version = ReadString(document.RootElement, "version");
            state.Dir = ReadString(document.RootElement, "dir");
            state.Previous = ReadString(document.RootElement, "previous");
        }
        catch { }
        return state;
    }

    private static string ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static void WriteCurrent(string root, string exe, string? previous)
    {
        try
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(exe) ?? string.Empty);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("version", VersionOf(dir));
                writer.WriteString("dir", dir);
                if (string.IsNullOrWhiteSpace(previous)) writer.WriteNull("previous");
                else writer.WriteString("previous", previous);
                writer.WriteEndObject();
            }
            string path = Path.Combine(root, "current.json");
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, buffer.ToArray());
            File.Move(tmp, path, true);
        }
        catch { }
    }

    private sealed class Current
    {
        public string Version = string.Empty;
        public string Dir = string.Empty;
        public string Previous = string.Empty;
    }

    private sealed class VersionOrder : System.Collections.Generic.IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            int[] pa = Parse(a ?? string.Empty);
            int[] pb = Parse(b ?? string.Empty);
            for (int i = 0; i < pa.Length; i++)
            {
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            }
            return 0;
        }

        private static int[] Parse(string version)
        {
            string[] parts = version.Split('.', '-', '+');
            var numbers = new int[4];
            for (int i = 0; i < numbers.Length && i < parts.Length; i++) int.TryParse(parts[i], out numbers[i]);
            return numbers;
        }
    }
}