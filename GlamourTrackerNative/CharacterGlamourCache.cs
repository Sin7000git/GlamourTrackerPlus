using System;

namespace GlamourTracker;

[Serializable]
public sealed class CharacterGlamourCache
{
    public List<uint> DresserBaseIds { get; set; } = [];
    public List<uint> ArmoireBaseIds { get; set; } = [];
    public List<StoredGlamourPlate> GlamourPlates { get; set; } = [];
    public DateTime LastSavedUtc { get; set; }

    /// <summary>Best Fashion Report score this judging window (from Masked Rose / DailyDuty).</summary>
    public int FashionReportHighestScore { get; set; }

    public int FashionReportAllowancesRemaining { get; set; } = 4;

    /// <summary>True after talking to Masked Rose or importing DailyDuty data this window.</summary>
    public bool FashionReportSynced { get; set; }

    public bool FashionReportFromDailyDuty { get; set; }

    public DateTime FashionReportNextResetUtc { get; set; }
}
