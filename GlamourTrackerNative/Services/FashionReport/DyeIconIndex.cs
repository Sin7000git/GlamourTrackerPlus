using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services.FashionReport;

/// <summary>One-time Item-sheet index for dye name → icon lookups.</summary>
internal static class DyeIconIndex
{
    private static Dictionary<string, ushort>? byName;

    public static ushort Resolve(IDataManager dataManager, string? dyeName)
    {
        if (string.IsNullOrWhiteSpace(dyeName) || dyeName == "—")
            return 0;

        EnsureBuilt(dataManager);

        if (byName!.TryGetValue(dyeName, out var icon))
            return icon;

        var withDyeSuffix = dyeName.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase)
            ? dyeName
            : dyeName + " Dye";
        return byName.TryGetValue(withDyeSuffix, out icon) ? icon : (ushort)0;
    }

    private static void EnsureBuilt(IDataManager dataManager)
    {
        if (byName != null)
            return;

        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dataManager.GetExcelSheet<Item>())
        {
            if (item.RowId == 0 || item.Icon == 0)
                continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Prefer the first hit for a given name (same as the old linear scan).
            map.TryAdd(name, item.Icon);
        }

        byName = map;
        PluginFileLog.Info("fashion.dye", $"dye icon index built ({map.Count} names)");
    }
}
