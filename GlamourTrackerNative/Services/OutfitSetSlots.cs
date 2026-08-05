using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>
/// The eleven gear slots an outfit set can fill, in the order the game stores them. Glamour plates
/// use a separate twelve-slot map because they carry a second ring.
/// </summary>
internal static class OutfitSetSlots
{
    public static readonly (string Label, int Index, Func<MirageStoreSetItem, uint> ItemId)[] All =
    [
        ("Main hand", 0, s => s.MainHand.RowId),
        ("Off-hand", 1, s => s.OffHand.RowId),
        ("Head", 2, s => s.Head.RowId),
        ("Body", 3, s => s.Body.RowId),
        ("Hands", 4, s => s.Hands.RowId),
        ("Legs", 5, s => s.Legs.RowId),
        ("Feet", 6, s => s.Feet.RowId),
        ("Earrings", 7, s => s.Earrings.RowId),
        ("Necklace", 8, s => s.Necklace.RowId),
        ("Bracelets", 9, s => s.Bracelets.RowId),
        ("Ring", 10, s => s.Ring.RowId),
    ];
}
