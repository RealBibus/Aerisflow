// Launcher/Program.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

class Program
{
    static int Main()
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string curPath = Path.Combine(root, "current.json");
        if (!File.Exists(curPath)) return 1;

        var cur = JsonSerializer.Deserialize<Current>(File.ReadAllText(curPath));
        if (cur == null || string.IsNullOrEmpty(cur.dir)) return 2;

        string appDir = Path.Combine(root, cur.dir);
        string exe = Path.Combine(appDir, "Aerisflow.exe");
        if (!File.Exists(exe)) return 3;

        // Ждём, пока старое приложение реально выйдет (лок на exe)
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var fs = File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) { Thread.Sleep(150); }
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = appDir,
            UseShellExecute = true,   // критично: detached child survives launcher exit
            CreateNoWindow = false,
        };
        Process.Start(psi);
        return 0;
    }

    class Current
    {
        public string version { get; set; } = "";
        public string dir { get; set; } = "";
        public string? previous { get; set; }
    }
}