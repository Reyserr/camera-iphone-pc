using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CameraPC.Net;

/// <summary>
/// Connexion vers l'app iPhone, en Wi-Fi (adresse IP) ou par câble USB : reçoit les images, le son et l'état,
/// envoie les commandes. Les événements sont levés sur un fil de fond (pas sur le fil de l'interface).
/// </summary>
public sealed class PhoneClient : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private long _bytesReceived;

    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;
    public event Action<JsonElement>? HelloReceived;
    public event Action<JsonElement>? StatusReceived;
    /// <summary>Image reçue : message complet (l'image JPEG commence à l'octet 8) et horodatage iPhone en ms.</summary>
    public event Action<byte[], ulong>? FrameReceived;
    public event Action<AudioPacket>? AudioReceived;
    /// <summary>Photo prise par l'iPhone (JPEG pleine qualité).</summary>
    public event Action<byte[]>? PhotoReceived;
    /// <summary>Temps d'aller-retour mesuré, en millisecondes.</summary>
    public event Action<double>? PingMeasured;

    public bool IsConnected
    {
        get { lock (_gate) return _client != null; }
    }

    /// <summary>« Wi-Fi » ou « USB » pendant une connexion.</summary>
    public string Mode { get; private set; } = "";

    public long TakeBytesReceived() => Interlocked.Exchange(ref _bytesReceived, 0);

    /// <summary>Connexion Wi-Fi à l'adresse IP affichée par l'iPhone.</summary>
    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync("Wi-Fi", $"{host}:{port}", async ct =>
        {
            var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
            try
            {
                await client.ConnectAsync(host, port, ct);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, cancellationToken);

    /// <summary>Connexion par câble USB au premier iPhone branché.</summary>
    public Task ConnectUsbAsync(int port, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync("USB", "USB", async ct =>
        {
            var devices = await UsbMux.ListDevicesAsync(ct);
            var device = devices.FirstOrDefault()
                         ?? throw new IOException("aucun iPhone branché en USB (déverrouille-le et touche « Se fier »).");
            return await UsbMux.ConnectAsync(device.Id, port, ct);
        }, cancellationToken);

    private async Task ConnectCoreAsync(string mode, string target, Func<CancellationToken, Task<TcpClient>> open,
                                        CancellationToken cancellationToken)
    {
        Disconnect();

        TcpClient client;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                client = await open(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"pas de réponse de {target} après 6 secondes.");
            }
        }

        var cts = new CancellationTokenSource();
        NetworkStream stream;
        lock (_gate)
        {
            _client = client;
            _stream = stream = client.GetStream();
            _cts = cts;
            Mode = mode;
        }

        ConnectionChanged?.Invoke(true);
        _ = Task.Run(() => ReceiveLoopAsync(client, stream, cts.Token));
        _ = Task.Run(() => PingLoopAsync(cts.Token));
    }

    public void Disconnect()
    {
        TcpClient? client;
        lock (_gate) client = _client;
        if (client != null) Close(client);
    }

    public Task SendCommandAsync(string command, object value) =>
        SendAsync(WireProtocol.Command, JsonSerializer.SerializeToUtf8Bytes(new { cmd = command, value }));

    public async Task SendAsync(byte type, ReadOnlyMemory<byte> payload)
    {
        NetworkStream? stream;
        lock (_gate) stream = _stream;
        if (stream == null) return;

        var header = new byte[WireProtocol.HeaderSize];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        header[4] = type;

        await _sendLock.WaitAsync();
        try
        {
            await stream.WriteAsync(header);
            if (!payload.IsEmpty) await stream.WriteAsync(payload);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Log?.Invoke("Envoi impossible : " + ex.Message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[WireProtocol.HeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                byte type = header[4];
                if (length < 0 || length > WireProtocol.MaxPayload)
                    throw new InvalidDataException($"taille de message invalide : {length}");

                var payload = new byte[length];
                if (length > 0) await stream.ReadExactlyAsync(payload, ct);
                Interlocked.Add(ref _bytesReceived, WireProtocol.HeaderSize + length);
                Dispatch(type, payload);
            }
        }
        catch (OperationCanceledException)
        {
            // Déconnexion demandée.
        }
        catch (EndOfStreamException)
        {
            Log?.Invoke("L'iPhone a fermé la connexion.");
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log?.Invoke("Connexion perdue : " + ex.Message);
        }
        finally
        {
            Close(client);
        }
    }

    private void Dispatch(byte type, byte[] payload)
    {
        switch (type)
        {
            case WireProtocol.FrameJpeg when payload.Length > 8:
                FrameReceived?.Invoke(payload, BinaryPrimitives.ReadUInt64BigEndian(payload));
                break;
            case WireProtocol.AudioPcm when payload.Length > 8:
                AudioReceived?.Invoke(new AudioPacket(
                    SampleRate: (int)BinaryPrimitives.ReadUInt32BigEndian(payload),
                    Channels: Math.Max(1, (int)payload[4]),
                    BitsPerSample: payload[5],
                    IsFloat: (payload[6] & 1) != 0,
                    Data: payload, Offset: 8, Count: payload.Length - 8));
                break;
            case WireProtocol.Photo when payload.Length > 0:
                PhotoReceived?.Invoke(payload);
                break;
            case WireProtocol.Hello:
                if (TryParseJson(payload, out var hello)) HelloReceived?.Invoke(hello);
                break;
            case WireProtocol.Status:
                if (TryParseJson(payload, out var status)) StatusReceived?.Invoke(status);
                break;
            case WireProtocol.Log:
                Log?.Invoke("[iPhone] " + Encoding.UTF8.GetString(payload));
                break;
            case WireProtocol.Pong when payload.Length == 8:
                long sent = BinaryPrimitives.ReadInt64BigEndian(payload);
                PingMeasured?.Invoke(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                break;
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var payload = new byte[8];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                BinaryPrimitives.WriteInt64BigEndian(payload, Stopwatch.GetTimestamp());
                await SendAsync(WireProtocol.Ping, payload);
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool TryParseJson(byte[] payload, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    /// <summary>Ferme la connexion donnée, seulement si c'est encore la connexion active.</summary>
    private void Close(TcpClient client)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _client = null;
            _stream = null;
        }
        try { client.Close(); } catch { /* déjà fermée */ }
        ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }
}
