using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CameraPC.Media;

/// <summary>
/// Webcam virtuelle « LynxCam », visible dans OBS, Discord, Zoom… (DirectShow).
/// S'appuie sur softcam (licence MIT), compilé par GitHub avec le nom LynxCam.
/// </summary>
public sealed class VirtualCamera : IDisposable
{
    /// <summary>Identifiant de la webcam, le même que celui compilé dans softcam.dll (voir build-pc.yml).</summary>
    public const string ClassId = "{B466DBF9-7F46-45F6-97F3-7F5554270042}";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateCameraFn(int width, int height, float framerate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleteCameraFn(IntPtr camera);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendFrameFn(IntPtr camera, IntPtr imageBits);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsConnectedFn(IntPtr camera);

    private readonly object _gate = new();
    private readonly FrameConverter _converter = new();
    // softcam.dll reste chargée jusqu'à la fermeture de l'app (ses fils internes en ont besoin).
    private static IntPtr s_library;
    private IntPtr _camera;
    private DeleteCameraFn? _delete;
    private SendFrameFn? _send;
    private IsConnectedFn? _isConnected;
    private byte[]? _frame;
    private GCHandle _pin;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool IsRunning
    {
        get { lock (_gate) return _camera != IntPtr.Zero; }
    }

    /// <summary>Vrai si une app (OBS, Discord…) lit la webcam en ce moment.</summary>
    public bool IsAppConnected
    {
        get
        {
            lock (_gate) return _camera != IntPtr.Zero && _isConnected != null && _isConnected(_camera);
        }
    }

    // ───────────── Installation (une fois, droits administrateur) ─────────────

    private static string BundledDll(string arch) =>
        Path.Combine(AppContext.BaseDirectory, "VirtualCam", arch, "softcam.dll");

    private static string InstalledDll(string arch) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "LynxCam", "VirtualCam", arch, "softcam.dll");

    /// <summary>Vrai si l'app contient la webcam virtuelle (version compilée par GitHub).</summary>
    public static bool IsBundled => File.Exists(BundledDll("x64"));

    public static string? RegisteredPath()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{ClassId}\InprocServer32");
            return key?.GetValue(null) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsInstalled => RegisteredPath() is { } path && File.Exists(path);

    /// <summary>
    /// Copie softcam.dll dans %LocalAppData%\LynxCam (emplacement fixe) puis l'enregistre dans Windows.
    /// Windows demande une seule fois l'autorisation administrateur.
    /// </summary>
    public static async Task<string?> InstallAsync()
    {
        if (!IsBundled) return "la webcam virtuelle n'est pas incluse dans cette version (prends celle compilée par GitHub).";
        var paths = new List<string>();
        foreach (var arch in new[] { "x64", "x86" })
        {
            string source = BundledDll(arch), target = InstalledDll(arch);
            if (!File.Exists(source)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length
                    || !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                    File.Copy(source, target, overwrite: true);
            }
            catch (IOException)
            {
                return "fichier de la webcam utilisé : ferme OBS, Discord et les autres apps vidéo, puis réessaie.";
            }
            paths.Add(target);
        }
        string? error = await RunElevatedAsync("--register", paths);
        if (error != null) return error;
        return IsInstalled ? null : "l'enregistrement de la webcam a échoué.";
    }

    public static async Task<string?> UninstallAsync()
    {
        var paths = new[] { "x64", "x86" }.Select(InstalledDll).Where(File.Exists).ToList();
        if (paths.Count == 0) return null;
        return await RunElevatedAsync("--unregister", paths);
    }

    private static async Task<string?> RunElevatedAsync(string verb, List<string> dllPaths)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "LynxCam.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        info.ArgumentList.Add(verb);
        foreach (var path in dllPaths) info.ArgumentList.Add(path);
        try
        {
            using var process = Process.Start(info);
            if (process == null) return "impossible de lancer l'installation.";
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? null : $"regsvr32 a renvoyé le code {process.ExitCode}.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "autorisation administrateur refusée.";
        }
    }

    /// <summary>Appelé dans le processus administrateur : enregistre (ou retire) les DLL avec regsvr32.</summary>
    public static int RegisterFromCommandLine(bool register, IEnumerable<string> dllPaths)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int result = 0;
        foreach (var path in dllPaths)
        {
            bool is32 = path.Contains(Path.DirectorySeparatorChar + "x86" + Path.DirectorySeparatorChar);
            string regsvr = Path.Combine(windows, is32 ? "SysWOW64" : "System32", "regsvr32.exe");
            var info = new ProcessStartInfo(regsvr) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("/s");
            if (!register) info.ArgumentList.Add("/u");
            info.ArgumentList.Add(path);
            using var process = Process.Start(info);
            process?.WaitForExit();
            if (process == null || process.ExitCode != 0) result = process?.ExitCode ?? 1;
        }
        return result;
    }

    // ───────────── Diffusion ─────────────

    /// <summary>Crée la webcam à la taille donnée. Renvoie un message d'erreur, ou null si tout va bien.</summary>
    public string? Start(int width, int height)
    {
        lock (_gate)
        {
            StopLocked();
            string? path = RegisteredPath();
            if (path == null || !File.Exists(path)) return "la webcam LynxCam n'est pas installée.";
            try
            {
                if (s_library == IntPtr.Zero) s_library = NativeLibrary.Load(path);
                var create = Marshal.GetDelegateForFunctionPointer<CreateCameraFn>(NativeLibrary.GetExport(s_library, "scCreateCamera"));
                _delete = Marshal.GetDelegateForFunctionPointer<DeleteCameraFn>(NativeLibrary.GetExport(s_library, "scDeleteCamera"));
                _send = Marshal.GetDelegateForFunctionPointer<SendFrameFn>(NativeLibrary.GetExport(s_library, "scSendFrame"));
                _isConnected = Marshal.GetDelegateForFunctionPointer<IsConnectedFn>(NativeLibrary.GetExport(s_library, "scIsConnected"));
                // 0 image/s = chaque image est transmise dès qu'elle arrive.
                _camera = create(width, height, 0f);
            }
            catch (Exception ex)
            {
                StopLocked();
                return "chargement de la webcam impossible : " + ex.Message;
            }
            if (_camera == IntPtr.Zero)
            {
                StopLocked();
                return "la webcam LynxCam est déjà utilisée par une autre fenêtre LynxCam.";
            }
            Width = width;
            Height = height;
            _frame = new byte[width * height * 3];
            _pin = GCHandle.Alloc(_frame, GCHandleType.Pinned);
            return null;
        }
    }

    /// <summary>Envoie une image (n'importe quelle taille : elle est adaptée avec des bandes noires). Appelable depuis n'importe quel fil.</summary>
    public void SendFrame(BitmapSource image)
    {
        lock (_gate)
        {
            if (_camera == IntPtr.Zero || _frame == null || _send == null) return;
            try
            {
                _converter.ToBgr24(image, _frame, Width, Height);
                _send(_camera, _pin.AddrOfPinnedObject());
            }
            catch (Exception)
            {
                // Image illisible : on passe à la suivante.
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_camera != IntPtr.Zero && _delete != null)
        {
            try { _delete(_camera); } catch { /* déjà fermée */ }
        }
        _camera = IntPtr.Zero;
        if (_pin.IsAllocated) _pin.Free();
        _frame = null;
        _delete = null;
        _send = null;
        _isConnected = null;
    }

    public void Dispose() => Stop();
}
