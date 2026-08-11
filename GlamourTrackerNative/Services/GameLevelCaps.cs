using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>
/// Live character level and item level caps from game sheets — tracks expansions automatically.
/// </summary>
internal static class GameLevelCaps
{
    private static int cachedMaxRequiredLevel;
    private static int cachedMaxItemLevel;

    /// <summary>
    /// Current max LevelEquip / character level.
    /// ParamGrow is padded with empty high rows (e.g. to 200); use the last row with ExpToNext &gt; 0, plus one.
    /// </summary>
    public static int MaxRequiredLevel(IDataManager dataManager)
    {
        if (cachedMaxRequiredLevel > 0)
            return cachedMaxRequiredLevel;

        // Highest row that still has ExpToNext. ParamGrow is padded with empty high rows (e.g. to 200);
        // do not +1 — row 100 currently still has ExpToNext and +1 showed 101.
        var lastWithExp = 0;
        var grow = dataManager.GetExcelSheet<ParamGrow>();
        if (grow != null)
        {
            foreach (var row in grow)
            {
                if (row.RowId == 0 || row.ExpToNext == 0)
                    continue;

                if ((int)row.RowId > lastWithExp)
                    lastWithExp = (int)row.RowId;
            }
        }

        cachedMaxRequiredLevel = lastWithExp > 0 ? lastWithExp : 100;
        return cachedMaxRequiredLevel;
    }

    /// <summary>
    /// Highest item level among named items (avoids empty / placeholder rows).
    /// </summary>
    public static int MaxItemLevel(IDataManager dataManager)
    {
        if (cachedMaxItemLevel > 0)
            return cachedMaxItemLevel;

        var max = 0;
        var items = dataManager.GetExcelSheet<Item>();
        if (items != null)
        {
            foreach (var item in items)
            {
                if (item.RowId == 0)
                    continue;

                var name = item.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var ilvl = (int)item.LevelItem.RowId;
                if (ilvl > max)
                    max = ilvl;
            }
        }

        cachedMaxItemLevel = max > 0 ? max : 1;
        return cachedMaxItemLevel;
    }

    /// <summary>0 (and negatives) mean “use current game maximum”.</summary>
    public static int ResolveRequiredLevelMax(IDataManager dataManager, int configuredMax) =>
        configuredMax <= 0 ? MaxRequiredLevel(dataManager) : Math.Min(configuredMax, MaxRequiredLevel(dataManager));

    /// <summary>0 (and negatives) mean “use current game maximum”.</summary>
    public static int ResolveItemLevelMax(IDataManager dataManager, int configuredMax) =>
        configuredMax <= 0 ? MaxItemLevel(dataManager) : Math.Min(configuredMax, MaxItemLevel(dataManager));
}
