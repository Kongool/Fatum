using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Fatum.Services;
using Dalamud.Bindings.ImGui;

namespace Fatum.Windows;

/// <summary>
/// A small indicator that shows a pulsing red dot while recording and a spinner while
/// transcribing. When locked it is click-through and fixed; when unlocked it stays
/// visible so you can drag it into place, and the position is saved.
/// </summary>
public sealed class RecordingOverlay : Window
{
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

    private const ImGuiWindowFlags LockedFlags = ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove;

    private readonly Configuration config;
    private readonly VoiceController controller;

    public RecordingOverlay(Configuration config, VoiceController controller)
        : base("Fatum##Overlay", BaseFlags | LockedFlags)
    {
        this.config = config;
        this.controller = controller;
        RespectCloseHotkey = false;
        ForceMainWindow = true;
    }

    // Locked: only while active. Unlocked: always visible (so you can position it).
    public override bool DrawConditions()
        => config.ShowOverlay && (!config.OverlayLocked || controller.State != RecordingState.Idle);

    public override void PreDraw()
    {
        var locked = config.OverlayLocked;
        Flags = locked ? BaseFlags | LockedFlags : BaseFlags;

        var hasSaved = config.OverlayPosX >= 0 && config.OverlayPosY >= 0;
        var vp = ImGui.GetMainViewport();
        Position = hasSaved
            ? new Vector2(config.OverlayPosX, config.OverlayPosY)
            : new Vector2(vp.WorkPos.X + (vp.WorkSize.X * 0.5f) - 80f, vp.WorkPos.Y + (vp.WorkSize.Y * 0.10f));

        // Locked: pin every frame. Unlocked: apply once, then let the user drag freely.
        PositionCondition = locked ? ImGuiCond.Always : ImGuiCond.FirstUseEver;

        ImGui.SetNextWindowBgAlpha(0.65f);
    }

    public override void Draw()
    {
        var state = controller.State;
        var t = (float)ImGui.GetTime();

        var recording = state == RecordingState.Recording;
        var transcribing = state == RecordingState.Transcribing;

        Vector4 dotColor;
        if (recording)
        {
            var alpha = 0.45f + (0.55f * MathF.Abs(MathF.Sin(t * 4f)));
            dotColor = new Vector4(0.90f, 0.20f, 0.20f, alpha);
        }
        else if (transcribing)
        {
            dotColor = new Vector4(0.95f, 0.80f, 0.25f, 1f);
        }
        else
        {
            dotColor = new Vector4(0.55f, 0.55f, 0.60f, 1f); // idle placeholder (unlocked only)
        }

        const float radius = 6f;
        var origin = ImGui.GetCursorScreenPos();
        var center = new Vector2(origin.X + radius, origin.Y + (ImGui.GetTextLineHeight() * 0.5f));
        var drawList = ImGui.GetWindowDrawList();

        if (transcribing)
        {
            var a0 = t * 6f;
            drawList.PathArcTo(center, radius, a0, a0 + (MathF.PI * 1.4f), 16);
            drawList.PathStroke(ImGui.GetColorU32(dotColor), ImDrawFlags.None, 2f);
        }
        else
        {
            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(dotColor));
        }

        ImGui.Dummy(new Vector2((radius * 2f) + 4f, ImGui.GetTextLineHeight()));
        ImGui.SameLine();

        if (recording) ImGui.Text($"REC  {controller.ActiveChannel}");
        else if (transcribing) ImGui.Text("transcribing…");
        else ImGui.Text("Fatum — drag me, then lock");

        // While unlocked, persist the position when the user finishes dragging.
        if (!config.OverlayLocked)
        {
            var pos = ImGui.GetWindowPos();
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                (Math.Abs(pos.X - config.OverlayPosX) > 0.5f || Math.Abs(pos.Y - config.OverlayPosY) > 0.5f))
            {
                config.OverlayPosX = pos.X;
                config.OverlayPosY = pos.Y;
                config.Save();
            }
        }
    }
}
