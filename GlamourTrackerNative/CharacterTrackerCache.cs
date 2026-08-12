using System;
using Newtonsoft.Json;

namespace GlamourTracker;

/// <summary>
/// Per-character persisted state: dresser/armoire ownership, glamour plates, and Fashion Report progress.
/// </summary>
[Serializable]
public sealed class CharacterTrackerCache
{
    public List<uint> DresserBaseIds { get; set; } = [];

    /// <summary>
    /// Pieces the dresser holds inside stored outfits. They are absent from the item list, which keeps
    /// one row per outfit, so without this an outfit's contents are invisible until the box is opened.
    /// </summary>
    public List<uint> DresserOutfitPieceIds { get; set; } = [];

    public List<uint> ArmoireBaseIds { get; set; } = [];
    public List<StoredGlamourPlate> GlamourPlates { get; set; } = [];
    public DateTime LastSavedUtc { get; set; }

    /// <summary>
    /// Outfit sets the dresser is holding in any form. Presence only — see
    /// <see cref="DresserCompleteSetRowIds"/> for the ones that are actually finished.
    /// The saved name is the old one so existing configs keep loading.
    /// </summary>
    [JsonProperty("DresserSetRowIds")]
    public List<uint> DresserSetPresenceRowIds { get; set; } = [];

    /// <summary>Outfit sets with every glam piece slot unlocked via Mirage (from last dresser open).</summary>
    public List<uint> DresserCompleteSetRowIds { get; set; } = [];

    /// <summary>Last known filled Prism Box slot count (persisted so Overview works before reopening the dresser).</summary>
    public int DresserSlotsUsed { get; set; }

    /// <summary>Best Fashion Report score this judging window (from Masked Rose).</summary>
    public int FashionReportHighestScore { get; set; }

    public int FashionReportAllowancesRemaining { get; set; } = 4;

    /// <summary>True after talking to Masked Rose this judging window.</summary>
    public bool FashionReportSynced { get; set; }

    public DateTime FashionReportNextResetUtc { get; set; }

    /// <summary>Outfit set row ids on this character's wishlist.</summary>
    public List<uint> WishlistSetRowIds { get; set; } = [];

    /// <summary>Wishlisted pieces as "setId:itemId" (glamour base item id).</summary>
    public List<string> WishlistPieceKeys { get; set; } = [];

    /// <summary>
    /// Set ids added to the wishlist while auto-remove-owned was on.
    /// Only these are eligible for automatic prune when the set becomes fully owned.
    /// </summary>
    public List<uint> WishlistAutoPruneSetRowIds { get; set; } = [];

    /// <summary>
    /// Piece keys added while auto-remove-owned was on.
    /// Only these are eligible for automatic prune when the piece becomes owned.
    /// </summary>
    public List<string> WishlistAutoPrunePieceKeys { get; set; } = [];

    public bool IsEmpty() =>
        DresserBaseIds.Count == 0
        && DresserOutfitPieceIds.Count == 0
        && ArmoireBaseIds.Count == 0
        && GlamourPlates.Count == 0
        && DresserSetPresenceRowIds.Count == 0
        && DresserCompleteSetRowIds.Count == 0
        && DresserSlotsUsed <= 0
        && !FashionReportSynced
        && FashionReportHighestScore <= 0
        && WishlistSetRowIds.Count == 0
        && WishlistPieceKeys.Count == 0
        && WishlistAutoPruneSetRowIds.Count == 0
        && WishlistAutoPrunePieceKeys.Count == 0;
}
