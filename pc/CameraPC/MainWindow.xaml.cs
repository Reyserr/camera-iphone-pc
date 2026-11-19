using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CameraPC.Media;
using CameraPC.Net;
using Microsoft.Win32;

namespace CameraPC;

public partial class MainWindow : Window
{
    /// <summary>Thèmes d'arrière-plan : mêmes noms et couleurs que dans les apps iPhone et Android.</summary>
    private static readonly (string Key, string Name, string From, string To)[] Themes =
    {
        ("nuit", "Nuit", "#141E30", "#243B55"),
        ("coucher", "Coucher de soleil", "#FF7E5F", "#FEB47B"),
        ("ocean", "Océan", "#2193B0", "#6DD5ED"),
        ("neon", "Néon", "#FC00FF", "#00DBDE"),
        ("studio", "Studio", "#5A5A5A", "#1E1E1E"),
        ("vert", "Fond vert", "#00FF00", "#00FF00"),
    };

    private readonly PhoneClient _phone = new();
    private readonly AudioPlayer _audio = new();
    private readonly VirtualCamera _virtualCam = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Images : on ne garde que la plus récente pour limiter le retard.
    private byte[]? _latestFrame;
    private BitmapSource? _frameToShow;
    private int _decoding;
    private int _framesReceived;
    private int _framesShown;
    private long _lastWebcamFrameTicks;
    private BitmapSource? _placeholder;

    private bool _ready;
    private bool _wantConnected;
    private bool _updating;
    private bool _settingsDirty;
    private bool _fpsWarned;
    private CancellationTokenSource? _reconnectCts;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
        BuildThemeList();
        AudioDeviceBox.ItemsSource = AudioPlayer.ListDevices();
        ApplySettingsToControls();

        _phone.Log += message => Ui(() => AddLog(message));
        _phone.ConnectionChanged += connected => Ui(() => OnConnectionChanged(connected));
        _phone.HelloReceived += hello => Ui(() => OnHello(hello));
        _phone.StatusReceived += status => Ui(() => OnStatus(status));
        _phone.PingMeasured += ms => Ui(() => LatencyText.Text = $"{ms:0} ms (aller-retour)");
        _phone.FrameReceived += OnFrameReceived;
        _phone.AudioReceived += _audio.Play;
        _phone.PhotoReceived += OnPhotoReceived;
        _audio.Log += message => Ui(() => AddLog(message));

        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();
        Loaded += (_, _) =>
        {
            if (_settings.VirtualCamAutoStart && VirtualCamera.IsInstalled) StartVirtualCam();
        };

        _ready = true;
        UpdateAllLabels();
        UpdateModeUi();
        UpdateBackgroundUi();
        UpdateVirtualCamUi();
        AddLog("Prêt. Ouvre LynxCam sur le téléphone, choisis Wi-Fi ou câble USB, puis clique sur « Se connecter ».");
    }

    private void Ui(Action action) => Dispatcher.InvokeAsync(action);

    // ═════════════ Connexion ═════════════

    private string Mode => SelectedTag(ModeBox) ?? "wifi";
    private bool UsbMode => Mode != "wifi";

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.Mode = SelectedTag(ModeBox) ?? "wifi";
        MarkDirty();
        UpdateModeUi();
    }

    private void UpdateModeUi() => HostBox.Visibility = UsbMode ? Visibility.Collapsed : Visibility.Visible;

    private void ConnectionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_wantConnected) ConnectButton_Click(sender, e);
    }

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

        if (!UsbMode)
        {
            string host = HostBox.Text.Trim();
            if (host.Length == 0)
            {
                SetStatus("Saisis l'adresse IP affichée sur le téléphone.", "ErrorBrush");
                return;
            }
            _settings.Host = host;
        }
        _settings.Save();

        _wantConnected = true;
        UpdateConnectButton();
        SetStatus(UsbMode ? "Connexion par câble USB…" : $"Connexion à {_settings.Host}…", null);
        try
        {
            await ConnectOnceAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Premier essai raté : on s'arrête là.
            _wantConnected = false;
            UpdateConnectButton();
            SetStatus("Échec : " + ex.Message, "ErrorBrush");
            AddLog("Connexion impossible : " + ex.Message);
            AddLog(Mode switch
            {
                "usb" => "Vérifie : iPhone branché et déverrouillé, app LynxCam ouverte, iTunes installé.",
                "adb" => "Vérifie : câble branché, débogage USB activé et autorisé sur le téléphone, app LynxCam ouverte.",
                _ => "Vérifie : même Wi-Fi, app LynxCam ouverte sur le téléphone, accès au réseau local autorisé.",
            });
        }
    }

    private Task ConnectOnceAsync(CancellationToken ct) => Mode switch
    {
        "usb" => _phone.ConnectUsbAsync(_settings.Port, ct),
        "adb" => _phone.ConnectAndroidUsbAsync(_settings.Port, ct),
        _ => _phone.ConnectAsync(_settings.Host, _settings.Port, ct),
    };

    private void OnConnectionChanged(bool connected)
    {
        if (connected)
        {
            _reconnectCts?.Cancel();
            SetStatus($"Connecté ({_phone.Mode})", "ConnectedBrush");
            AddLog($"Connecté au téléphone ({_phone.Mode}).");
            SetControlsEnabled(true);
            SendAllSettings();
        }
        else
        {
            SetControlsEnabled(false);
            PreviewImage.Source = null;
            PlaceholderText.Visibility = Visibility.Visible;
            RecBadge.Visibility = Visibility.Collapsed;
            OverlayBorder.Visibility = Visibility.Collapsed;
            ResolutionText.Text = FpsText.Text = LatencyText.Text = BitrateText.Text = BatteryText.Text = "—";
            if (_wantConnected)
            {
                SetStatus("Connexion perdue, nouvelle tentative…", "ErrorBrush");
                ReconnectLoop();
            }
        }
        UpdateConnectButton();
    }

    /// <summary>Après une coupure, réessaie toutes les 2 secondes tant que l'utilisateur ne s'est pas déconnecté.</summary>
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
                await ConnectOnceAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus($"Reconnexion impossible ({ex.Message}). Nouvel essai dans 2 s…", "ErrorBrush");
            }
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        ActionBar.IsEnabled = enabled;
        VideoSection.IsEnabled = enabled;
        BackgroundSection.IsEnabled = enabled;
        ImageSection.IsEnabled = enabled;
    }

    private void UpdateConnectButton() =>
        ConnectButton.Content = _wantConnected ? "Se déconnecter" : "Se connecter";

    private void SetStatus(string text, string? brushKey)
    {
        var brush = (Brush)FindResource(brushKey ?? "MutedBrush");
        StatusText.Text = text;
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
    }

    // ═════════════ Envoi des réglages au téléphone ═════════════

    private void Send(string command, object value)
    {
        if (_phone.IsConnected) _ = _phone.SendCommandAsync(command, value);
    }

    private void MarkDirty() => _settingsDirty = true;

    /// <summary>À la connexion, le PC impose tous ses réglages au téléphone.</summary>
    private void SendAllSettings()
    {
        var s = _settings;
        Send("camera", s.Camera);
        Send("resolution", s.Resolution);
        Send("fps", s.Fps);
        Send("quality", s.Quality / 100.0);
        Send("background", s.Background);
        Send("blur", s.Blur);
        Send("theme", s.Theme);
        if (s.Background == "image") SendBackgroundPhoto();
        Send("exposure", s.Exposure);
        Send("focus", s.FocusAuto ? "auto" : (object)s.Focus);
        Send("whitebalance", s.WhiteBalanceAuto ? "auto" : (object)s.Kelvin);
        Send("brightness", s.Brightness);
        Send("contrast", s.Contrast);
        Send("saturation", s.Saturation);
        Send("mirror", s.Mirror);
        Send("mic", s.Mic);
        _audio.Enabled = s.Mic;
        _updating = true;
        ZoomSlider.Value = 1;
        TorchToggle.IsChecked = false;
        _updating = false;
        _fpsWarned = false;
        UpdateAllLabels();
    }

    // ═════════════ Messages du téléphone ═════════════

    private void OnHello(JsonElement hello)
    {
        string name = GetString(hello, "name") ?? "Téléphone";
        string system = GetString(hello, "system") ?? "";
        DeviceText.Text = name;
        DeviceText.ToolTip = $"{name} — {system}";
        AddLog($"Téléphone : {name} ({system})");
        if ((GetNumber(hello, "protocol") ?? 1) < 2)
            AddLog("L'app du téléphone est ancienne : réinstalle la dernière version de LynxCam pour avoir toutes les fonctions.");
    }

    private void OnStatus(JsonElement status)
    {
        double? battery = GetNumber(status, "battery");
        bool charging = GetBool(status, "charging") == true;
        BatteryText.Text = battery is >= 0 ? $"{battery * 100:0} %{(charging ? " (en charge)" : "")}" : "—";

        bool recording = GetBool(status, "recording") == true;
        int seconds = (int)(GetNumber(status, "recordSeconds") ?? 0);
        RecBadge.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecText.Text = $"REC {seconds / 60:00}:{seconds % 60:00}";
        RecordLabel.Text = recording ? "Arrêter" : "Enregistrer";

        int target = (int)(GetNumber(status, "fpsTarget") ?? _settings.Fps);
        int applied = (int)(GetNumber(status, "fpsApplied") ?? target);
        if (applied < target && !_fpsWarned)
        {
            _fpsWarned = true;
            AddLog($"Cet objectif ne fait que {applied} img/s dans cette résolution.");
        }

        // Les boutons du téléphone peuvent changer la caméra, la lampe, le micro ou l'enregistrement : on suit.
        _updating = true;
        if (GetString(status, "camera") is { } camera) SelectByTag(CameraBox, camera);
        if (GetBool(status, "torch") is { } torch) TorchToggle.IsChecked = torch;
        if (GetBool(status, "mic") is { } mic)
        {
            MicToggle.IsChecked = mic;
            MicBox.IsChecked = mic;
            _audio.Enabled = mic;
        }
        RecordToggle.IsChecked = recording;
        if (GetNumber(status, "zoom") is { } zoom && !ZoomSlider.IsMouseCaptureWithin && Math.Abs(zoom - ZoomSlider.Value) > 0.05)
            ZoomSlider.Value = Math.Clamp(zoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updating = false;
        UpdateAllLabels();
    }

    private void OnPhotoReceived(byte[] jpeg)
    {
        try
        {
            string path = NewPicturePath("photo");
            File.WriteAllBytes(path, jpeg);
            Ui(() => AddLog($"Photo enregistrée : {path} (et dans la galerie du téléphone)"));
        }
        catch (Exception ex)
        {
            Ui(() => AddLog("Photo non enregistrée sur le PC : " + ex.Message));
        }
    }

    private static string NewPicturePath(string kind)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LynxCam");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"LynxCam-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
    }

    // ═════════════ Images ═════════════

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
                if (frame != null)
                {
                    if (_virtualCam.IsRunning)
                    {
                        _virtualCam.SendFrame(frame);
                        Interlocked.Exchange(ref _lastWebcamFrameTicks, DateTime.UtcNow.Ticks);
                    }
                    if (Interlocked.Exchange(ref _frameToShow, frame) == null)
                        Dispatcher.InvokeAsync(ShowPendingFrame, DispatcherPriority.Render);
                }
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

    private void OnTimer()
    {
        int received = Interlocked.Exchange(ref _framesReceived, 0);
        int shown = _framesShown;
        _framesShown = 0;
        long bytes = _phone.TakeBytesReceived();

        if (_phone.IsConnected)
        {
            FpsText.Text = $"{received} reçues, {shown} affichées";
            BitrateText.Text = $"{bytes * 8 / 1_000_000.0:0.0} Mbit/s";
            OverlayText.Text = $"{received} img/s · {bytes * 8 / 1_000_000.0:0.0} Mbit/s · {_phone.Mode}";
            OverlayBorder.Visibility = Visibility.Visible;
        }

        // Sans image du téléphone, la webcam virtuelle affiche « En attente du téléphone ».
        if (_virtualCam.IsRunning)
        {
            var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastWebcamFrameTicks), DateTimeKind.Utc);
            if (idle > TimeSpan.FromSeconds(1.5)) SendPlaceholder();
            UpdateVirtualCamStatusText();
        }

        if (_settingsDirty)
        {
            _settingsDirty = false;
            _settings.Save();
        }
    }

    // ═════════════ Barre d'actions ═════════════

    private void SwitchCamera_Click(object sender, RoutedEventArgs e)
    {
        string next = SelectedTag(CameraBox) == "front" ? "back" : "front";
        SelectByTag(CameraBox, next); // envoie la commande via CameraBox_SelectionChanged
    }

    private void TorchToggle_Click(object sender, RoutedEventArgs e) => Send("torch", TorchToggle.IsChecked == true);

    private void MicToggle_Click(object sender, RoutedEventArgs e) => SetMicrophone(MicToggle.IsChecked == true);

    private void MicBox_Click(object sender, RoutedEventArgs e) => SetMicrophone(MicBox.IsChecked == true);

    private void SetMicrophone(bool on)
    {
        _settings.Mic = on;
        MarkDirty();
        _updating = true;
        MicToggle.IsChecked = on;
        MicBox.IsChecked = on;
        _updating = false;
        _audio.Enabled = on;
        Send("mic", on);
    }

    private void Photo_Click(object sender, RoutedEventArgs e) => Send("photo", true);

    private void RecordToggle_Click(object sender, RoutedEventArgs e) => Send("record", RecordToggle.IsChecked == true);

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource image)
        {
            AddLog("Aucune image à capturer.");
            return;
        }
        try
        {
            string path = NewPicturePath("capture");
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            encoder.Save(file);
            AddLog("Capture enregistrée : " + path);
        }
        catch (Exception ex)
        {
            AddLog("Capture impossible : " + ex.Message);
        }
    }

    // ═════════════ Webcam virtuelle ═════════════

    private void UpdateVirtualCamUi()
    {
        bool installed = VirtualCamera.IsInstalled;
        InstallCamButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        InstallCamButton.IsEnabled = VirtualCamera.IsBundled;
        UninstallCamButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        WebcamToggle.IsEnabled = installed;
        WebcamToggle.IsChecked = _virtualCam.IsRunning;
        WebcamToggle.Content = _virtualCam.IsRunning ? "Désactiver la webcam" : "Activer la webcam";
        UpdateVirtualCamStatusText();
    }

    private void UpdateVirtualCamStatusText()
    {
        VirtualCamStatus.Text =
            !VirtualCamera.IsBundled ? "Pas incluse dans cette version : utilise LynxCam téléchargé depuis GitHub (avec le dossier VirtualCam)."
            : !VirtualCamera.IsInstalled ? "Pas encore installée. Clique sur « Installer » (une seule fois : Windows demande l'autorisation)."
            : !_virtualCam.IsRunning ? "Installée. Clique sur « Activer la webcam »."
            : _virtualCam.IsAppConnected ? $"Active en {_virtualCam.Width} × {_virtualCam.Height}, utilisée par une app."
            : $"Active en {_virtualCam.Width} × {_virtualCam.Height}, en attente d'une app (OBS, Discord…).";
    }

    private async void InstallCam_Click(object sender, RoutedEventArgs e)
    {
        InstallCamButton.IsEnabled = false;
        AddLog("Installation de la webcam LynxCam…");
        string? error = await VirtualCamera.InstallAsync();
        AddLog(error == null ? "Webcam LynxCam installée." : "Installation impossible : " + error);
        UpdateVirtualCamUi();
        if (error == null) StartVirtualCam();
    }

    private async void UninstallCam_Click(object sender, RoutedEventArgs e)
    {
        StopVirtualCam();
        string? error = await VirtualCamera.UninstallAsync();
        AddLog(error == null ? "Webcam LynxCam désinstallée." : "Désinstallation impossible : " + error);
        UpdateVirtualCamUi();
    }

    private void WebcamToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WebcamToggle.IsChecked == true) StartVirtualCam();
        else StopVirtualCam();
    }

    private void StartVirtualCam()
    {
        (int width, int height) = _settings.VirtualCamResolution == "1080p" ? (1920, 1080) : (1280, 720);
        string? error = _virtualCam.Start(width, height);
        if (error != null)
        {
            AddLog("Webcam impossible à activer : " + error);
        }
        else
        {
            AddLog($"Webcam LynxCam active ({width} × {height}).");
            _placeholder = null;
            SendPlaceholder();
        }
        UpdateVirtualCamUi();
    }

    private void StopVirtualCam()
    {
        if (!_virtualCam.IsRunning) return;
        _virtualCam.Stop();
        AddLog("Webcam LynxCam désactivée.");
        UpdateVirtualCamUi();
    }

    private void SendPlaceholder()
    {
        if (!_virtualCam.IsRunning) return;
        _placeholder ??= MakePlaceholder(_virtualCam.Width, _virtualCam.Height);
        _virtualCam.SendFrame(_placeholder);
    }

    private BitmapSource MakePlaceholder(int width, int height)
    {
        var visual = new DrawingVisual();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        using (var dc = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0x14, 0x1E, 0x30), Color.FromRgb(0x24, 0x3B, 0x55), 45);
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            var title = new FormattedText("LynxCam", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), height / 9.0, Brushes.White, pixelsPerDip);
            dc.DrawText(title, new Point((width - title.Width) / 2, height / 2.0 - title.Height));
            var subtitle = new FormattedText("En attente du téléphone…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), height / 26.0, new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)), pixelsPerDip);
            dc.DrawText(subtitle, new Point((width - subtitle.Width) / 2, height / 2.0 + height / 40.0));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void VirtualCamResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        _settings.VirtualCamResolution = SelectedTag(VirtualCamResolutionBox) ?? "720p";
        MarkDirty();
        if (_virtualCam.IsRunning)
        {
            AddLog("Nouvelle taille de webcam : ferme puis rouvre la source dans OBS si l'image ne suit pas.");
            StartVirtualCam();
        }
    }

    private void VirtualCamAutoBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.VirtualCamAutoStart = VirtualCamAutoBox.IsChecked == true;
        MarkDirty();
    }

    // ═════════════ Vidéo ═════════════

    private void CameraBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || SelectedTag(CameraBox) is not { } camera) return;
        _settings.Camera = camera;
        MarkDirty();
        Send("camera", camera);
        _updating = true;
        ZoomSlider.Value = 1;
        TorchToggle.IsChecked = false;
        _updating = false;
        UpdateAllLabels();
    }

    private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || SelectedTag(ResolutionBox) is not { } resolution) return;
        _settings.Resolution = resolution;
        MarkDirty();
        _fpsWarned = false;
        Send("resolution", resolution);
    }

    private void FpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || !int.TryParse(SelectedTag(FpsBox), out int fps)) return;
        _settings.Fps = fps;
        MarkDirty();
        _fpsWarned = false;
        Send("fps", fps);
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        if (_updating) return;
        _settings.Quality = (int)QualitySlider.Value;
        MarkDirty();
        Send("quality", QualitySlider.Value / 100.0);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        if (_updating) return;
        Send("zoom", Math.Round(ZoomSlider.Value, 1));
    }

    // ═════════════ Arrière-plan ═════════════

    private void BuildThemeList()
    {
        foreach (var theme in Themes)
        {
            var from = (Color)ColorConverter.ConvertFromString(theme.From);
            var to = (Color)ColorConverter.ConvertFromString(theme.To);
            Brush fill = theme.Key == "studio"
                ? new RadialGradientBrush(from, to)
                : new LinearGradientBrush(from, to, new Point(0, 1), new Point(1, 0));
            var panel = new StackPanel { Width = 76 };
            panel.Children.Add(new Border { Height = 46, CornerRadius = new CornerRadius(6), Background = fill });
            panel.Children.Add(new TextBlock
            {
                Text = theme.Name,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            ThemeList.Items.Add(new ListBoxItem { Content = panel, Tag = theme.Key, Padding = new Thickness(4), Margin = new Thickness(2) });
        }
    }

    private void BackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateBackgroundUi();
        if (_updating || SelectedTag(BackgroundBox) is not { } mode) return;
        _settings.Background = mode;
        MarkDirty();
        if (mode == "image") SendBackgroundPhoto();
        Send("background", mode);
    }

    private void UpdateBackgroundUi()
    {
        string mode = SelectedTag(BackgroundBox) ?? "none";
        BlurPanel.Visibility = mode == "blur" ? Visibility.Visible : Visibility.Collapsed;
        ThemePanel.Visibility = mode == "theme" ? Visibility.Visible : Visibility.Collapsed;
        PhotoPanel.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        if (_updating) return;
        _settings.Blur = (int)BlurSlider.Value;
        MarkDirty();
        Send("blur", _settings.Blur);
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || (ThemeList.SelectedItem as ListBoxItem)?.Tag is not string theme) return;
        _settings.Theme = theme;
        MarkDirty();
        Send("theme", theme);
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Photo d'arrière-plan",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.BackgroundPhoto = dialog.FileName;
        MarkDirty();
        PhotoNameText.Text = Path.GetFileName(dialog.FileName);
        SendBackgroundPhoto();
        Send("background", "image");
    }

    /// <summary>Envoie la photo d'arrière-plan au téléphone, réduite à 1920 pixels maximum.</summary>
    private void SendBackgroundPhoto()
    {
        string path = _settings.BackgroundPhoto;
        if (!_phone.IsConnected) return;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AddLog("Choisis une photo pour l'arrière-plan.");
            return;
        }
        try
        {
            BitmapSource image;
            ushort orientation = 1;
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image = decoder.Frames[0];
                try
                {
                    if ((decoder.Frames[0].Metadata as BitmapMetadata)?.GetQuery("System.Photo.Orientation") is ushort o)
                        orientation = o;
                }
                catch (Exception)
                {
                    // Pas d'orientation dans ce fichier.
                }
            }

            double scale = Math.Min(1.0, 1920.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            int angle = orientation switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
            if (angle != 0) image = new TransformedBitmap(image, new RotateTransform(angle));

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            encoder.Save(output);
            _ = _phone.SendAsync(WireProtocol.BackgroundImage, output.ToArray());
            AddLog($"Photo d'arrière-plan envoyée ({output.Length / 1024} Ko).");
        }
        catch (Exception ex)
        {
            AddLog("Photo illisible : " + ex.Message);
        }
    }

    // ═════════════ Image ═════════════

    private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        if (_updating) return;
        _settings.Exposure = Math.Round(ExposureSlider.Value, 1);
        MarkDirty();
        Send("exposure", _settings.Exposure);
    }

    private void FocusAutoBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.FocusAuto = FocusAutoBox.IsChecked == true;
        FocusSlider.IsEnabled = !_settings.FocusAuto;
        MarkDirty();
        Send("focus", _settings.FocusAuto ? "auto" : (object)_settings.Focus);
    }

    private void FocusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating) return;
        _settings.Focus = Math.Round(FocusSlider.Value, 2);
        MarkDirty();
        if (!_settings.FocusAuto) Send("focus", _settings.Focus);
    }

    private void WhiteBalanceAutoBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.WhiteBalanceAuto = WhiteBalanceAutoBox.IsChecked == true;
        KelvinSlider.IsEnabled = !_settings.WhiteBalanceAuto;
        MarkDirty();
        Send("whitebalance", _settings.WhiteBalanceAuto ? "auto" : (object)_settings.Kelvin);
    }

    private void KelvinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        if (_updating) return;
        _settings.Kelvin = (int)KelvinSlider.Value;
        MarkDirty();
        if (!_settings.WhiteBalanceAuto) Send("whitebalance", _settings.Kelvin);
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        if (_updating) return;
        int value = (int)((Slider)sender).Value;
        if (ReferenceEquals(sender, BrightnessSlider)) { _settings.Brightness = value; Send("brightness", value); }
        else if (ReferenceEquals(sender, ContrastSlider)) { _settings.Contrast = value; Send("contrast", value); }
        else if (ReferenceEquals(sender, SaturationSlider)) { _settings.Saturation = value; Send("saturation", value); }
        MarkDirty();
    }

    private void MirrorBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.Mirror = MirrorBox.IsChecked == true;
        MarkDirty();
        Send("mirror", _settings.Mirror);
    }

    private void ResetImage_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings;
        s.Exposure = 0;
        s.FocusAuto = true;
        s.WhiteBalanceAuto = true;
        s.Brightness = s.Contrast = s.Saturation = 0;
        s.Mirror = false;
        MarkDirty();
        ApplySettingsToControls();
        UpdateAllLabels();
        Send("exposure", 0);
        Send("focus", "auto");
        Send("whitebalance", "auto");
        Send("brightness", 0);
        Send("contrast", 0);
        Send("saturation", 0);
        Send("mirror", false);
    }

    // ═════════════ Son ═════════════

    private void AudioDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AudioDeviceBox.SelectedItem is not KeyValuePair<int, string> device) return;
        _settings.AudioDevice = device.Key;
        MarkDirty();
        _audio.DeviceNumber = device.Key;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateAllLabels();
        _settings.Volume = (int)VolumeSlider.Value;
        MarkDirty();
        _audio.Volume = (float)(VolumeSlider.Value / 100.0);
    }

    // ═════════════ Outils ═════════════

    /// <summary>Recopie les réglages sauvegardés dans les contrôles, sans rien envoyer au téléphone.</summary>
    private void ApplySettingsToControls()
    {
        var s = _settings;
        _updating = true;
        SelectByTag(ModeBox, s.Mode);
        HostBox.Text = s.Host;
        SelectByTag(CameraBox, s.Camera);
        SelectByTag(ResolutionBox, s.Resolution);
        SelectByTag(FpsBox, s.Fps.ToString(CultureInfo.InvariantCulture));
        QualitySlider.Value = s.Quality;
        SelectByTag(BackgroundBox, s.Background);
        BlurSlider.Value = s.Blur;
        ThemeList.SelectedItem = ThemeList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == s.Theme)
                                 ?? ThemeList.Items.OfType<ListBoxItem>().FirstOrDefault();
        PhotoNameText.Text = string.IsNullOrEmpty(s.BackgroundPhoto) ? "Aucune photo choisie" : Path.GetFileName(s.BackgroundPhoto);
        ExposureSlider.Value = s.Exposure;
        FocusAutoBox.IsChecked = s.FocusAuto;
        FocusSlider.Value = s.Focus;
        FocusSlider.IsEnabled = !s.FocusAuto;
        WhiteBalanceAutoBox.IsChecked = s.WhiteBalanceAuto;
        KelvinSlider.Value = s.Kelvin;
        KelvinSlider.IsEnabled = !s.WhiteBalanceAuto;
        BrightnessSlider.Value = s.Brightness;
        ContrastSlider.Value = s.Contrast;
        SaturationSlider.Value = s.Saturation;
        MirrorBox.IsChecked = s.Mirror;
        MicBox.IsChecked = s.Mic;
        MicToggle.IsChecked = s.Mic;
        VolumeSlider.Value = s.Volume;
        var devices = AudioDeviceBox.Items.OfType<KeyValuePair<int, string>>().ToList();
        int deviceIndex = devices.FindIndex(d => d.Key == s.AudioDevice);
        AudioDeviceBox.SelectedIndex = deviceIndex >= 0 ? deviceIndex : 0;
        SelectByTag(VirtualCamResolutionBox, s.VirtualCamResolution);
        VirtualCamAutoBox.IsChecked = s.VirtualCamAutoStart;
        _updating = false;

        _audio.DeviceNumber = s.AudioDevice;
        _audio.Volume = s.Volume / 100f;
    }

    private void UpdateAllLabels()
    {
        QualityLabel.Text = $"Qualité : {QualitySlider.Value:0} %";
        ZoomLabel.Text = $"Zoom : ×{ZoomSlider.Value:0.0}";
        BlurLabel.Text = $"Force du flou : {BlurSlider.Value:0}";
        ExposureLabel.Text = $"Exposition : {ExposureSlider.Value:+0.0;-0.0;0.0}";
        KelvinLabel.Text = $"Température : {KelvinSlider.Value:0} K";
        BrightnessLabel.Text = $"Luminosité : {BrightnessSlider.Value:+0;-0;0}";
        ContrastLabel.Text = $"Contraste : {ContrastSlider.Value:+0;-0;0}";
        SaturationLabel.Text = $"Saturation : {SaturationSlider.Value:+0;-0;0}";
        VolumeLabel.Text = $"Volume : {VolumeSlider.Value:0} %";
    }

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

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _wantConnected = false;
        _reconnectCts?.Cancel();
        _timer.Stop();
        _settings.Save();
        _virtualCam.Dispose();
        _audio.Dispose();
        _phone.Dispose();
    }
}
