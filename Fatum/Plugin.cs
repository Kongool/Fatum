using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Fatum.Services;
using Fatum.Windows;

namespace Fatum;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Fatum Voice Chat";

    private const string CommandName = "/fatum";
    private const string CommandAlias = "/fat";

    // --- Injected Dalamud services ---
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("Fatum");
    private readonly ConfigWindow configWindow;
    private readonly ReviewWindow reviewWindow;
    private readonly RecordingOverlay overlay;
    private readonly VoiceController controller;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Seed a starter binding only when none exist (see Configuration.Bindings note).
        if (Configuration.Bindings.Count == 0)
        {
            Configuration.Bindings.Add(new HotkeyBinding());
            Configuration.Save();
        }

        var chatSender = new ChatSender();
        reviewWindow = new ReviewWindow(chatSender);
        configWindow = new ConfigWindow(Configuration);

        controller = new VoiceController(Configuration, reviewWindow);
        overlay = new RecordingOverlay(Configuration, controller);

        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(reviewWindow);
        windowSystem.AddWindow(overlay);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Fatum voice-chat settings. /fatum ptt or /fatum toggle to switch modes.",
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /fatum.",
            ShowInHelp = false,
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfig;
        Framework.Update += controller.OnFrameworkUpdate;
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim().ToLowerInvariant();
        switch (args)
        {
            case "ptt":
                Configuration.Trigger = TriggerMode.PushToTalk;
                Configuration.Save();
                ChatGui.Print("[Fatum] Trigger mode: push-to-talk.");
                break;
            case "toggle":
                Configuration.Trigger = TriggerMode.Toggle;
                Configuration.Save();
                ChatGui.Print("[Fatum] Trigger mode: toggle.");
                break;
            default:
                OpenConfig();
                break;
        }
    }

    private void OpenConfig() => configWindow.IsOpen = true;

    public void Dispose()
    {
        Framework.Update -= controller.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        controller.Dispose();
        windowSystem.RemoveAllWindows();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }
}
