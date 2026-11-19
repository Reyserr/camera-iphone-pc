using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CameraPC.Media;

namespace CameraPC;

public partial class App : Application
{
    private static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LynxCam");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;

        // Lancé en administrateur pour installer ou retirer la webcam virtuelle.
        if (args.Length > 0 && (args[0] == "--register" || args[0] == "--unregister"))
        {
            Shutdown(VirtualCamera.RegisterFromCommandLine(args[0] == "--register", args.Skip(1)));
            return;
        }

        // Test de démarrage utilisé par la compilation GitHub : ouvre la fenêtre puis quitte.
        bool smokeTest = args.Contains("--smoke-test");

        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrash(ex.Exception);
            ex.Handled = true;
            if (smokeTest)
                Shutdown(1);
            else
                MessageBox.Show("Erreur inattendue : " + ex.Exception.Message, "LynxCam",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => WriteCrash(ex.ExceptionObject as Exception);

        MainWindow window;
        try
        {
            window = new MainWindow();
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            if (!smokeTest)
                MessageBox.Show("LynxCam n'a pas pu démarrer : " + ex.Message, "LynxCam",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = window;
        if (smokeTest)
        {
            window.ContentRendered += (_, _) =>
                Dispatcher.InvokeAsync(() => Shutdown(0), DispatcherPriority.ApplicationIdle);
        }
        window.Show();
    }

    private static void WriteCrash(Exception? exception)
    {
        if (exception == null) return;
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(Path.Combine(DataFolder, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception}\n\n");
        }
        catch (Exception)
        {
            // Rien de plus à faire.
        }
    }
}
