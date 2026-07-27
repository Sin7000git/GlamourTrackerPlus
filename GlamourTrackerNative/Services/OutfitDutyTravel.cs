using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>Open Contents Finder for a duty by display name.</summary>
internal static class OutfitDutyTravel
{
    public static unsafe bool TryOpenDuty(string dutyName, IDataManager dataManager, IChatGui chatGui)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
            return false;

        var sheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        ContentFinderCondition? best = null;
        var bestScore = 0;

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
                continue;
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var score = 0;
            if (name.Equals(dutyName, StringComparison.OrdinalIgnoreCase))
                score = 3;
            else if (name.Contains(dutyName, StringComparison.OrdinalIgnoreCase)
                     || dutyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                score = 2;
            else
                continue;

            if (score > bestScore)
            {
                bestScore = score;
                best = row;
                if (score == 3)
                    break;
            }
        }

        if (best is not { } match || match.RowId == 0)
        {
            chatGui.PrintError($"[Glamour Tracker+] Could not find duty \"{dutyName}\" in Contents Finder.");
            PluginFileLog.Warn("outfit.duty", $"No ContentFinderCondition match for '{dutyName}'");
            return false;
        }

        var agent = AgentContentsFinder.Instance();
        if (agent == null)
        {
            chatGui.PrintError("[Glamour Tracker+] Contents Finder is not available right now.");
            return false;
        }

        agent->OpenRegularDuty(match.RowId);
        PluginFileLog.Info("outfit.duty", $"OpenRegularDuty id={match.RowId} name={match.Name.ExtractText()}");
        return true;
    }
}
