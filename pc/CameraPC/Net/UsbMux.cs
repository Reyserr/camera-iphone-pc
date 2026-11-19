using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace CameraPC.Net;

/// <summary>
/// Connexion à l'iPhone par câble USB, via le service « Apple Mobile Device » installé avec iTunes.
/// Ce service écoute sur 127.0.0.1:27015 (protocole usbmux) et ouvre un tunnel vers un port de l'iPhone.
/// </summary>
public static class UsbMux
{
    public const int ServicePort = 27015;
    private const int PlistMessage = 8;

    public sealed record Device(long Id, string Serial);

    /// <summary>Liste les iPhone branchés en USB.</summary>
    public static async Task<List<Device>> ListDevicesAsync(CancellationToken ct)
    {
        using var client = await ConnectServiceAsync(ct);
        var stream = client.GetStream();
        await SendAsync(stream, 1, new Dictionary<string, object>
        {
            ["MessageType"] = "ListDevices",
            ["ClientVersionString"] = "LynxCam",
            ["ProgName"] = "LynxCam",
            ["kLibUSBMuxVersion"] = 3,
        }, ct);
        var reply = await ReceiveAsync(stream, ct);

        var devices = new List<Device>();
        if (reply.TryGetValue("DeviceList", out var list) && list is List<object> entries)
        {
            foreach (var entry in entries.OfType<Dictionary<string, object>>())
            {
                if (entry.TryGetValue("Properties", out var p) && p is Dictionary<string, object> props
                    && props.TryGetValue("ConnectionType", out var type) && type as string == "USB"
                    && props.TryGetValue("DeviceID", out var id) && id is long deviceId)
                {
                    devices.Add(new Device(deviceId, props.TryGetValue("SerialNumber", out var s) ? s as string ?? "" : ""));
                }
            }
        }
        return devices;
    }

    /// <summary>Ouvre un tunnel vers le port TCP donné de l'iPhone. Le TcpClient renvoyé se lit et s'écrit comme une connexion normale.</summary>
    public static async Task<TcpClient> ConnectAsync(long deviceId, int port, CancellationToken ct)
    {
        var client = await ConnectServiceAsync(ct);
        try
        {
            var stream = client.GetStream();
            // Le port est attendu dans l'ordre réseau (octets inversés).
            int networkPort = ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);
            await SendAsync(stream, 2, new Dictionary<string, object>
            {
                ["MessageType"] = "Connect",
                ["DeviceID"] = deviceId,
                ["PortNumber"] = networkPort,
                ["ClientVersionString"] = "LynxCam",
                ["ProgName"] = "LynxCam",
            }, ct);
            var reply = await ReceiveAsync(stream, ct);
            long result = reply.TryGetValue("Number", out var n) && n is long number ? number : -1;
            if (result != 0)
            {
                throw new IOException(result switch
                {
                    3 => "l'iPhone refuse la connexion : ouvre l'app LynxCam sur l'iPhone.",
                    2 => "l'iPhone n'est plus branché.",
                    _ => $"erreur USB {result}.",
                });
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> ConnectServiceAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        try
        {
            await client.ConnectAsync("127.0.0.1", ServicePort, ct);
            return client;
        }
        catch (SocketException)
        {
            client.Dispose();
            throw new IOException("service Apple introuvable : installe iTunes (version du site Apple) puis redémarre le PC.");
        }
    }

    // ───────────── Messages usbmux : en-tête de 16 octets + plist XML ─────────────

    private static async Task SendAsync(NetworkStream stream, int tag, Dictionary<string, object> message, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(WritePlist(message));
        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0), 16 + body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);           // version : plist
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), PlistMessage);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), tag);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }

    private static async Task<Dictionary<string, object>> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 16 || length > 4_000_000) throw new IOException("réponse USB invalide.");
        var body = new byte[length - 16];
        await stream.ReadExactlyAsync(body, ct);
        return ReadPlist(body);
    }

    internal static string WritePlist(Dictionary<string, object> values)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        foreach (var (key, value) in values)
        {
            sb.Append("\t<key>").Append(SecurityElement.Escape(key)).Append("</key>\n");
            sb.Append(value switch
            {
                string s => $"\t<string>{SecurityElement.Escape(s)}</string>\n",
                bool b => b ? "\t<true/>\n" : "\t<false/>\n",
                int i => $"\t<integer>{i}</integer>\n",
                long l => $"\t<integer>{l}</integer>\n",
                _ => throw new ArgumentException("type non géré : " + value.GetType()),
            });
        }
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    internal static Dictionary<string, object> ReadPlist(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(reader);
        var dict = doc.DocumentElement?.SelectSingleNode("dict") as XmlElement
                   ?? throw new IOException("réponse USB illisible.");
        return (Dictionary<string, object>)ParseValue(dict);
    }

    private static object ParseValue(XmlElement element)
    {
        switch (element.Name)
        {
            case "dict":
                var dict = new Dictionary<string, object>();
                string? key = null;
                foreach (var child in element.ChildNodes.OfType<XmlElement>())
                {
                    if (child.Name == "key") key = child.InnerText;
                    else if (key != null)
                    {
                        dict[key] = ParseValue(child);
                        key = null;
                    }
                }
                return dict;
            case "array":
                return element.ChildNodes.OfType<XmlElement>().Select(ParseValue).ToList();
            case "integer":
                return long.TryParse(element.InnerText.Trim(), out var l) ? l : 0L;
            case "real":
                return double.TryParse(element.InnerText.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                return Convert.FromBase64String(element.InnerText.Trim());
            default:
                return element.InnerText;
        }
    }
}
