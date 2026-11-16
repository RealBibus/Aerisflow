using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;

namespace Launcher;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private sealed class CurrentState
    {
        public string version { get; set; } = "";
        public string dir { get; set; } = "";
        public string? previous { get; set; }
    }

    [STAThread]
    private static void Main()
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string currentPath = Path.Combine(root, "current.json");

        var state = ReadCurrent(currentPath, root);
        if (state == null || string.IsNullOrEmpty(state.dir))
        {
            MessageBoxW(IntPtr.Zero, "Не найдена установленная версия Aerisflow.", "Aerisflow", 0x10);
            return;
        }

        if (TryStartHealthy(root, state.dir)) return;

        if (!string.IsNullOrEmpty(state.previous))
        {
            var rollback = new CurrentState { version = state.version, dir = state.previous!, previous = null };
            if (TryStartHealthy(root, rollback.dir))
            {
                WriteCurrent(currentPath, rollback);
                return;
            }
        }

        MessageBoxW(IntPtr.Zero, "Не удалось запустить Aerisflow. Переустановите приложение.", "Aerisflow", 0x10);
    }

    private static bool TryStartHealthy(string root, string dir)
    {
        string appDir = Path.Combine(root, dir);
        string exe = Path.Combine(appDir, "Aerisflow.exe");
        if (!File.Exists(exe)) return false;

        string healthy = Path.Combine(appDir, "healthy.ok");
        try { File.Delete(healthy); } catch { }

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = appDir,
            UseShellExecute = true,
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch { return false; }
        if (proc == null) return false;

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (File.Exists(healthy)) return true;
            if (proc.HasExited) return proc.ExitCode == 0 && File.Exists(healthy);
            Thread.Sleep(200);
        }
        return File.Exists(healthy);
    }

    private static CurrentState? ReadCurrent(string path, string root)
    {
        if (File.Exists(path))
        {
            try { return JsonSerializer.Deserialize<CurrentState>(File.ReadAllText(path)); }
            catch { }
        }
        foreach (var d in Directory.GetDirectories(root, "app-*"))
        {
            if (File.Exists(Path.Combine(d, "Aerisflow.exe")))
                return new CurrentState { version = Path.GetFileName(d), dir = Path.GetFileName(d) };
        }
        return null;
    }

    private static void WriteCurrent(string path, CurrentState state)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, path, true);
    }
}