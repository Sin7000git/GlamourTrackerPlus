namespace GlamourTracker.Services;

internal static class ItemIdHelper
{
    /// <summary>Item IDs in the glamour dresser often include this modifier (see Glamaholic).</summary>
    public const uint ItemModifierMod = 500_000;

    /// <summary>HQ inventory / list IDs are typically <c>base + 1_000_000</c>.</summary>
    public const uint HqOffset = 1_000_000;

    /// <summary>
    /// Sheet / ownership key: strips HQ (<see cref="HqOffset"/>) and dresser
    /// (<see cref="ItemModifierMod"/>) so Excel rows and stored IDs match.
    /// </summary>
    public static uint GlamourBaseId(uint itemId) => itemId % ItemModifierMod;

    /// <summary>Same as <see cref="GlamourBaseId"/> — use before <c>Item</c> sheet lookups.</summary>
    public static uint SheetItemId(uint itemId) => GlamourBaseId(itemId);

    public static IEnumerable<uint> GetRelatedItemIds(uint itemId)
    {
        yield return itemId;

        var baseId = GlamourBaseId(itemId);
        yield return baseId;

        if (baseId != 0 && baseId < HqOffset)
            yield return baseId + HqOffset;

        if (itemId < HqOffset && itemId != baseId + HqOffset)
            yield return itemId + HqOffset;

        if (baseId != itemId)
            yield return baseId + ItemModifierMod;
    }
}
