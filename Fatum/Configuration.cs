using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;

namespace Fatum;

/// <summary>A single key bound to a chat channel. Press it to record and route there.</summary>
[Serializable]
public class HotkeyBinding
{
    public VirtualKey Key { get; set; } = VirtualKey.V;
    public ChatChannel Channel { get; set; } = ChatChannel.Say;

    public HotkeyBinding() { }

    public HotkeyBinding(VirtualKey key, ChatChannel channel)
    {
        Key = key;
        Channel = channel;
    }
}

/// <summary>How recording is started/stopped.</summary>
public enum TriggerMode
{
    /// <summary>Hold the hotkey to record, release to transcribe.</summary>
    PushToTalk,

    /// <summary>Press once to start, press again to stop.</summary>
    Toggle,

    /// <summary>No key — continuously listen and auto-segment on speech/silence.</summary>
    VoiceActivity,
}

/// <summary>Which chat channel the transcript is sent to on confirm.</summary>
public enum ChatChannel
{
    Say,
    Yell,
    Shout,
    Party,
    Alliance,
    FreeCompany,
    Echo,
    Linkshell1,
    Linkshell2,
    Linkshell3,
    Linkshell4,
    Linkshell5,
    Linkshell6,
    Linkshell7,
    Linkshell8,
    CrossWorldLinkshell1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Trigger ---
    public TriggerMode Trigger { get; set; } = TriggerMode.PushToTalk;

    /// <summary>
    /// Each bound key records and routes its transcript to its own channel.
    /// Defaults to empty; a starter binding is seeded in Plugin if none exist. (A
    /// non-empty initializer here would be *appended to* by the config deserializer
    /// on every load, duplicating bindings.)
    /// </summary>
    public List<HotkeyBinding> Bindings { get; set; } = new();

    // --- Speech-to-text ---
    /// <summary>Absolute path to a whisper ggml model file (e.g. ggml-base.en.bin).</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>"auto", or a language code like "en", "ja", "de", "fr".</summary>
    public string Language { get; set; } = "en";

    /// <summary>NAudio WaveIn device index. -1 = system default.</summary>
    public int InputDeviceNumber { get; set; } = -1;

    /// <summary>Ignore clips shorter than this (accidental key taps).</summary>
    public float MinRecordSeconds { get; set; } = 0.4f;

    // --- Voice-activity (hands-free) mode ---
    /// <summary>RMS loudness above which speech is considered active (0..1).</summary>
    public float VadThreshold { get; set; } = 0.02f;

    /// <summary>Milliseconds of silence that ends a spoken segment.</summary>
    public int VadSilenceMs { get; set; } = 700;

    // --- Output ---
    /// <summary>
    /// When true (default) the transcript is sent to chat immediately. When false it
    /// opens in a review box and you press Send / Enter to commit.
    /// </summary>
    public bool AutoSend { get; set; } = true;

    /// <summary>Strip a trailing period Whisper likes to add to short phrases.</summary>
    public bool StripTrailingPeriod { get; set; } = true;

    /// <summary>Show the on-screen recording / transcribing indicator.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>When locked the overlay is click-through and fixed; unlock to drag it.</summary>
    public bool OverlayLocked { get; set; } = true;

    /// <summary>Saved overlay position; negative means "use the default top-center spot".</summary>
    public float OverlayPosX { get; set; } = -1f;
    public float OverlayPosY { get; set; } = -1f;

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);

    public static string SlashPrefix(ChatChannel channel) => channel switch
    {
        ChatChannel.Say => "/s ",
        ChatChannel.Yell => "/y ",
        ChatChannel.Shout => "/sh ",
        ChatChannel.Party => "/p ",
        ChatChannel.Alliance => "/a ",
        ChatChannel.FreeCompany => "/fc ",
        ChatChannel.Echo => "/echo ",
        ChatChannel.Linkshell1 => "/l1 ",
        ChatChannel.Linkshell2 => "/l2 ",
        ChatChannel.Linkshell3 => "/l3 ",
        ChatChannel.Linkshell4 => "/l4 ",
        ChatChannel.Linkshell5 => "/l5 ",
        ChatChannel.Linkshell6 => "/l6 ",
        ChatChannel.Linkshell7 => "/l7 ",
        ChatChannel.Linkshell8 => "/l8 ",
        ChatChannel.CrossWorldLinkshell1 => "/cwl1 ",
        _ => "/echo ",
    };
}
