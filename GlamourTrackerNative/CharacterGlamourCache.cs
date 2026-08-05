using System;
using Newtonsoft.Json;

namespace GlamourTracker;

[Serializable]
public sealed class CharacterGlamourCache
{
    public List<uint> DresserBaseIds { get; set; } = [];
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

    /// <summary>Legacy field (DailyDuty import removed); kept for config compat.</summary>
    public bool FashionReportFromDailyDuty { get; set; }

    public DateTime FashionReportNextResetUtc { get; set; }
}
