using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>Open Contents Finder for a duty by display name.</summary>
internal static class OutfitDutyTravel
{
    private static Dictionary<string, uint>? exactDutyIds;
    private static List<(string Name, uint RowId)>? dutyEntries;

    public static unsafe bool TryOpenDuty(string dutyName, IDataManager dataManager, IChatGui chatGui)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
            return false;

        EnsureIndex(dataManager);

        ContentFinderCondition? best = null;
        var bestScore = 0;

        if (exactDutyIds!.TryGetValue(dutyName, out var exactId)
            && dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(exactId, out var exactRow))
        {
            best = exactRow;
            bestScore = 3;
        }
        else
        {
            foreach (var (name, rowId) in dutyEntries!)
            {
                var score = 0;
                if (name.Equals(dutyName, StringComparison.OrdinalIgnoreCase))
                    score = 3;
                else if (name.Contains(dutyName, StringComparison.OrdinalIgnoreCase)
                         || dutyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    score = 2;
                else
                    continue;

                if (score <= bestScore)
                    continue;

                if (!dataManager.GetExcelSheet<ContentFinderCondition>().TryGetRow(rowId, out var row))
                    continue;

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

    private static void EnsureIndex(IDataManager dataManager)
    {
        if (exactDutyIds != null)
            return;

        var exact = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Name, uint RowId)>();
        foreach (var row in dataManager.GetExcelSheet<ContentFinderCondition>())
        {
            if (row.RowId == 0)
                continue;
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            entries.Add((name, row.RowId));
            exact.TryAdd(name, row.RowId);
        }

        dutyEntries = entries;
        exactDutyIds = exact;
        PluginFileLog.Info("outfit.duty", $"duty name index built ({entries.Count} duties)");
    }
}
