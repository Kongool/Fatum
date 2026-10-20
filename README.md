# Fatum — Voice-to-Text for FFXIV

Hold a key (or toggle), talk into your mic, and your speech is transcribed **locally**
with Whisper and dropped into a small review box. Press **Enter** to send it to your
chosen chat channel. No cloud, no API keys, nothing leaves your PC.

This is a **Dalamud plugin**, so it only works when you launch FFXIV through
[XIVLauncher](https://goatcorp.github.io/).

---

## ⚠️ Read this first

- **Dalamud is against Square Enix's Terms of Service.** Bans specifically for it are
  very rare, but programmatically sending chat is a bit further out than a passive
  overlay. That's why the default is **review-before-send** (you press Enter yourself).
  Use auto-send at your own discretion.
- Keep voice chat to **casual/social channels** (say/party/FC/linkshell). Don't automate
  spam. Be a good citizen.

---

## 1. Prerequisites

- FFXIV via **XIVLauncher + Dalamud** (open the game once with it so Dalamud loads).
- **.NET SDK 10** — Dalamud 15 is built against .NET 10, so the project targets
  `net10.0-windows`. (Verified building against Dalamud 15.0.3.2 with SDK 10.0.302.)
  If your Dalamud is older, drop the target framework in `Fatum.csproj` to match.
- A **Whisper model file** (next step).

## 2. Get a Whisper model

Download one ggml model. `base.en` is the sweet spot for English (fast + accurate):

```bash
# ~148 MB, English only — good default
curl -L -o ggml-base.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin
```

Other options: `ggml-tiny.en.bin` (~75 MB, fastest, less accurate),
`ggml-small.en.bin` (~466 MB, more accurate), `ggml-base.bin` (multilingual).
Put the file anywhere you like and remember the path.

## 3. Build the plugin

From this folder:

```bash
dotnet build Fatum/Fatum.csproj -c Release
```

The output lands in **`Fatum/bin/Release/`** and includes `Fatum.dll` plus the Whisper
native libraries (`whisper.dll`, `ggml-*-whisper.dll`) and `NAudio.dll` — all in that one
folder, ready to load.

> Note: Dalamud 15 ships its own ImGui bindings (`Dalamud.Bindings.ImGui`), which the
> csproj references directly from your dev folder (`%AppData%\XIVLauncher\addon\Hooks\dev`).
> The `Dalamud.NET.Sdk` version at the top of `Fatum.csproj` only controls build plumbing;
> the actual Dalamud API comes from that dev folder.

## 4. Load it in-game

1. In FFXIV, open **`/xlsettings`** → **Experimental** tab.
2. Under **Dev Plugin Locations**, add `C:\Dev\Fatum\Fatum\bin\Release\` (the folder,
   or the `Fatum.dll` inside it).
3. Open **`/xlplugins`** → **Dev Tools / installed** and enable **Fatum Voice Chat**.

## 5. Configure

Open settings via the **⚙ gear icon** next to the plugin in `/xlplugins`, or type
**`/fatum`** (short: **`/fat`**) in chat:

- **Trigger** — Push-to-talk, Toggle, or **Always listening** (hands-free voice
  activation, no key). In Always-listening mode it auto-segments on speech/silence and
  sends to your first binding's channel; tune *Sensitivity* just above your idle mic level.
  (`/fatum ptt` / `/fatum toggle` switch the key modes.)
- **Hotkeys → channels** — bind as many keys as you like, each routed to its own
  channel (e.g. `V → Say`, `P → Party`, `F → FreeCompany`). Click a key button to
  rebind; **+ Add binding** for another; **Remove** to drop one. Whichever key you press
  decides where that message goes.
- **Microphone** — pick your input device; the **live level meter** below it moves as you
  speak so you can confirm the right mic is picked up (green = good, red = too hot).
- **Model file** — paste the path to the `.bin` you downloaded.
- **Language** — `en`, `ja`, `auto`, etc.
- **Auto-send** — off = review box (recommended); on = send immediately.
- **Screen-edge tab** — a **Fatum** tab docked to a screen edge (right edge, middle, by
  default). Hover it to pop out the settings panel; click to pin it open. **Drag it
  anywhere**: let go and it lands on the nearest edge (left/right/top/bottom) where you
  dropped it. It shares the edge-dock layout with Daedalus and Charon, so tabs wanting the
  same spot sit side by side instead of overlapping. A red dot shows while you're being
  heard. Turn it off, pick an edge, or *Reset placement* under *Screen-edge tab*.
- **On-screen indicator** — a small click-through overlay shows a pulsing red **REC** dot
  (with the target channel) while you talk and a spinner while it transcribes, so you get
  feedback without watching the chat log. To move it: untick **Lock overlay position** in
  Output settings, **drag** it where you want, then re-lock (position is saved).
  **Reset position** returns it to top-center.

## 6. Use it

- **Push-to-talk:** hold one of your bound keys, speak, release. First run loads the
  model (a second or two), then a review box appears with your words, routed to that
  key's channel.
- **Toggle:** press a bound key to start, press the same key again to stop.
- In the review box: edit if needed, **Enter** to send, **Esc** to cancel.

---

## How it fits together

| File | Job |
|------|-----|
| `Plugin.cs` | Entry point, service wiring, `/fatum` command, windows. |
| `Services/VoiceController.cs` | Polls the hotkey each frame, runs record → transcribe → review. |
| `Services/MicrophoneCapture.cs` | NAudio WASAPI capture at 16 kHz mono (what Whisper wants). |
| `Services/MicLevelMonitor.cs` | Independent mic tap that drives the config-window level meter. |
| `Services/WhisperTranscriber.cs` | Whisper.net wrapper; loads the model once, transcribes off-thread. |
| `Services/ChatSender.cs` | Sends via ClientStructs `UIModule.ProcessChatBoxEntry` (patch-stable). |
| `Windows/ReviewWindow.cs` | The pre-filled "press Enter to send" box. |
| `Windows/RecordingOverlay.cs` | Click-through REC dot / transcribing spinner overlay. |
| `Windows/ConfigWindow.cs` | Settings UI. |

## Troubleshooting

- **"Whisper model not found"** — the path in settings is wrong or the file moved.
- **Message doesn't send / "UIModule unavailable"** — you must be logged in to a
  character (not the title screen). Chat sending goes through ClientStructs, which stays
  current across patches, so there's no signature to hand-maintain.
- **No transcript / "heard nothing"** — wrong mic selected, or the clip was under the
  *Min clip length*. Check the input device and speak a bit longer.
- **Native DLL load errors** — confirm `whisper.dll` and the `ggml*.dll` files are next
  to `Fatum.dll` in the build output. The csproj copies them post-build; if your NuGet
  cache path differs, copy them from `Whisper.net.Runtime`'s `build/win-x64` folder.
- **Nothing happens on the hotkey** — Dalamud only sees keys while the **game window is
  focused**. Also make sure the hotkey doesn't collide with a game keybind you're holding.

## Notes

Built and loading against **Dalamud 15** (`Dalamud.NET.Sdk/15.0.0`, `DalamudApiLevel 15`,
.NET 10). Chat sending uses FFXIVClientStructs `UIModule.ProcessChatBoxEntry`, so it stays
current across game patches — no hand-maintained signature.
