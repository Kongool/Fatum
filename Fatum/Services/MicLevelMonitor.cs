using System;
using NAudio.Wave;

namespace Fatum.Services;

/// <summary>
/// A lightweight, independent mic tap used only to drive the level meter in the config
/// window. It opens its own capture so it never interferes with the record path, and
/// exposes a smoothed 0..1 peak level.
/// </summary>
public sealed class MicLevelMonitor : IDisposable
{
    private WaveInEvent? waveIn;
    private volatile float level;

    /// <summary>Current smoothed level, 0..1.</summary>
    public float Level => level;

    public bool IsRunning => waveIn is not null;

    /// <summary>Device this monitor is currently open on (matches config values; -2 = none).</summary>
    public int DeviceNumber { get; private set; } = -2;

    public void Start(int deviceNumber)
    {
        Stop();
        try
        {
            waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
            };
            waveIn.DataAvailable += OnData;
            waveIn.StartRecording();
            DeviceNumber = deviceNumber;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Mic level monitor failed to start");
            Stop();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float peak = 0f;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            var v = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            var a = Math.Abs(v) / 32768f;
            if (a > peak) peak = a;
        }

        // Fast attack, slow release so the bar is readable.
        var current = level;
        level = peak > current ? peak : current * 0.85f;
    }

    public void Stop()
    {
        if (waveIn is null) return;
        try { waveIn.StopRecording(); } catch { /* ignore */ }
        waveIn.DataAvailable -= OnData;
        waveIn.Dispose();
        waveIn = null;
        level = 0f;
        DeviceNumber = -2;
    }

    public void Dispose() => Stop();
}
