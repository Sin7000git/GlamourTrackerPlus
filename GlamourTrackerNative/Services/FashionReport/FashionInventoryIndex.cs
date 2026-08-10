using System.Collections.Immutable;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace GlamourTracker.Services.FashionReport;

[Flags]
internal enum FashionGearLocation
{
    None = 0,
    Inventory = 1,
    Armoury = 2,
    Saddlebag = 4,
    Dresser = 8,
    Armoire = 16,
}

internal sealed class FashionInventorySnapshot
{
    public required IReadOnlyDictionary<uint, long> CountsByItemId { get; init; }
    public required IReadOnlyDictionary<uint, FashionGearLocation> LocationsByItemId { get; init; }
    public DateTime ScannedAtUtc { get; init; }

    public static FashionInventorySnapshot Empty { get; } = new()
    {
        CountsByItemId = ImmutableDictionary<uint, long>.Empty,
        LocationsByItemId = ImmutableDictionary<uint, FashionGearLocation>.Empty,
        ScannedAtUtc = DateTime.MinValue,
    };

    public long GetCount(uint itemId)
    {
        var baseId = ItemIdHelper.GlamourBaseId(itemId);
        return CountsByItemId.TryGetValue(baseId, out var n) ? n : 0;
    }

    public FashionGearLocation GetCarryLocations(uint itemId)
    {
        var baseId = ItemIdHelper.GlamourBaseId(itemId);
        return LocationsByItemId.TryGetValue(baseId, out var loc) ? loc : FashionGearLocation.None;
    }
}

/// <summary>Scans character bags, armoury chest, and saddlebags (framework thread).</summary>
internal sealed class FashionInventoryIndex
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private static readonly GameInventoryType[] ExcludedTypes =
    [
        GameInventoryType.Cosmopouch1,
        GameInventoryType.Cosmopouch2,
    ];

    private static readonly GameInventoryType[] ScanTypes = BuildScanTypes();

    private readonly IGameInventory gameInventory;
    private FashionInventorySnapshot? cached;
    private DateTime cachedAtUtc = DateTime.MinValue;

    public FashionInventoryIndex(IGameInventory gameInventory)
    {
        this.gameInventory = gameInventory;
    }

    public FashionInventorySnapshot Scan(bool force = false)
    {
        if (!force
            && this.cached != null
            && DateTime.UtcNow - this.cachedAtUtc < CacheTtl)
            return this.cached;

        var counts = new Dictionary<uint, long>();
        var locations = new Dictionary<uint, FashionGearLocation>();

        foreach (var type in ScanTypes)
        {
            ReadOnlySpan<GameInventoryItem> items;
            try
            {
                items = gameInventory.GetInventoryItems(type);
            }
            catch
            {
                continue;
            }

            if (items.IsEmpty)
                continue;

            var bucket = Classify(type.ToString());
            if (bucket == FashionGearLocation.None)
                continue;

            foreach (ref readonly var item in items)
            {
                if (item.ItemId == 0 || item.Quantity <= 0)
                    continue;

                var baseId = item.BaseItemId;
                if (baseId == 0)
                    baseId = ItemIdHelper.GlamourBaseId(item.ItemId);

                counts.TryGetValue(baseId, out var existing);
                counts[baseId] = existing + item.Quantity;

                locations.TryGetValue(baseId, out var loc);
                locations[baseId] = loc | bucket;
            }
        }

        var snapshot = new FashionInventorySnapshot
        {
            CountsByItemId = counts,
            LocationsByItemId = locations,
            ScannedAtUtc = DateTime.UtcNow,
        };
        this.cached = snapshot;
        this.cachedAtUtc = snapshot.ScannedAtUtc;
        return snapshot;
    }

    private static GameInventoryType[] BuildScanTypes()
    {
        var list = new List<GameInventoryType>();
        foreach (GameInventoryType type in Enum.GetValues<GameInventoryType>())
        {
            if (ExcludedTypes.Contains(type))
                continue;

            var name = type.ToString();
            if (!ShouldScan(name))
                continue;
            if (Classify(name) == FashionGearLocation.None)
                continue;

            list.Add(type);
        }

        return list.ToArray();
    }

    private static bool ShouldScan(string name)
    {
        if (name.StartsWith("Retainer", StringComparison.Ordinal))
            return false;
        if (name.StartsWith("FreeCompany", StringComparison.Ordinal))
            return false;
        if (name.StartsWith("Chocobo", StringComparison.Ordinal))
            return false;
        if (name is "HandIn" or "Mail" or "MailEdit" or "Gil" or "RetainerGil" or "FreeCompanyGil")
            return false;
        return true;
    }

    private static FashionGearLocation Classify(string name)
    {
        if (name.Contains("Saddle", StringComparison.Ordinal))
            return FashionGearLocation.Saddlebag;

        if (name.Contains("Armoury", StringComparison.Ordinal) || name.Contains("Armory", StringComparison.Ordinal))
            return FashionGearLocation.Armoury;

        // Main inventory pages / crystals / equipped are treated as on-character inventory.
        if (name.StartsWith("Inventory", StringComparison.Ordinal)
            || name is "Crystals" or "Currency"
            || name.Contains("Equipped", StringComparison.Ordinal))
            return FashionGearLocation.Inventory;

        // Catch remaining player-carry containers (e.g. Inventory1..4 variants already covered).
        if (!name.Contains("Retainer", StringComparison.Ordinal)
            && !name.Contains("Saddle", StringComparison.Ordinal)
            && !name.Contains("Armoury", StringComparison.Ordinal)
            && !name.Contains("Armory", StringComparison.Ordinal)
            && (name.Contains("Bag", StringComparison.Ordinal) || name.Contains("Pouch", StringComparison.Ordinal)))
            return FashionGearLocation.Inventory;

        return FashionGearLocation.None;
    }

    public static string FormatLocations(FashionGearLocation locations)
    {
        if (locations == FashionGearLocation.None)
            return "not found";

        var parts = new List<string>(5);
        if (locations.HasFlag(FashionGearLocation.Inventory))
            parts.Add("bags");
        if (locations.HasFlag(FashionGearLocation.Armoury))
            parts.Add("armoury");
        if (locations.HasFlag(FashionGearLocation.Saddlebag))
            parts.Add("saddlebag");
        if (locations.HasFlag(FashionGearLocation.Dresser))
            parts.Add("dresser");
        if (locations.HasFlag(FashionGearLocation.Armoire))
            parts.Add("armoire");
        return string.Join(" + ", parts);
    }
}
