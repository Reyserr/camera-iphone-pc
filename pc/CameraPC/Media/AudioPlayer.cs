using System.Collections.Generic;
using NAudio.Wave;
using CameraPC.Net;

namespace CameraPC.Media;

/// <summary>
/// Joue le son du micro de l'iPhone sur la sortie audio choisie.
/// Pour OBS : source « Capture audio d'application » sur LynxCam.
/// Pour Discord : choisir la sortie « CABLE Input » (VB-Audio Virtual Cable).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private (int Rate, int Channels, int Bits, bool IsFloat) _format;
    private int _deviceNumber = -1;
    private float _volume = 1f;
    private bool _enabled;
    private bool _failed;

    public event Action<string>? Log;

    /// <summary>Sorties audio de Windows (-1 = sortie par défaut).</summary>
    public static List<KeyValuePair<int, string>> ListDevices()
    {
        var devices = new List<KeyValuePair<int, string>> { new(-1, "Sortie par défaut de Windows") };
        try
        {
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                devices.Add(new(i, WaveOut.GetCapabilities(i).ProductName));
        }
        catch (Exception)
        {
            // Pas de carte son : on garde la sortie par défaut.
        }
        return devices;
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                _enabled = value;
                _failed = false;
                if (!value) StopLocked();
            }
        }
    }

    public int DeviceNumber
    {
        set
        {
            lock (_gate)
            {
                if (_deviceNumber == value) return;
                _deviceNumber = value;
                _failed = false;
                StopLocked(); // redémarre au prochain morceau de son
            }
        }
    }

    /// <summary>Volume de 0 à 1.</summary>
    public float Volume
    {
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_output != null) _output.Volume = _volume;
            }
        }
    }

    public void Play(AudioPacket packet)
    {
        lock (_gate)
        {
            if (!_enabled || _failed || packet.Count <= 0) return;
            var format = (packet.SampleRate, packet.Channels, packet.BitsPerSample, packet.IsFloat);
            if (_output == null || format != _format)
            {
                StopLocked();
                try
                {
                    var waveFormat = packet.IsFloat
                        ? WaveFormat.CreateIeeeFloatWaveFormat(packet.SampleRate, packet.Channels)
                        : new WaveFormat(packet.SampleRate, packet.BitsPerSample, packet.Channels);
                    _buffer = new BufferedWaveProvider(waveFormat)
                    {
                        BufferDuration = TimeSpan.FromSeconds(1),
                        DiscardOnBufferOverflow = true,
                    };
                    _output = new WaveOutEvent { DeviceNumber = _deviceNumber, DesiredLatency = 120 };
                    _output.Init(_buffer);
                    _output.Volume = _volume;
                    _output.Play();
                    _format = format;
                    Log?.Invoke($"Son de l'iPhone : {packet.SampleRate} Hz, {packet.Channels} voie(s)");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    StopLocked();
                    Log?.Invoke("Lecture du son impossible : " + ex.Message);
                    return;
                }
            }

            // Si du retard s'accumule, on le supprime pour rester synchro avec l'image.
            if (_buffer!.BufferedDuration > TimeSpan.FromMilliseconds(350)) _buffer.ClearBuffer();
            _buffer.AddSamples(packet.Data, packet.Offset, packet.Count);
        }
    }

    private void StopLocked()
    {
        try { _output?.Stop(); } catch { /* déjà arrêté */ }
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public void Dispose()
    {
        lock (_gate) StopLocked();
    }
}
