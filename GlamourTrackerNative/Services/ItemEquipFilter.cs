using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>Job / race / level checks for glamour randomizer pools (Item sheet data).</summary>
internal static class ItemEquipFilter
{
    public static bool MatchesJob(Item item, uint classJobId, ExcelSheet<ClassJob> classJobs)
    {
        if (classJobId == 0 || !item.ClassJobCategory.IsValid)
            return false;

        if (!classJobs.TryGetRow(classJobId, out var job))
            return false;

        var category = item.ClassJobCategory.Value;
        if (MatchesAbbreviation(category, job.Abbreviation.ExtractText()))
            return true;

        // Job rows (NIN, DRG, …): also accept gear flagged for the starter class.
        if (ClassJobFilterList.TryGetDistinctParentId(job, out var parentId)
            && classJobs.TryGetRow(parentId, out var parent)
            && MatchesAbbreviation(category, parent.Abbreviation.ExtractText()))
            return true;

        return false;
    }

    private static bool MatchesAbbreviation(ClassJobCategory category, string abbr) =>
        abbr switch
        {
            "GLA" => category.GLA,
            "PGL" => category.PGL,
            "MRD" => category.MRD,
            "LNC" => category.LNC,
            "ARC" => category.ARC,
            "CNJ" => category.CNJ,
            "THM" => category.THM,
            "CRP" => category.CRP,
            "BSM" => category.BSM,
            "ARM" => category.ARM,
            "GSM" => category.GSM,
            "LTW" => category.LTW,
            "WVR" => category.WVR,
            "ALC" => category.ALC,
            "CUL" => category.CUL,
            "MIN" => category.MIN,
            "BTN" => category.BTN,
            "FSH" => category.FSH,
            "PLD" => category.PLD,
            "MNK" => category.MNK,
            "WAR" => category.WAR,
            "DRG" => category.DRG,
            "BRD" => category.BRD,
            "WHM" => category.WHM,
            "BLM" => category.BLM,
            "ACN" => category.ACN,
            "SMN" => category.SMN,
            "SCH" => category.SCH,
            "ROG" => category.ROG,
            "NIN" => category.NIN,
            "MCH" => category.MCH,
            "DRK" => category.DRK,
            "AST" => category.AST,
            "SAM" => category.SAM,
            "RDM" => category.RDM,
            "BLU" => category.BLU,
            "GNB" => category.GNB,
            "DNC" => category.DNC,
            "RPR" => category.RPR,
            "SGE" => category.SGE,
            "VPR" => category.VPR,
            "PCT" => category.PCT,
            _ => false,
        };

    /// <summary>
    /// True when the item can be equipped by the given race and sex (EquipRaceCategory).
    /// Items without a restriction row are treated as unrestricted.
    /// </summary>
    public static bool MatchesRaceAndSex(Item item, uint raceId, bool isFemale)
    {
        if (!item.EquipRestriction.IsValid)
            return true;

        var category = item.EquipRestriction.Value;
        if (isFemale)
        {
            if (!category.Female)
                return false;
        }
        else if (!category.Male)
        {
            return false;
        }

        return raceId switch
        {
            1 => category.Hyur,
            2 => category.Elezen,
            3 => category.Lalafell,
            4 => category.Miqote,
            5 => category.Roegadyn,
            6 => category.AuRa,
            7 => category.Hrothgar,
            8 => category.Viera,
            _ => true,
        };
    }

    public static byte RequiredLevel(Item item) => item.LevelEquip;

    public static ushort ItemLevel(Item item) => (ushort)item.LevelItem.RowId;

    public static bool MatchesRequiredLevel(Item item, bool enabled, int minRequiredLevel, int maxRequiredLevel)
    {
        if (!enabled)
            return true;

        var level = RequiredLevel(item);
        if (minRequiredLevel > maxRequiredLevel)
            (minRequiredLevel, maxRequiredLevel) = (maxRequiredLevel, minRequiredLevel);

        return level >= minRequiredLevel && level <= maxRequiredLevel;
    }

    public static bool MatchesItemLevel(Item item, bool enabled, int minItemLevel, int maxItemLevel)
    {
        if (!enabled)
            return true;

        var ilvl = ItemLevel(item);
        if (minItemLevel > maxItemLevel)
            (minItemLevel, maxItemLevel) = (maxItemLevel, minItemLevel);

        return ilvl >= minItemLevel && ilvl <= maxItemLevel;
    }
}
