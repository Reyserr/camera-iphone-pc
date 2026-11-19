using System.IO;
using System.Text.Json;

namespace CameraPC;

/// <summary>Réglages sauvegardés dans %AppData%\LynxCam\settings.json.</summary>
public sealed class AppSettings
{
    // Connexion
    public string Mode { get; set; } = "wifi";       // "wifi" ou "usb"
    public string Host { get; set; } = "";
    public int Port { get; set; } = Net.WireProtocol.DefaultPort;

    // Vidéo
    public string Camera { get; set; } = "back";
    public string Resolution { get; set; } = "720p";
    public int Fps { get; set; } = 30;
    public int Quality { get; set; } = 70;

    // Arrière-plan
    public string Background { get; set; } = "none";  // none, blur, theme, image
    public int Blur { get; set; } = 50;
    public string Theme { get; set; } = "nuit";
    public string BackgroundPhoto { get; set; } = "";

    // Image
    public double Exposure { get; set; }
    public bool FocusAuto { get; set; } = true;
    public double Focus { get; set; } = 0.5;
    public bool WhiteBalanceAuto { get; set; } = true;
    public int Kelvin { get; set; } = 5500;
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Saturation { get; set; }
    public bool Mirror { get; set; }

    // Son
    public bool Mic { get; set; }
    public int AudioDevice { get; set; } = -1;
    public int Volume { get; set; } = 100;

    // Webcam virtuelle
    public string VirtualCamResolution { get; set; } = "720p";
    public bool VirtualCamAutoStart { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LynxCam", "settings.json");

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
