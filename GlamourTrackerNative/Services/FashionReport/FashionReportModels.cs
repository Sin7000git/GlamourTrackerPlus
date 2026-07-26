using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlamourTracker.Services.FashionReport;

internal sealed class FashionReportStateDto
{
    [JsonPropertyName("lastOptions")]
    public FashionReportLastOptionsDto? LastOptions { get; set; }

    /// <summary>
    /// Slot objects plus metadata keys like <c>_updatedAt</c> (number) from the API.
    /// </summary>
    [JsonPropertyName("dyeData")]
    public Dictionary<string, JsonElement>? DyeData { get; set; }

    [JsonPropertyName("easy100")]
    public FashionReportEasySectionDto? Easy100 { get; set; }

    [JsonPropertyName("easy80")]
    public FashionReportEasySectionDto? Easy80 { get; set; }

    [JsonPropertyName("links")]
    public FashionReportLinksDto? Links { get; set; }

    [JsonPropertyName("dyesFresh")]
    public bool DyesFresh { get; set; }

    [JsonPropertyName("easy100Fresh")]
    public bool Easy100Fresh { get; set; }

    [JsonPropertyName("easy80Fresh")]
    public bool Easy80Fresh { get; set; }
}

internal sealed class FashionReportLastOptionsDto
{
    [JsonPropertyName("week")]
    public string? Week { get; set; }

    [JsonPropertyName("reportTitle")]
    public string? ReportTitle { get; set; }

    [JsonPropertyName("hints")]
    public List<FashionReportHintDto>? Hints { get; set; }
}

internal sealed class FashionReportHintDto
{
    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("ringNote")]
    public string? RingNote { get; set; }
}

internal sealed class FashionReportEasySectionDto
{
    [JsonPropertyName("itemPairs")]
    public List<FashionReportEasyItemDto>? ItemPairs { get; set; }

    [JsonPropertyName("dyes")]
    public Dictionary<string, string>? Dyes { get; set; }
}

internal sealed class FashionReportEasyItemDto
{
    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class FashionReportLinksDto
{
    [JsonPropertyName("theorycraft")]
    public string? Theorycraft { get; set; }

    [JsonPropertyName("results")]
    public string? Results { get; set; }
}

internal sealed class FashionReportHintItemsDto
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("items")]
    public List<FashionReportItemCardDto>? Items { get; set; }
}

internal sealed class FashionReportItemCardDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("garlandUrl")]
    public string? GarlandUrl { get; set; }
}

internal sealed class FashionReportItemDetailDto
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("garlandUrl")]
    public string? GarlandUrl { get; set; }

    [JsonPropertyName("lodestoneUrl")]
    public string? LodestoneUrl { get; set; }

    /// <summary>Raw section objects — shapes vary by type (vendor, craft, quest, …).</summary>
    [JsonPropertyName("sections")]
    public List<JsonElement>? Sections { get; set; }
}

internal enum FashionItemAcquireKind
{
    Unknown = 0,
    Owned = 1,
    Vendor = 2,
    Craft = 3,
    Quest = 4,
    Exchange = 5,
    GrandCompany = 6,
    Achievement = 7,
    DutyDrop = 8,
    TreasureCoffer = 9,
    Market = 10,
}

internal sealed class FashionVendorPick
{
    public required string Name { get; init; }
    public required string Location { get; init; }
    public int Gil { get; init; }
    public bool SameArea { get; init; }
    public float? DistanceSquared { get; init; }
}

internal sealed class FashionCraftIngredient
{
    public required string Name { get; init; }
    public uint ItemId { get; init; }
    public int Required { get; init; }
    public long OwnedCount { get; init; }

    public bool HasEnough => OwnedCount >= Required;
}

internal sealed class FashionAcquireSection
{
    public required string Type { get; init; }
    public required string Label { get; init; }
    public string? Headline { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    public IReadOnlyList<FashionCraftIngredient> Ingredients { get; init; } = [];
}

internal sealed class FashionResolvedItem
{
    public required string Name { get; init; }
    public uint ItemId { get; init; }
    public ushort IconId { get; init; }
    public bool Owned { get; init; }
    public FashionGearLocation GearLocations { get; init; }
    public FashionItemAcquireKind AcquireKind { get; init; }
    public string Summary { get; init; } = "Source unknown";
    public FashionVendorPick? PreferredVendor { get; init; }
    public IReadOnlyList<FashionAcquireSection> Sections { get; init; } = [];
    public IReadOnlyList<FashionCraftIngredient> CraftIngredients { get; init; } = [];
    /// <summary>Precomputed for UI — avoid LINQ in Draw.</summary>
    public bool HasCraftRecipe { get; init; }
    public int CraftMatsReady { get; init; }
    public int CraftMatsTotal { get; init; }
    public string? GarlandUrl { get; init; }
    public string? LodestoneUrl { get; init; }
    public string? SlotKey { get; init; }
    public string? SlotLabel { get; init; }
}

internal sealed class FashionHintSlotView
{
    public required string SlotKey { get; init; }
    public required string SlotLabel { get; init; }
    public required string Hint { get; init; }
    public string? RingNote { get; init; }
    public IReadOnlyList<FashionResolvedItem> Items { get; init; } = [];
    public FashionResolvedItem? BestPick { get; init; }
    public int OwnedCount { get; init; }
}

internal sealed class FashionDyeSlotView
{
    public required string SlotKey { get; init; }
    public required string SlotLabel { get; init; }
    public string? ExactDye { get; init; }
    public string? ColorFamily { get; init; }
}

internal sealed class FashionEasyOutfitView
{
    public required string Title { get; init; }
    public bool Fresh { get; init; }
    public IReadOnlyList<FashionResolvedItem> Items { get; init; } = [];
    public IReadOnlyDictionary<string, string> Dyes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class FashionReportSnapshot
{
    public string Week { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool DyesFresh { get; init; }
    public string? TheorycraftUrl { get; init; }
    public string? ResultsUrl { get; init; }
    public IReadOnlyList<FashionHintSlotView> Hints { get; init; } = [];
    public IReadOnlyList<FashionDyeSlotView> Dyes { get; init; } = [];
    public FashionEasyOutfitView? Easy80 { get; init; }
    public FashionEasyOutfitView? Easy100 { get; init; }
    public DateTime FetchedUtc { get; init; }
}
