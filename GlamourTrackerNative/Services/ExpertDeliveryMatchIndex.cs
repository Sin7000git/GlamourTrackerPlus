using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourTracker.Services;

/// <summary>
/// Cached GC expert-delivery rows for fast list matching while the supply window is open.
/// </summary>
internal sealed class ExpertDeliveryMatchIndex
{
    private readonly Dictionary<uint, GrandCompanyItem> uniqueByIconId;
    private readonly Dictionary<uint, string> sheetNames;

    private ExpertDeliveryMatchIndex(
        List<GrandCompanyItem> items,
        Dictionary<uint, GrandCompanyItem> uniqueByIconId,
        Dictionary<uint, string> sheetNames)
    {
        this.Items = items;
        this.uniqueByIconId = uniqueByIconId;
        this.sheetNames = sheetNames;
    }

    public List<GrandCompanyItem> Items { get; }

    public static ExpertDeliveryMatchIndex Build(
        List<GrandCompanyItem> items,
        Func<uint, string> getSheetItemName)
    {
        var iconHits = new Dictionary<uint, int>();
        foreach (var item in items)
        {
            if (item.IconId == 0)
                continue;

            iconHits.TryGetValue(item.IconId, out var count);
            iconHits[item.IconId] = count + 1;
        }

        var uniqueByIconId = new Dictionary<uint, GrandCompanyItem>();
        foreach (var item in items)
        {
            if (item.IconId != 0 && iconHits[item.IconId] == 1)
                uniqueByIconId[item.IconId] = item;
        }

        var sheetNames = new Dictionary<uint, string>();
        foreach (var item in items)
        {
            if (!sheetNames.ContainsKey(item.ItemId))
                sheetNames[item.ItemId] = getSheetItemName(item.ItemId);
        }

        return new ExpertDeliveryMatchIndex(items, uniqueByIconId, sheetNames);
    }

    public bool TryGetByIconId(uint iconId, out GrandCompanyItem item) =>
        this.uniqueByIconId.TryGetValue(iconId, out item);

    public GrandCompanyItem? MatchByRowLabel(string rowLabel)
    {
        GrandCompanyItem? match = null;
        var hits = 0;

        foreach (var item in this.Items)
        {
            if (!this.sheetNames.TryGetValue(item.ItemId, out var sheetName)
                || string.IsNullOrEmpty(sheetName))
                continue;

            if (!rowLabel.Contains(sheetName, StringComparison.Ordinal))
                continue;

            match = item;
            hits++;
        }

        return hits == 1 ? match : null;
    }
}
