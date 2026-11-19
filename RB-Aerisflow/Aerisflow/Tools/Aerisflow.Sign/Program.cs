using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aerisflow.Sign;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        switch (args[0])
        {
            case "genkeys": return GenKeys(args);
            case "manifest": return BuildManifest(args);
            case "sign": return Sign(args);
            case "verify": return Verify(args);
            default: Usage(); return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("genkeys <outDir>");
        Console.WriteLine("manifest <appDir> <version> <channel> <rollout> <notesFile> <outManifest>");
        Console.WriteLine("sign <manifest> <privateKeyPem> <outSig>");
        Console.WriteLine("verify <manifest> <sig> <publicKeyBase64File>");
    }

    private static int GenKeys(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(3072);
        File.WriteAllText(Path.Combine(dir, "private.pem"), rsa.ExportPkcs8PrivateKeyPem());
        string pub = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        File.WriteAllText(Path.Combine(dir, "public.txt"), pub);
        Console.WriteLine("PUBLIC KEY (вставь в UpdateConfig.PublicKeySpki):");
        Console.WriteLine(pub);
        return 0;
    }

    private static int BuildManifest(string[] args)
    {
        if (args.Length < 7) { Usage(); return 1; }
        string appDir = args[1], version = args[2], channel = args[3];
        int rollout = int.Parse(args[4]);
        string notes = File.ReadAllText(args[5]);
        string outPath = args[6];

        var files = new System.Collections.Generic.List<object>();
        foreach (var f in Directory.GetFiles(appDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(appDir, f).Replace('\\', '/');
            if (rel is "healthy.ok" or "staged.ok" or "settings.json" or "device.json" or "current.json") continue;
            if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(f);
            string hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            files.Add(new { path = rel, sha256 = hash, size = new FileInfo(f).Length, asset = rel.Replace('/', '_') });
        }

        var manifest = new
        {
            version,
            channel,
            rollout_percent = rollout,
            release_notes = notes,
            entry = "Aerisflow.exe",
            files,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("manifest written: " + outPath);
        return 0;
    }

    private static int Sign(string[] args)
    {
        if (args.Length < 4) { Usage(); return 1; }
        byte[] data = File.ReadAllBytes(args[1]);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(args[2]));
        byte[] sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        File.WriteAllBytes(args[3], sig);
        Console.WriteLine("sig written: " + args[3]);
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 4) { Usage(); return 1; }
        byte[] data = File.ReadAllBytes(args[1]);
        byte[] sig = File.ReadAllBytes(args[2]);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(File.ReadAllText(args[3]).Trim()), out _);

        bool ok = rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Console.WriteLine(ok ? "signature OK" : "signature INVALID");
        return ok ? 0 : 2;
    }
}