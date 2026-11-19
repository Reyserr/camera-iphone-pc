using System.IO;
using System.Text.Json;

namespace CameraPC;

/// <summary>Réglages sauvegardés dans %AppData%\CameraPC\settings.json.</summary>
public sealed class AppSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = Net.WireProtocol.DefaultPort;
    public string Camera { get; set; } = "back";
    public string Resolution { get; set; } = "720p";
    public int Quality { get; set; } = 70;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CameraPC", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Fichier abîmé : on repart des valeurs par défaut.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Pas grave si la sauvegarde échoue.
        }
    }
}
