using System;
using System.Text;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Fatum.Services;

/// <summary>
/// Sends a message through the game's own chat-box entry point via FFXIVClientStructs
/// (<see cref="UIModule.ProcessChatBoxEntry"/>). ClientStructs keeps this function's
/// signature current across game patches, so there is no hand-rolled signature to break.
/// Supports leading slash commands (/p, /s, ...).
/// </summary>
public sealed unsafe class ChatSender
{
    /// <summary>Send a message. Returns null on success, or an error string to show the user.</summary>
    public string? Send(string message)
    {
        message = Sanitise(message);
        if (string.IsNullOrWhiteSpace(message))
            return "Nothing to send.";

        // Chat has a byte limit; 500 UTF-8 bytes is the safe ceiling.
        var bytes = Encoding.UTF8.GetByteCount(message);
        if (bytes > 500)
            return $"Message too long ({bytes} bytes, max 500). Say something shorter.";

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return "UIModule unavailable (are you logged in?).";

        Plugin.Log.Info($"[Fatum] Sending to chat box: '{message}'");
        var payload = Utf8String.FromString(message);
        try
        {
            uiModule->ProcessChatBoxEntry(payload, nint.Zero, false);
        }
        catch (Exception ex)
        {
            return $"Send failed: {ex.Message}";
        }
        finally
        {
            payload->Dtor(true);
        }

        return null;
    }

    private static string Sanitise(string text)
    {
        // Collapse newlines/tabs (chat is one line), strip control chars.
        text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        text = Regex.Replace(text, @"[\x00-\x1F\x7F]", string.Empty);
        return text.Trim();
    }
}
