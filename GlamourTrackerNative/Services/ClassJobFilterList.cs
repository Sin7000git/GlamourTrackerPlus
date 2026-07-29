using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>
/// Job picker rows for randomize filters: starter classes are folded into their job
/// (e.g. <c>NIN/ROG — Ninja</c>) instead of listing ROG and NIN separately.
/// </summary>
internal static class ClassJobFilterList
{
    public readonly record struct Entry(uint RowId, ushort UiPriority, string Label);

    public static List<Entry> Build(ExcelSheet<ClassJob> sheet)
    {
        var all = sheet.Where(j => j.RowId != 0).ToList();

        // Only count a real upgrade parent (ClassJobParent often equals the row itself).
        var starterIds = new HashSet<uint>();
        foreach (var j in all)
        {
            if (TryGetDistinctParentId(j, out var parentId))
                starterIds.Add(parentId);
        }

        var entries = new List<Entry>(all.Count);
        foreach (var j in all)
        {
            var abbr = j.Abbreviation.ExtractText();
            if (string.IsNullOrWhiteSpace(abbr))
                continue;

            // Starter class with a job upgrade — shown on the job row only.
            if (starterIds.Contains(j.RowId))
                continue;

            var name = j.Name.ExtractText();
            string label;
            if (TryGetDistinctParentId(j, out var parentId)
                && sheet.TryGetRow(parentId, out var parent))
            {
                var parentAbbr = parent.Abbreviation.ExtractText();
                label = string.IsNullOrWhiteSpace(parentAbbr) || parentAbbr.Equals(abbr, StringComparison.OrdinalIgnoreCase)
                    ? $"{abbr} — {name}"
                    : $"{abbr}/{parentAbbr} — {name}";
            }
            else
            {
                label = $"{abbr} — {name}";
            }

            entries.Add(new Entry(j.RowId, j.UIPriority, label));
        }

        return entries
            .OrderBy(e => e.UiPriority)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Maps a previously saved starter-class id to its job (or first job for ACN).
    /// </summary>
    public static uint ResolveStoredJobId(uint storedId, ExcelSheet<ClassJob> sheet)
    {
        if (storedId == 0)
            return 0;

        var entries = Build(sheet);
        if (entries.Exists(e => e.RowId == storedId))
            return storedId;

        var upgrades = sheet
            .Where(j => TryGetDistinctParentId(j, out var parentId) && parentId == storedId)
            .OrderBy(j => j.UIPriority)
            .Select(j => j.RowId)
            .ToList();
        return upgrades.Count > 0 ? upgrades[0] : storedId;
    }

    /// <summary>True when <see cref="ClassJob.ClassJobParent"/> points at a different class/job.</summary>
    internal static bool TryGetDistinctParentId(ClassJob job, out uint parentId)
    {
        parentId = job.ClassJobParent.RowId;
        if (parentId == 0 || parentId == job.RowId)
        {
            parentId = 0;
            return false;
        }

        return true;
    }
}
