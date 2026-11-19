using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CameraPC.Net;

namespace CameraPC;

public partial class MainWindow : Window
{
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    private readonly PhoneClient _phone = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Images : on ne garde que la plus récente pour limiter le retard.
    private byte[]? _latestFrame;
    private BitmapSource? _frameToShow;
    private int _decoding;
    private int _framesReceived;
    private int _framesShown;

    private bool _ready;
    private bool _wantConnected;
    private bool _updatingControls;
    private CancellationTokenSource? _reconnectCts;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;

        HostBox.Text = _settings.Host;
        PortBox.Text = _settings.Port.ToString();
        SelectByTag(CameraBox, _settings.Camera);
        SelectByTag(ResolutionBox, _settings.Resolution);
        QualitySlider.Value = _settings.Quality;
        UpdateQualityLabel();
        UpdateZoomLabel();
        CameraPanel.IsEnabled = false;

        _phone.Log += message => Ui(() => AddLog(message));
        _phone.ConnectionChanged += connected => Ui(() => OnConnectionChanged(connected));
        _phone.HelloReceived += hello => Ui(() => OnHello(hello));
        _phone.StatusReceived += status => Ui(() => OnStatus(status));
        _phone.PingMeasured += ms => Ui(() => LatencyText.Text = $"{ms:0} ms (aller-retour)");
        _phone.FrameReceived += OnFrameReceived;

        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        _ready = true;
        AddLog("Prêt. Ouvre l'app sur l'iPhone, puis saisis l'adresse qu'elle affiche.");
    }

    private void Ui(Action action) => Dispatcher.InvokeAsync(action);

    // ───────────── Connexion ─────────────

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wantConnected)
        {
            _wantConnected = false;
            _reconnectCts?.Cancel();
            _phone.Disconnect();
            SetStatus("Déconnecté", null);
            UpdateConnectButton();
            return;
        }

        string host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            SetStatus("Saisis l'adresse IP affichée sur l'iPhone.", ErrorBrush);
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            SetStatus("Port invalide (5050 par défaut).", ErrorBrush);
            return;
        }

        _settings.Host = host;
        _settings.Port = port;
        _settings.Save();

        _wantConnected = true;
        UpdateConnectButton();
        SetStatus($"Connexion à {host}:{port}…", null);
        try
        {
            await _phone.ConnectAsync(host, port);
        }
        catch (Exception ex)
        {
            // Premier essai raté : on s'arrête là (adresse sans doute fausse).
            _wantConnected = false;
            UpdateConnectButton();
            SetStatus("Échec : " + ex.Message, ErrorBrush);
            AddLog($"Connexion impossible à {host}:{port} : {ex.Message}");
            AddLog("Vérifie : même Wi-Fi, app ouverte sur l'iPhone, accès au réseau local autorisé.");
        }
    }

    private void ConnectionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_wantConnected) ConnectButton_Click(sender, e);
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            _reconnectCts?.Cancel();
            SetStatus($"Connecté à {_settings.Host}", ConnectedBrush);
            AddLog("Connecté à l'iPhone.");
            CameraPanel.IsEnabled = true;
            SendInitialSettings();
        }
        else
        {
            CameraPanel.IsEnabled = false;
            PreviewImage.Source = null;
            PlaceholderText.Visibility = Visibility.Visible;
            ResolutionText.Text = FpsText.Text = LatencyText.Text = BitrateText.Text = BatteryText.Text = "—";
            if (_wantConnected)
            {
                SetStatus("Connexion perdue, nouvelle tentative…", ErrorBrush);
                ReconnectLoop();
            }
        }
        UpdateConnectButton();
    }

    /// <summary>Après une coupure, réessaie toutes les 2 secondes tant que l'utilisateur n'a pas cliqué sur Se déconnecter.</summary>
    private async void ReconnectLoop()
    {
        _reconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        while (_wantConnected && !cts.IsCancellationRequested && !_phone.IsConnected)
        {
            try
            {
                await Task.Delay(2000, cts.Token);
                if (!_wantConnected || _phone.IsConnected) return;
                SetStatus("Reconnexion…", null);
                await _phone.ConnectAsync(_settings.Host, _settings.Port, cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus($"Reconnexion impossible ({ex.Message}). Nouvel essai dans 2 s…", ErrorBrush);
            }
        }
    }

    private void SendInitialSettings()
    {
        // Le PC impose ses derniers réglages à l'iPhone.
        _ = _phone.SendCommandAsync("camera", _settings.Camera);
        _ = _phone.SendCommandAsync("resolution", _settings.Resolution);
        _ = _phone.SendCommandAsync("quality", _settings.Quality / 100.0);
        _updatingControls = true;
        ZoomSlider.Value = 1;
        TorchBox.IsChecked = false;
        _updatingControls = false;
    }

    private void UpdateConnectButton() =>
        ConnectButton.Content = _wantConnected ? "Se déconnecter" : "Se connecter";

    private void SetStatus(string text, Brush? brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush ?? (Brush)FindResource("MutedBrush");
    }

    // ───────────── Messages de l'iPhone ─────────────

    private void OnHello(JsonElement hello)
    {
        string name = GetString(hello, "name") ?? "iPhone";
        string system = GetString(hello, "system") ?? "";
        DeviceText.Text = name;
        DeviceText.ToolTip = $"{name} — {system}";
        AddLog($"iPhone : {name} ({system}), protocole {GetNumber(hello, "protocol") ?? 0}");
    }

    private void OnStatus(JsonElement status)
    {
        double? battery = GetNumber(status, "battery");
        bool charging = status.TryGetProperty("charging", out var c) && c.ValueKind == JsonValueKind.True;
        BatteryText.Text = battery is >= 0 ? $"{battery * 100:0} %{(charging ? " (en charge)" : "")}" : "—";

        // Les boutons de l'iPhone peuvent changer la caméra ou la lampe : on suit.
        _updatingControls = true;
        if (GetString(status, "camera") is { } camera) SelectByTag(CameraBox, camera);
        if (status.TryGetProperty("torch", out var torch) && torch.ValueKind is JsonValueKind.True or JsonValueKind.False)
            TorchBox.IsChecked = torch.GetBoolean();
        if (GetNumber(status, "zoom") is { } zoom && !ZoomSlider.IsMouseCaptureWithin && Math.Abs(zoom - ZoomSlider.Value) > 0.05)
            ZoomSlider.Value = Math.Clamp(zoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updatingControls = false;
        UpdateZoomLabel();
    }

    // ───────────── Images ─────────────

    /// <summary>Appelé sur le fil de réception pour chaque image.</summary>
    private void OnFrameReceived(byte[] payload, ulong timestamp)
    {
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Exchange(ref _latestFrame, payload);
        if (Interlocked.CompareExchange(ref _decoding, 1, 0) == 0)
            _ = Task.Run(DecodeLoop);
    }

    private void DecodeLoop()
    {
        while (true)
        {
            var payload = Interlocked.Exchange(ref _latestFrame, null);
            if (payload != null)
            {
                var frame = DecodeJpeg(payload);
                if (frame != null && Interlocked.Exchange(ref _frameToShow, frame) == null)
                    Dispatcher.InvokeAsync(ShowPendingFrame, DispatcherPriority.Render);
                continue;
            }

            Volatile.Write(ref _decoding, 0);
            // Une image a pu arriver juste avant la remise à zéro : on reprend si personne d'autre ne l'a fait.
            if (Volatile.Read(ref _latestFrame) == null || Interlocked.CompareExchange(ref _decoding, 1, 0) != 0)
                return;
        }
    }

    private static BitmapSource? DecodeJpeg(byte[] payload)
    {
        try
        {
            // Les 8 premiers octets sont l'horodatage, l'image JPEG suit.
            using var stream = new MemoryStream(payload, 8, payload.Length - 8, writable: false);
            var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null; // image abîmée : on passe à la suivante
        }
    }

    private void ShowPendingFrame()
    {
        var frame = Interlocked.Exchange(ref _frameToShow, null);
        if (frame == null || !_phone.IsConnected) return;
        PreviewImage.Source = frame;
        PlaceholderText.Visibility = Visibility.Collapsed;
        _framesShown++;
        ResolutionText.Text = $"{frame.PixelWidth} × {frame.PixelHeight}";
    }

    private void UpdateStats()
    {
        int received = Interlocked.Exchange(ref _framesReceived, 0);
        int shown = _framesShown;
        _framesShown = 0;
        long bytes = _phone.TakeBytesReceived();
        if (!_phone.IsConnected) return;
        FpsText.Text = $"{received} reçues, {shown} affichées";
        BitrateText.Text = $"{bytes * 8 / 1_000_000.0:0.0} Mbit/s";
    }

    // ───────────── Réglages caméra ─────────────

    private void CameraBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingControls || SelectedTag(CameraBox) is not { } camera) return;
        _settings.Camera = camera;
        _settings.Save();
        _ = _phone.SendCommandAsync("camera", camera);
        _updatingControls = true;
        ZoomSlider.Value = 1;
        TorchBox.IsChecked = false;
        _updatingControls = false;
        UpdateZoomLabel();
    }

    private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingControls || SelectedTag(ResolutionBox) is not { } resolution) return;
        _settings.Resolution = resolution;
        _settings.Save();
        _ = _phone.SendCommandAsync("resolution", resolution);
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateQualityLabel();
        if (_updatingControls) return;
        _settings.Quality = (int)QualitySlider.Value;
        _settings.Save();
        _ = _phone.SendCommandAsync("quality", QualitySlider.Value / 100.0);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateZoomLabel();
        if (_updatingControls) return;
        _ = _phone.SendCommandAsync("zoom", Math.Round(ZoomSlider.Value, 1));
    }

    private void TorchBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingControls) return;
        _ = _phone.SendCommandAsync("torch", TorchBox.IsChecked == true);
    }

    private void UpdateQualityLabel() => QualityLabel.Text = $"Qualité JPEG : {QualitySlider.Value:0} %";

    private void UpdateZoomLabel() => ZoomLabel.Text = $"Zoom : ×{ZoomSlider.Value:0.0}";

    // ───────────── Outils ─────────────

    private void AddLog(string message)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_log.Count > 500) _log.RemoveAt(0);
        LogList.ScrollIntoView(_log[^1]);
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? GetNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _wantConnected = false;
        _reconnectCts?.Cancel();
        _statsTimer.Stop();
        _settings.Save();
        _phone.Dispose();
    }
}
