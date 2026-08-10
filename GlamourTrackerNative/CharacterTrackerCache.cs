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

    public bool IsEmpty() =>
        DresserBaseIds.Count == 0
        && DresserOutfitPieceIds.Count == 0
        && ArmoireBaseIds.Count == 0
        && GlamourPlates.Count == 0
        && DresserSetPresenceRowIds.Count == 0
        && DresserCompleteSetRowIds.Count == 0
        && DresserSlotsUsed <= 0
        && !FashionReportSynced
        && FashionReportHighestScore <= 0;
}
