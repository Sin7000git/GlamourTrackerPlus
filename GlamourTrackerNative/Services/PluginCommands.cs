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
                "Glamour Tracker+ Native. /glamplus = main UI · /glamplus report = Fashion Report · /glamplus imgui = ImGui fallback.",
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

            case "imgui":
            case "old":
                this.plugin.ToggleImGuiMainUi();
                return;

            case "native":
            case "nui":
            case "fashion":
            case "fr":
            case "report":
                this.plugin.OpenFashionReportTab();
                return;

            case "refresh":
                this.plugin.RefreshAll(true);
                this.chatGui.Print("Glamour Tracker+ Native refreshed dresser and armoire data.");
                return;

            case "randomize":
            case "rand":
            case "roll":
                _ = Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    var result = this.plugin.BeginRandomizeOpenPlate(r =>
                    {
                        if (!r.InProgress)
                            this.chatGui.Print($"[Glamour Tracker+ Native] {r.Message}");
                        if (r is { Success: true, InProgress: false })
                            this.plugin.RefreshAll(true);
                    });
                    this.chatGui.Print($"[Glamour Tracker+ Native] {result.Message}");
                });
                return;

            case "gcdebug":
            case "gcicons":
                this.plugin.DebugGcExpertDelivery();
                return;

            case "help":
                this.chatGui.Print(
                    "Glamour Tracker+ Native: /glamplus | /glamplus report | /glamplus imgui | /glamplus help");
                return;

            default:
                this.chatGui.Print($"Unknown option \"{verb}\". Use /glamplus help.");
                return;
        }
    }
}
