using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Fatum.Services;

/// <summary>
/// Captures microphone audio at 16 kHz mono 16-bit PCM (exactly what Whisper wants)
/// and returns it as a float[] when recording stops.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private WaveInEvent? waveIn;
    private readonly List<byte> buffer = new();
    private readonly object gate = new();
    private bool accumulate = true;

    public bool IsRecording { get; private set; }

    /// <summary>Fires per captured chunk with 16 kHz mono float samples (for VAD).</summary>
    public event Action<float[]>? ChunkAvailable;

    /// <summary>Enumerate available input devices as (index, name). Index -1 is the system default.</summary>
    public static IEnumerable<(int Index, string Name)> ListDevices()
    {
        yield return (-1, "System default");
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            yield return (i, caps.ProductName);
        }
    }

    public void Start(int deviceNumber, bool accumulateBuffer = true)
    {
        if (IsRecording) return;

        accumulate = accumulateBuffer;
        lock (gate) buffer.Clear();

        waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
        };
        waveIn.DataAvailable += OnDataAvailable;
        waveIn.StartRecording();
        IsRecording = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Convert this chunk to float samples for any streaming listener (VAD).
        var handler = ChunkAvailable;
        if (handler is not null)
        {
            var chunk = new float[e.BytesRecorded / 2];
            for (var i = 0; i < chunk.Length; i++)
            {
                var s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                chunk[i] = s / 32768f;
            }
            handler(chunk);
        }

        if (accumulate)
        {
            lock (gate)
            {
                for (var i = 0; i < e.BytesRecorded; i++)
                    buffer.Add(e.Buffer[i]);
            }
        }
    }

    /// <summary>Stops recording and returns the captured audio as 16 kHz mono float samples.</summary>
    public float[] Stop()
    {
        if (!IsRecording || waveIn is null)
            return Array.Empty<float>();

        waveIn.StopRecording();
        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.Dispose();
        waveIn = null;
        IsRecording = false;

        byte[] pcm;
        lock (gate) pcm = buffer.ToArray();

        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    public void Dispose()
    {
        if (waveIn is null) return;
        try { waveIn.StopRecording(); } catch { /* ignore */ }
        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.Dispose();
        waveIn = null;
        IsRecording = false;
    }
}
