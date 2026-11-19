namespace CameraPC.Net;

/// <summary>
/// Types de messages échangés avec l'app iPhone (voir docs/protocole.md).
/// Format : [taille des données : 4 octets gros-boutiste][type : 1 octet][données]
/// </summary>
public static class WireProtocol
{
    public const int DefaultPort = 5050;
    public const int HeaderSize = 5;
    public const int MaxPayload = 16_000_000;

    public const byte Hello = 0x01;
    public const byte FrameJpeg = 0x02;
    public const byte FrameH264 = 0x03;
    public const byte AudioPcm = 0x04;
    public const byte Command = 0x10;
    public const byte Status = 0x11;
    public const byte Log = 0x12;
    public const byte BackgroundImage = 0x13;
    public const byte Photo = 0x14;
    public const byte Ping = 0x20;
    public const byte Pong = 0x21;
}

/// <summary>Morceau de son reçu du micro de l'iPhone.</summary>
public sealed record AudioPacket(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, byte[] Data, int Offset, int Count);
