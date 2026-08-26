using System.Numerics;
using Dalamud.Interface.Windowing;
using Fatum.Services;
using Dalamud.Bindings.ImGui;

namespace Fatum.Windows;

/// <summary>
/// The "type into chat, I hit Enter" step. The transcript lands here pre-filled and
/// editable. Press Enter in the box (or click Send) to commit it to chat; Esc/Cancel
/// throws it away. Nothing is sent to the game until you confirm.
/// </summary>
public sealed class ReviewWindow : Window
{
    private readonly ChatSender sender;

    private string draft = string.Empty;
    private ChatChannel channel = ChatChannel.Say;
    private bool focusNextFrame;

    public ReviewWindow(ChatSender sender)
        : base("Fatum##Review",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.sender = sender;
        Size = new Vector2(420, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Open the review box with transcribed text for a given channel.</summary>
    public void ShowFor(string text, ChatChannel targetChannel)
    {
        draft = text;
        channel = targetChannel;
        focusNextFrame = true;
        IsOpen = true;
        Plugin.Log.Info($"[Fatum] Review box opened: '{text}' → {targetChannel}");
    }

    /// <summary>Auto-send path (config.AutoSend). Skips the box entirely.</summary>
    public void SendDirect(string text, ChatChannel targetChannel)
    {
        var err = sender.Send(Configuration.SlashPrefix(targetChannel) + text);
        if (err is not null)
            Plugin.ChatGui.PrintError($"[Fatum] {err}");
    }

    public override void Draw()
    {
        ImGui.TextDisabled($"Channel: {channel}");

        if (focusNextFrame)
        {
            ImGui.SetKeyboardFocusHere();
            focusNextFrame = false;
        }

        ImGui.SetNextItemWidth(400);
        var entered = ImGui.InputText("##draft", ref draft, 500,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (entered)
            Commit();

        if (ImGui.Button("Send"))
            Commit();
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            draft = string.Empty;
            IsOpen = false;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Enter = send, Esc = cancel");
    }

    private void Commit()
    {
        Plugin.Log.Info($"[Fatum] Review commit: '{draft}' → {channel}");
        var err = sender.Send(Configuration.SlashPrefix(channel) + draft);
        if (err is not null)
        {
            Plugin.ChatGui.PrintError($"[Fatum] {err}");
            return;
        }
        draft = string.Empty;
        IsOpen = false;
    }
}
