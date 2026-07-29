using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace GlamourTracker.Services;

internal sealed class PluginCommands : IDisposable
{
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly Plugin plugin;

    public PluginCommands(ICommandManager commandManager, IChatGui chatGui, Plugin plugin)
    {
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.plugin = plugin;

        this.commandManager.AddHandler(Plugin.CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "main UI\n"
                + "/glamplus fashion → Fashion Report\n"
                + "/glamplus refresh → Force refresh\n"
                + "/glamplus help → Command aliases",
        });
    }

    public void Dispose()
    {
        this.commandManager.RemoveHandler(Plugin.CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var verb = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant()
            ?? string.Empty;

        switch (verb)
        {
            case "":
            case "open":
            case "ui":
                this.plugin.ToggleMainUi();
                return;

#if GLAMOUR_DEV
            case "imgui":
            case "old":
                this.plugin.ToggleImGuiMainUi();
                return;
#endif

            case "fashion":
            case "fr":
            case "report":
                this.plugin.OpenFashionReportTab();
                return;

            case "refresh":
                this.plugin.RefreshAll(true);
                this.chatGui.Print("Glamour Tracker+ refreshed dresser and armoire data.");
                return;

#if GLAMOUR_DEV
            case "gcdebug":
            case "gcicons":
                this.plugin.DebugGcExpertDelivery();
                return;
#endif

            case "help":
            case "?":
                PrintAliasHelp();
                return;

            default:
                this.chatGui.Print($"Unknown option \"{verb}\". Use /glamplus help.");
                return;
        }
    }

    private void PrintAliasHelp()
    {
        this.chatGui.Print("[Glamour Tracker+] Command aliases:");
        this.chatGui.Print("  /glamplus  (also: open, ui) → main UI");
        this.chatGui.Print("  /glamplus fashion  (also: fr, report) → Fashion Report");
        this.chatGui.Print("  /glamplus refresh → Force refresh dresser/armoire data");
#if GLAMOUR_DEV
        this.chatGui.Print("  /glamplus imgui  (also: old) → legacy ImGui UI (Dev)");
        this.chatGui.Print("  /glamplus gcdebug  (also: gcicons) → GC marker diagnostics (Dev)");
#endif
        this.chatGui.Print("  /glamplus help  (also: ?) → this list");
    }
}
