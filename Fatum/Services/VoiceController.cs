using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using Fatum.Windows;

namespace Fatum.Services;

public enum RecordingState
{
    Idle,
    Recording,
    Transcribing,
}

/// <summary>
/// Drives the whole flow. In key modes it polls the hotkeys each frame; in voice-activity
/// mode it listens continuously and auto-segments on speech/silence. Either way it
/// transcribes off-thread and hands the text to the review window (or auto-sends).
/// </summary>
public sealed class VoiceController : IDisposable
{
    private readonly Configuration config;
    private readonly ReviewWindow reviewWindow;
    private readonly MicrophoneCapture mic = new();
    private readonly WhisperTranscriber transcriber = new();

    private readonly Dictionary<VirtualKey, bool> prevDown = new();
    private VirtualKey recordingKey = VirtualKey.NO_KEY;
    private ChatChannel recordingChannel;
    private DateTime recordStart;
    private volatile bool busy;

    // Voice-activity state (all touched only on the capture thread once running).
    private bool vadListening;
    private volatile bool vadSpeaking;
    private readonly List<float> vadSegment = new();
    private double vadSegmentMs;
    private double vadSilenceMs;

    public VoiceController(Configuration config, ReviewWindow reviewWindow)
    {
        this.config = config;
        this.reviewWindow = reviewWindow;
    }

    /// <summary>Current state, for the on-screen overlay.</summary>
    public RecordingState State
    {
        get
        {
            if (busy) return RecordingState.Transcribing;
            if (config.Trigger == TriggerMode.VoiceActivity)
                return vadSpeaking ? RecordingState.Recording : RecordingState.Idle;
            return mic.IsRecording ? RecordingState.Recording : RecordingState.Idle;
        }
    }

    /// <summary>Channel the in-flight clip is headed to.</summary>
    public ChatChannel ActiveChannel => recordingChannel;

    private ChatChannel VadChannel =>
        config.Bindings.Count > 0 ? config.Bindings[0].Channel : ChatChannel.Say;

    public void OnFrameworkUpdate(IFramework framework)
    {
        if (config.Trigger == TriggerMode.VoiceActivity)
        {
            EnsureVadRunning();
            return;
        }

        EnsureVadStopped();
        PollHotkeys();
    }

    // ---- Push-to-talk / Toggle -------------------------------------------------

    private void PollHotkeys()
    {
        var recordingReleased = false;
        var recordingPressedAgain = false;
        var firstPressed = VirtualKey.NO_KEY;
        var firstPressedChannel = default(ChatChannel);

        foreach (var binding in config.Bindings)
        {
            var down = Plugin.KeyState[binding.Key];
            prevDown.TryGetValue(binding.Key, out var was);
            var pressed = down && !was;
            var released = !down && was;
            prevDown[binding.Key] = down;

            if (mic.IsRecording && binding.Key == recordingKey)
            {
                recordingReleased = released;
                recordingPressedAgain = pressed;
            }
            else if (firstPressed == VirtualKey.NO_KEY && pressed)
            {
                firstPressed = binding.Key;
                firstPressedChannel = binding.Channel;
            }
        }

        if (mic.IsRecording)
        {
            var stop = config.Trigger switch
            {
                TriggerMode.PushToTalk => recordingReleased,
                TriggerMode.Toggle => recordingPressedAgain,
                _ => false,
            };
            if (stop) StopAndTranscribe();
            return;
        }

        if (busy) return;

        if (firstPressed != VirtualKey.NO_KEY)
        {
            recordingKey = firstPressed;
            recordingChannel = firstPressedChannel;
            StartRecording();
        }
    }

    private void StartRecording()
    {
        if (busy || mic.IsRecording) return;
        try
        {
            mic.Start(config.InputDeviceNumber);
            recordStart = DateTime.UtcNow;
            Plugin.ChatGui.Print($"[Fatum] Listening... ({recordingChannel})");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to start microphone");
            Plugin.ChatGui.PrintError($"[Fatum] Mic error: {ex.Message}");
        }
    }

    private void StopAndTranscribe()
    {
        if (!mic.IsRecording) return;

        var samples = mic.Stop();
        var channel = recordingChannel;
        recordingKey = VirtualKey.NO_KEY;

        var seconds = (DateTime.UtcNow - recordStart).TotalSeconds;
        if (seconds < config.MinRecordSeconds || samples.Length == 0)
        {
            Plugin.ChatGui.Print("[Fatum] (too short, ignored)");
            return;
        }

        Deliver(samples, channel);
    }

    // ---- Voice-activity (hands-free) -------------------------------------------

    private void EnsureVadRunning()
    {
        if (vadListening) return;
        try
        {
            recordingChannel = VadChannel;
            vadSpeaking = false;
            vadSegment.Clear();
            vadSegmentMs = 0;
            vadSilenceMs = 0;
            mic.ChunkAvailable += OnVadChunk;
            mic.Start(config.InputDeviceNumber, accumulateBuffer: false);
            vadListening = true;
            Plugin.ChatGui.Print($"[Fatum] Voice-activated listening on ({VadChannel}). Just talk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to start voice-activated listening");
            Plugin.ChatGui.PrintError($"[Fatum] Mic error: {ex.Message}");
            EnsureVadStopped();
        }
    }

    private void EnsureVadStopped()
    {
        if (!vadListening) return;
        mic.ChunkAvailable -= OnVadChunk;
        mic.Stop();
        vadListening = false;
        vadSpeaking = false;
        vadSegment.Clear();
    }

    private void OnVadChunk(float[] chunk)
    {
        // Don't capture a new phrase while the previous one is still transcribing.
        if (busy || chunk.Length == 0) return;

        double sum = 0;
        foreach (var s in chunk) sum += (double)s * s;
        var rms = Math.Sqrt(sum / chunk.Length);
        var chunkMs = chunk.Length * 1000.0 / 16000.0;

        if (rms >= config.VadThreshold)
        {
            if (!vadSpeaking)
            {
                vadSpeaking = true;
                vadSegment.Clear();
                vadSegmentMs = 0;
            }
            vadSilenceMs = 0;
            vadSegment.AddRange(chunk);
            vadSegmentMs += chunkMs;
        }
        else if (vadSpeaking)
        {
            // Keep a little trailing audio so word endings aren't clipped.
            vadSegment.AddRange(chunk);
            vadSegmentMs += chunkMs;
            vadSilenceMs += chunkMs;
            if (vadSilenceMs >= config.VadSilenceMs)
                FlushVadSegment();
        }

        // Safety cap so a noisy room can't buffer forever.
        if (vadSpeaking && vadSegmentMs > 20000)
            FlushVadSegment();
    }

    private void FlushVadSegment()
    {
        var speechMs = vadSegmentMs - vadSilenceMs;
        var samples = vadSegment.ToArray();
        vadSpeaking = false;
        vadSegment.Clear();
        vadSegmentMs = 0;
        vadSilenceMs = 0;

        if (speechMs >= config.MinRecordSeconds * 1000 && samples.Length > 0)
            Deliver(samples, VadChannel);
    }

    // ---- Shared transcribe + deliver -------------------------------------------

    private void Deliver(float[] samples, ChatChannel channel)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        Plugin.Log.Info(
            $"[Fatum] Captured {samples.Length} samples ({samples.Length / 16000f:F2}s), peak {peak:F4} → {channel}");

        busy = true;
        Plugin.ChatGui.Print("[Fatum] Transcribing...");

        _ = Task.Run(async () =>
        {
            try
            {
                var text = await transcriber
                    .TranscribeAsync(samples, config.ModelPath, config.Language, CancellationToken.None)
                    .ConfigureAwait(false);

                if (config.StripTrailingPeriod && text.EndsWith("."))
                    text = text[..^1];

                if (string.IsNullOrWhiteSpace(text))
                {
                    Plugin.ChatGui.Print("[Fatum] Heard nothing — speak a full phrase (~1s+).");
                    return;
                }

                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (config.AutoSend)
                        reviewWindow.SendDirect(text, channel);
                    else
                        reviewWindow.ShowFor(text, channel);
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Transcription failed");
                Plugin.ChatGui.PrintError($"[Fatum] {ex.Message}");
            }
            finally
            {
                busy = false;
            }
        });
    }

    public void Dispose()
    {
        EnsureVadStopped();
        mic.Dispose();
        transcriber.Dispose();
    }
}
