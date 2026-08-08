namespace GlamourTracker.Services;

internal static class ItemIdHelper
{
    /// <summary>Item IDs in the glamour dresser often include this modifier (see Glamaholic).</summary>
    public const uint ItemModifierMod = 500_000;

    private const uint HqOffset = 1_000_000;

    /// <summary>Strips HQ and glamour-dresser modifiers so sheet IDs match stored IDs.</summary>
    public static uint GlamourBaseId(uint itemId) => itemId % ItemModifierMod;

    public static IEnumerable<uint> GetRelatedItemIds(uint itemId)
    {
        yield return itemId;

        var baseId = GlamourBaseId(itemId);
        yield return baseId;

        if (itemId < HqOffset)
            yield return itemId + HqOffset;

        if (baseId != itemId)
            yield return baseId + ItemModifierMod;
    }
}
