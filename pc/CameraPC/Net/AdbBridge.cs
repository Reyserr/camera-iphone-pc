using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CameraPC.Net;

/// <summary>
/// Connexion par câble USB à un téléphone Android, avec l'outil gratuit « adb » de Google.
/// adb relie un port du PC au port 5050 du téléphone (le débogage USB doit être activé).
/// </summary>
public static class AdbBridge
{
    private const string DownloadUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
    /// <summary>Port du PC relié au port 5050 du téléphone.</summary>
    public const int LocalPort = 15050;

    private static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LynxCam");

    /// <summary>Cherche adb.exe : dossier de LynxCam, puis SDK Android, puis le PATH.</summary>
    public static string? FindAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(DataFolder, "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        };
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
            candidates.AddRange(path.Split(Path.PathSeparator).Where(p => p.Length > 0).Select(p => Path.Combine(p.Trim(), "adb.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Renvoie adb.exe, en le téléchargeant chez Google la première fois (environ 7 Mo).</summary>
    public static async Task<string> EnsureAdbAsync(Action<string> log, CancellationToken ct)
    {
        string? adb = FindAdb();
        if (adb != null) return adb;

        log("Première connexion USB Android : téléchargement de l'outil adb de Google…");
        Directory.CreateDirectory(DataFolder);
        string zip = Path.Combine(DataFolder, "platform-tools.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
        await using (var file = File.Create(zip))
        {
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(file, ct);
        }
        ZipFile.ExtractToDirectory(zip, DataFolder, overwriteFiles: true);
        File.Delete(zip);

        adb = Path.Combine(DataFolder, "platform-tools", "adb.exe");
        if (!File.Exists(adb)) throw new IOException("téléchargement d'adb raté.");
        log("Outil adb installé.");
        return adb;
    }

    /// <summary>Vérifie qu'un téléphone est branché et autorisé, puis relie le port du PC à celui du téléphone.</summary>
    public static async Task ForwardAsync(string adb, int phonePort, CancellationToken ct)
    {
        await StartServerAsync(adb, ct);
        string devices = await RunAsync(adb, ct, "devices");
        var lines = devices.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Any(l => l.EndsWith("unauthorized")))
            throw new IOException("sur le téléphone, accepte « Autoriser le débogage USB ? » puis réessaie.");
        if (!lines.Any(l => l.EndsWith("device")))
            throw new IOException("aucun téléphone Android détecté : branche-le et active le débogage USB (voir le tutoriel).");

        await RunAsync(adb, ct, "forward", $"tcp:{LocalPort}", $"tcp:{phonePort}");
    }

    /// <summary>
    /// Démarre le service adb sans rediriger sa sortie : sinon le service, qui reste ouvert,
    /// garderait la sortie et bloquerait la lecture des commandes suivantes.
    /// </summary>
    private static async Task StartServerAsync(string adb, CancellationToken ct)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("start-server");
        using var process = Process.Start(info) ?? throw new IOException("impossible de lancer adb.");
        await process.WaitForExitAsync(ct);
    }

    private static async Task<string> RunAsync(string adb, CancellationToken ct, params string[] args)
    {
        var info = new ProcessStartInfo(adb)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("impossible de lancer adb.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new IOException("adb : " + (await error).Trim());
        return await output;
    }
}
