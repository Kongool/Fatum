using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Fatum.Services;
using Dalamud.Bindings.ImGui;

namespace Fatum.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly MicLevelMonitor monitor = new();

    private (int Index, string Name)[] devices = Array.Empty<(int, string)>();
    private VirtualKey[] validKeys = Array.Empty<VirtualKey>();
    private int listeningForKeyIndex = -1; // which binding row is capturing a key

    private static readonly string[] ChannelNames = Enum.GetNames<ChatChannel>();

    public ConfigWindow(Configuration config)
        : base("Fatum Voice Chat — Settings##Config", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        Size = new Vector2(480, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => InitResources();

    public override void OnClose()
    {
        // The edge-dock panel may still be showing this body; leave the mic meter running for it.
        if (embedded) return;
        monitor.Stop();
        listeningForKeyIndex = -1;
    }

    public override void Draw()
    {
        DrawTrigger();
        DrawBindings();
        DrawMicrophone();
        DrawWhisper();
        DrawOutput();
        DrawDock();
    }

    // --- Drawn inside the edge-dock panel ------------------------------------

    private bool embedded;

    /// <summary>Draws the settings body inside another window (the edge-dock panel).</summary>
    public void DrawEmbedded()
    {
        if (!embedded)
        {
            embedded = true;
            if (!IsOpen) InitResources();
        }
        Draw();
    }

    /// <summary>The dock panel closed: stop the mic meter unless the settings window still needs it.</summary>
    public void ReleaseEmbedded()
    {
        if (!embedded) return;
        embedded = false;
        if (IsOpen) return;
        monitor.Stop();
        listeningForKeyIndex = -1;
    }

    private void InitResources()
    {
        devices = MicrophoneCapture.ListDevices().ToArray();
        validKeys = Plugin.KeyState.GetValidVirtualKeys().ToArray();
        if (monitor.DeviceNumber != config.InputDeviceNumber)
            monitor.Start(config.InputDeviceNumber);
    }

    private static readonly string[] DockEdges = { "Left", "Right", "Top", "Bottom" };

    private void DrawDock()
    {
        Section("Screen-edge tab");
        var enabled = config.EdgeDockEnabled;
        if (ImGui.Checkbox("Show Fatum tab on the screen edge", ref enabled))
            Apply(() => config.EdgeDockEnabled = enabled);

        if (!config.EdgeDockEnabled) return;

        ImGui.Indent();
        // Combo index = DockEdge value (Left 0, Right 1, Top 2, Bottom 3).
        var edge = (int)config.EdgeDockEdge;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("Dock edge", ref edge, DockEdges, DockEdges.Length))
            Apply(() => config.EdgeDockEdge = (Dock.DockEdge)edge);

        ImGui.SameLine();
        if (ImGui.Button("Reset placement##dock"))
            Apply(config.ResetEdgeDockPlacement);

        ImGui.TextDisabled("Drag the tab anywhere: it lands on the nearest edge, where you let go.");
        ImGui.TextDisabled("Hover to open, click to pin. Shares space with Daedalus / Charon tabs.");
        ImGui.Unindent();
    }

    private void DrawTrigger()
    {
        Section("Trigger");
        var trigger = (int)config.Trigger;
        if (ImGui.RadioButton("Push-to-talk (hold key)", ref trigger, (int)TriggerMode.PushToTalk))
            Apply(() => config.Trigger = TriggerMode.PushToTalk);
        if (ImGui.RadioButton("Toggle (press to start, press to stop)", ref trigger, (int)TriggerMode.Toggle))
            Apply(() => config.Trigger = TriggerMode.Toggle);
        if (ImGui.RadioButton("Always listening (hands-free, no key)", ref trigger, (int)TriggerMode.VoiceActivity))
            Apply(() => config.Trigger = TriggerMode.VoiceActivity);

        if (config.Trigger == TriggerMode.VoiceActivity)
        {
            ImGui.Indent();
            var channel = config.Bindings.Count > 0 ? config.Bindings[0].Channel : ChatChannel.Say;
            ImGui.TextDisabled($"Speak and it auto-sends to: {channel} (the first binding's channel).");

            var thr = config.VadThreshold;
            if (ImGui.SliderFloat("Sensitivity (lower = more sensitive)", ref thr, 0.005f, 0.1f, "%.3f"))
                Apply(() => config.VadThreshold = thr);

            var sil = config.VadSilenceMs;
            if (ImGui.SliderInt("End-of-phrase silence (ms)", ref sil, 300, 2000))
                Apply(() => config.VadSilenceMs = sil);

            ImGui.TextDisabled("Watch the mic Level below: set sensitivity just above your idle noise.");
            ImGui.Unindent();
        }
    }

    private void DrawBindings()
    {
        Section("Hotkeys → channels");
        ImGui.TextDisabled("Each key records and sends to its own channel.");

        var removeAt = -1;
        for (var i = 0; i < config.Bindings.Count; i++)
        {
            var binding = config.Bindings[i];
            ImGui.PushID(i);

            // Key button (click to rebind)
            var capturing = listeningForKeyIndex == i;
            var label = capturing ? "Press any key..." : binding.Key.ToString();
            if (ImGui.Button($"{label}##key", new Vector2(150, 0)))
                listeningForKeyIndex = capturing ? -1 : i;

            if (capturing)
                CaptureKeyInto(binding);

            // Channel combo
            ImGui.SameLine();
            ImGui.Text("→");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            var channel = (int)binding.Channel;
            if (ImGui.Combo("##channel", ref channel, ChannelNames, ChannelNames.Length))
                Apply(() => binding.Channel = (ChatChannel)channel);

            // Remove (keep at least one binding)
            if (config.Bindings.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                    removeAt = i;
            }

            ImGui.PopID();
        }

        if (removeAt >= 0)
        {
            config.Bindings.RemoveAt(removeAt);
            if (listeningForKeyIndex == removeAt) listeningForKeyIndex = -1;
            config.Save();
        }

        if (ImGui.Button("+ Add binding"))
        {
            config.Bindings.Add(new HotkeyBinding(VirtualKey.NO_KEY, ChatChannel.Say));
            config.Save();
        }
    }

    private void DrawMicrophone()
    {
        Section("Microphone");

        var currentName = devices.FirstOrDefault(d => d.Index == config.InputDeviceNumber).Name ?? "System default";
        if (ImGui.BeginCombo("Input device", currentName))
        {
            foreach (var (index, name) in devices)
            {
                var selected = index == config.InputDeviceNumber;
                if (ImGui.Selectable(name, selected))
                {
                    Apply(() => config.InputDeviceNumber = index);
                    monitor.Start(index);
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        // Keep the meter tap on the currently selected device.
        if (monitor.DeviceNumber != config.InputDeviceNumber)
            monitor.Start(config.InputDeviceNumber);

        // Live level meter (voice rarely peaks at 1.0, so scale for readability).
        var shown = Math.Min(1f, monitor.Level * 1.4f);
        ImGui.Text("Level");
        ImGui.SameLine();
        var barColor = shown > 0.9f
            ? new Vector4(0.85f, 0.25f, 0.25f, 1f)  // clipping-ish → red
            : new Vector4(0.25f, 0.75f, 0.35f, 1f); // healthy → green
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, barColor);
        ImGui.ProgressBar(shown, new Vector2(-1, 0), monitor.IsRunning ? " " : "no signal");
        ImGui.PopStyleColor();
        ImGui.TextDisabled("Speak to check your mic. Bar should move well before the red zone.");

        var minRec = config.MinRecordSeconds;
        if (ImGui.SliderFloat("Min clip length (s)", ref minRec, 0f, 2f, "%.2f"))
            Apply(() => config.MinRecordSeconds = minRec);
    }

    private void DrawWhisper()
    {
        Section("Speech-to-text (Whisper, offline)");
        var modelPath = config.ModelPath;
        if (ImGui.InputText("Model file (.bin)", ref modelPath, 1024))
            Apply(() => config.ModelPath = modelPath);
        ImGui.TextDisabled("A ggml model, e.g. ggml-base.en.bin. See the README for downloads.");

        var lang = config.Language;
        if (ImGui.InputText("Language (\"auto\", \"en\", \"ja\"...)", ref lang, 16))
            Apply(() => config.Language = lang);
    }

    private void DrawOutput()
    {
        Section("Output");
        var autoSend = config.AutoSend;
        if (ImGui.Checkbox("Auto-send (skip review box — higher risk)", ref autoSend))
            Apply(() => config.AutoSend = autoSend);

        var strip = config.StripTrailingPeriod;
        if (ImGui.Checkbox("Strip trailing period from short phrases", ref strip))
            Apply(() => config.StripTrailingPeriod = strip);

        var overlay = config.ShowOverlay;
        if (ImGui.Checkbox("Show on-screen recording indicator", ref overlay))
            Apply(() => config.ShowOverlay = overlay);

        if (config.ShowOverlay)
        {
            ImGui.Indent();
            var locked = config.OverlayLocked;
            if (ImGui.Checkbox("Lock overlay position", ref locked))
                Apply(() => config.OverlayLocked = locked);

            ImGui.SameLine();
            if (ImGui.Button("Reset position"))
                Apply(() =>
                {
                    config.OverlayPosX = -1f;
                    config.OverlayPosY = -1f;
                });

            if (!config.OverlayLocked)
                ImGui.TextDisabled("Unlocked: drag the overlay into place, then lock it.");
            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Transcripts open in a review box; press Enter there to send.");
    }

    private void CaptureKeyInto(HotkeyBinding binding)
    {
        foreach (var key in validKeys)
        {
            if (key is VirtualKey.NO_KEY or VirtualKey.LBUTTON or VirtualKey.RBUTTON)
                continue;
            if (Plugin.KeyState[key])
            {
                Apply(() => binding.Key = key);
                listeningForKeyIndex = -1;
                break;
            }
        }
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text(label);
        ImGui.Spacing();
    }

    private void Apply(Action change)
    {
        change();
        config.Save();
    }

    public void Dispose() => monitor.Dispose();
}
