using System.Numerics;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Windows.Native;

internal enum OutfitSortMode
{
    Name = 0,
    Progress = 1,
    MissingFirst = 2,
}

internal enum OutfitCategoryFilter
{
    All = 0,
    Duty = 1,
    Vendor = 2,
    QuestEvent = 3,
    Craft = 4,
    Exchange = 5,
    Other = 6,
}

/// <summary>Where outfit pieces are stored (Outfit sets browser).</summary>
internal enum OutfitStorageFilter
{
    All = 0,
    Dresser = 1,
    Armoire = 2,
}

internal static class TrackerNativeHelpers
{
    public const float Indent = 20f;

    public static readonly Vector4 ColorMuted = new(0.7f, 0.7f, 0.68f, 1f);
    public static readonly Vector4 ColorOk = new(0.55f, 1f, 0.65f, 1f);
    public static readonly Vector4 ColorWarn = new(1f, 0.7f, 0.4f, 1f);
    public static readonly Vector4 ColorMissing = new(1f, 0.45f, 0.45f, 1f);
    public static readonly Vector4 ColorInfo = new(0.55f, 0.78f, 1f, 1f);
    public static readonly Vector4 ColorTitle = new(0.95f, 0.95f, 0.92f, 1f);

    public static readonly string[] SortModeLabels =
    [
        "Sort: Name",
        "Sort: Progress",
        "Sort: Missing first",
    ];

    public static readonly string[] CategoryFilterLabels =
    [
        "All sources",
        "Duties",
        "Vendors",
        "Quests & events",
        "Crafting",
        "Exchanges",
        "Other",
    ];

    public static readonly string[] StorageFilterLabels =
    [
        "All storage",
        "In dresser",
        "In armoire",
    ];

    public static bool PieceMatchesStorage(GlamourStorageLocation storage, OutfitStorageFilter filter) =>
        filter switch
        {
            OutfitStorageFilter.Dresser => storage.HasFlag(GlamourStorageLocation.Dresser),
            OutfitStorageFilter.Armoire => storage.HasFlag(GlamourStorageLocation.Armoire),
            _ => true,
        };

    public static bool IsPieceStoredForFilter(GlamourStorageLocation storage, OutfitStorageFilter filter) =>
        filter switch
        {
            OutfitStorageFilter.Dresser => storage.HasFlag(GlamourStorageLocation.Dresser),
            OutfitStorageFilter.Armoire => storage.HasFlag(GlamourStorageLocation.Armoire),
            _ => storage != GlamourStorageLocation.None,
        };

    public static bool SetMatchesStorage(OutfitSetInfo set, OutfitStorageFilter filter)
    {
        if (filter == OutfitStorageFilter.All)
            return true;

        return set.Pieces.Any(p => PieceMatchesStorage(p.Storage, filter));
    }

    /// <summary>
    /// Pieces relevant to the active storage filter, split into stored vs still missing.
    /// Dresser/armoire scopes to pieces that belong there (or are already stored there).
    /// </summary>
    public static (List<OutfitPieceInfo> Stored, List<OutfitPieceInfo> Missing, int Total) SplitPiecesForFilter(
        OutfitSetInfo set,
        OutfitStorageFilter filter,
        Func<uint, bool> isGlamourPiece,
        Func<uint, bool> isArmoireEligible)
    {
        IEnumerable<OutfitPieceInfo> scoped = filter switch
        {
            OutfitStorageFilter.Dresser => set.Pieces.Where(p =>
                p.Storage.HasFlag(GlamourStorageLocation.Dresser) || isGlamourPiece(p.ItemId)),
            OutfitStorageFilter.Armoire => set.Pieces.Where(p =>
                p.Storage.HasFlag(GlamourStorageLocation.Armoire) || isArmoireEligible(p.ItemId)),
            _ => set.Pieces,
        };

        var stored = new List<OutfitPieceInfo>();
        var missing = new List<OutfitPieceInfo>();
        foreach (var piece in scoped)
        {
            if (IsPieceStoredForFilter(piece.Storage, filter))
                stored.Add(piece);
            else
                missing.Add(piece);
        }

        return (stored, missing, stored.Count + missing.Count);
    }

    public static string FormatStorage(GlamourStorageLocation storage) => storage switch
    {
        GlamourStorageLocation.Dresser => "Dresser",
        GlamourStorageLocation.Armoire => "Armoire",
        GlamourStorageLocation.Dresser | GlamourStorageLocation.Armoire => "Dresser + Armoire",
        _ => "Missing",
    };

    public static string FormatSetStorage(OutfitSetStorageLocation storage) => storage switch
    {
        OutfitSetStorageLocation.Dresser => "Dresser",
        OutfitSetStorageLocation.Armoire => "Armoire",
        OutfitSetStorageLocation.Both => "Both",
        _ => string.Empty,
    };

    /// <summary>Ownership-based status — ignores dresser unlock bits (often stale/uncached).</summary>
    public static string FormatSetCollectionStatus(OutfitSetInfo set) =>
        FormatSetCollectionStatus(set, OutfitStorageFilter.All, _ => true, _ => true);

    public static string FormatSetCollectionStatus(
        OutfitSetInfo set,
        OutfitStorageFilter filter,
        Func<uint, bool> isGlamourPiece,
        Func<uint, bool> isArmoireEligible)
    {
        var (stored, missing, total) = SplitPiecesForFilter(set, filter, isGlamourPiece, isArmoireEligible);
        if (total == 0)
            return "No pieces for this filter";

        if (missing.Count == 0)
        {
            if (filter != OutfitStorageFilter.All)
            {
                return filter == OutfitStorageFilter.Dresser
                    ? "Complete · Dresser"
                    : "Complete · Armoire";
            }

            return set.SetStorage switch
            {
                OutfitSetStorageLocation.Dresser => "Complete · Dresser",
                OutfitSetStorageLocation.Armoire => "Complete · Armoire",
                OutfitSetStorageLocation.Both => "Complete · Dresser + Armoire",
                _ => "Complete",
            };
        }

        if (stored.Count == 0)
            return "Not collected";

        return $"{stored.Count}/{total} stored";
    }

    public static Vector4 GetSetStatusColor(OutfitSetInfo set) =>
        set.MissingPieces == 0 ? ColorOk
        : set.OwnedPieceCount == 0 ? ColorMuted
        : ColorWarn;

    public static Vector4 GetSetStatusColor(int storedCount, int missingCount) =>
        missingCount == 0 ? ColorOk
        : storedCount == 0 ? ColorMuted
        : ColorWarn;

    public static Vector4 GetSetStorageLabelColor(OutfitSetInfo set)
    {
        if (set.SetStorage == OutfitSetStorageLocation.Both)
            return ColorMissing;

        if (set.SetStorage is OutfitSetStorageLocation.Dresser or OutfitSetStorageLocation.Armoire)
        {
            return set.MissingPieces == 0
                ? ColorOk
                : ColorInfo;
        }

        return ColorMuted;
    }

    public static uint ResolveItemIcon(uint itemId)
    {
        if (itemId == 0)
            return 0;
        return Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item)
            ? (uint)item.Icon
            : 0;
    }

    public static string ResolveItemName(uint itemId)
    {
        if (itemId == 0)
            return "Unknown item";
        if (!Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return $"Item #{itemId}";
        var name = item.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"Item #{itemId}" : name;
    }

    public static OutfitCategoryFilter CategoryFromAcquireKind(FashionItemAcquireKind kind) => kind switch
    {
        FashionItemAcquireKind.DutyDrop or FashionItemAcquireKind.TreasureCoffer => OutfitCategoryFilter.Duty,
        FashionItemAcquireKind.Vendor or FashionItemAcquireKind.Market => OutfitCategoryFilter.Vendor,
        FashionItemAcquireKind.Quest or FashionItemAcquireKind.Achievement => OutfitCategoryFilter.QuestEvent,
        FashionItemAcquireKind.Craft => OutfitCategoryFilter.Craft,
        FashionItemAcquireKind.Exchange or FashionItemAcquireKind.GrandCompany => OutfitCategoryFilter.Exchange,
        FashionItemAcquireKind.Owned => OutfitCategoryFilter.All,
        _ => OutfitCategoryFilter.Other,
    };

    public static OutfitCategoryFilter AggregateSetCategory(IEnumerable<FashionItemAcquireKind> kinds)
    {
        var counts = new Dictionary<OutfitCategoryFilter, int>();
        foreach (var kind in kinds)
        {
            if (kind is FashionItemAcquireKind.Owned or FashionItemAcquireKind.Unknown)
                continue;
            var cat = CategoryFromAcquireKind(kind);
            if (cat == OutfitCategoryFilter.All)
                continue;
            counts[cat] = counts.GetValueOrDefault(cat) + 1;
        }

        if (counts.Count == 0)
            return OutfitCategoryFilter.Other;

        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }
}
