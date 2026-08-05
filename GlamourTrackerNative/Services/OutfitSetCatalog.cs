using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

internal sealed class OutfitSetCatalog
{
    private static readonly (string Label, int SlotIndex, Func<MirageStoreSetItem, uint> ItemId)[] SlotReaders =
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

    private readonly IDataManager dataManager;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly CabinetCatalog cabinetCatalog;

    private List<OutfitSetInfo>? sets;

    public OutfitSetCatalog(
        IDataManager dataManager,
        GlamourOwnershipIndex ownershipIndex,
        CabinetCatalog cabinetCatalog)
    {
        this.dataManager = dataManager;
        this.ownershipIndex = ownershipIndex;
        this.cabinetCatalog = cabinetCatalog;
    }

    public IReadOnlyList<OutfitSetInfo> GetSets()
    {
        this.sets ??= BuildSets();
        return this.sets;
    }

    public void Invalidate() => this.sets = null;

    public int CountSetsInArmoire() =>
        GetSets().Count(set => set.SetStorage is OutfitSetStorageLocation.Armoire or OutfitSetStorageLocation.Both);

    /// <summary>One-pass outfit-set totals for the Overview tab.</summary>
    public OutfitSetOverviewStats GetOverviewStats()
    {
        var sets = GetSets();
        var dresserEligible = 0;
        var armoireEligible = 0;
        var setsInDresser = 0;
        var setsInArmoire = 0;
        var completedInDresser = 0;
        var completedInArmoire = 0;

        foreach (var set in sets)
        {
            var glamourPieces = set.Pieces.Where(p => this.IsGlamourPiece(p.ItemId)).ToList();
            var armoirePieces = set.Pieces.Where(p => this.cabinetCatalog.IsArmoireEligible(p.ItemId)).ToList();

            var canDresser = glamourPieces.Count > 0;
            var canArmoire = armoirePieces.Count > 0;
            if (canDresser)
                dresserEligible++;
            if (canArmoire)
                armoireEligible++;

            // Completed / sets-in-dresser (e.g. 73/262), not / all sets in the game.
            // Presence = set row in dresser item list, unlock bits, or any glam piece stored.
            // Complete = all glam pieces in dresser OR every glam Mirage slot unlocked for the set.
            // Never treat presence alone as complete (that was the false 262/262 bug).
            var anyGlamourInDresser = glamourPieces.Any(p => this.ownershipIndex.IsInDresser(p.ItemId));
            var inDresser = this.ownershipIndex.IsOutfitSetUnlockedLive(set.SetId)
                || this.ownershipIndex.IsOutfitSetInDresser(set.SetId)
                || anyGlamourInDresser;
            if (inDresser)
            {
                setsInDresser++;
                if (IsDresserSetCompleteForOverview(set.SetId, glamourPieces))
                    completedInDresser++;
            }

            // "In armoire" = at least one armoire-eligible piece stored there; complete = all of them.
            var armoireStoredCount = armoirePieces.Count(p => this.ownershipIndex.IsInArmoire(p.ItemId));
            var allArmoireInArmoire = canArmoire && armoireStoredCount == armoirePieces.Count;
            if (canArmoire && armoireStoredCount > 0)
            {
                setsInArmoire++;
                if (allArmoireInArmoire)
                    completedInArmoire++;
            }
        }

        // Incomplete counts are derived when needed: owned − completed.
        return new OutfitSetOverviewStats(
            DresserEligible: dresserEligible,
            ArmoireEligible: armoireEligible,
            SetsInDresser: setsInDresser,
            SetsInArmoire: setsInArmoire,
            CompletedInDresser: completedInDresser,
            CompletedInArmoire: completedInArmoire);
    }

    private List<OutfitSetInfo> BuildSets()
    {
        var itemSheet = this.dataManager.GetExcelSheet<Item>();
        var sets = new List<OutfitSetInfo>();

        foreach (var row in this.dataManager.GetExcelSheet<MirageStoreSetItem>())
        {
            if (row.RowId == 0)
                continue;

            var pieces = new List<OutfitPieceInfo>();
            foreach (var (label, slotIndex, reader) in SlotReaders)
            {
                var itemId = reader(row);
                if (itemId == 0)
                    continue;

                var storage = this.ownershipIndex.GetStorage(itemId);
                pieces.Add(new OutfitPieceInfo(row.RowId, itemId, slotIndex, label, storage));
            }

            if (pieces.Count == 0)
                continue;

            var set = new OutfitSetInfo(row.RowId, ResolveSetName(row, itemSheet))
            {
                IsUnlocked = IsOutfitSetUnlocked(row.RowId),
                SetStorage = ResolveSetStorage(row, pieces),
                Pieces = pieces,
            };

            set.OwnedPieceCount = pieces.Count(p => p.Storage != GlamourStorageLocation.None);
            sets.Add(set);
        }

        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private OutfitSetStorageLocation ResolveSetStorage(MirageStoreSetItem row, List<OutfitPieceInfo> pieces)
    {
        var dresserComplete = IsDresserSetComplete(row, pieces);
        var armoireComplete = IsArmoireSetComplete(pieces);

        if (dresserComplete && armoireComplete)
            return OutfitSetStorageLocation.Both;

        if (dresserComplete)
            return OutfitSetStorageLocation.Dresser;

        if (armoireComplete)
            return OutfitSetStorageLocation.Armoire;

        return OutfitSetStorageLocation.None;
    }

    private bool IsDresserSetComplete(MirageStoreSetItem row, List<OutfitPieceInfo> pieces)
    {
        var glamourPieces = pieces.Where(p => this.IsGlamourPiece(p.ItemId)).ToList();
        return IsDresserSetCompleteForOverview(row.RowId, glamourPieces);
    }

    /// <summary>
    /// Fully finished in the dresser: persisted Mirage-complete cache, or every glam piece is
    /// either in the dresser item list or unlocked via Mirage slot flags (0.1.102 — no PrismBoxLoaded gate).
    /// Presence alone (set row in list) is NOT enough — that was the false 262/262.
    /// </summary>
    private bool IsDresserSetCompleteForOverview(uint setId, List<OutfitPieceInfo> glamourPieces)
    {
        // Persisted completes must win even if glam-piece filtering yields an empty list.
        if (this.ownershipIndex.IsOutfitSetCompleteInDresser(setId))
            return true;

        if (glamourPieces.Count == 0)
            return false;

        return glamourPieces.All(p =>
            this.ownershipIndex.IsInDresser(p.ItemId)
            || this.ownershipIndex.IsOutfitSlotUnlocked(setId, p.SlotIndex));
    }

    private bool IsArmoireSetComplete(List<OutfitPieceInfo> pieces)
    {
        var armoirePieces = pieces.Where(p => this.cabinetCatalog.IsArmoireEligible(p.ItemId)).ToList();
        if (armoirePieces.Count == 0)
            return false;

        return armoirePieces.All(p => p.Storage.HasFlag(GlamourStorageLocation.Armoire));
    }

    private bool IsGlamourPiece(uint itemId)
    {
        var itemSheet = this.dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var item))
            return false;

        return GlamourOwnershipIndex.IsGlamourGear(item);
    }

    private static unsafe bool IsOutfitSetUnlocked(uint setId)
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return false;

        var bitIndex = (int)setId;
        var bits = finder->GlamourDresserItemSetUnlockBits;
        var wordIndex = bitIndex / 16;
        if (wordIndex < 0 || wordIndex >= bits.Length)
            return false;

        return (bits[wordIndex] & (1 << (bitIndex % 16))) != 0;
    }

    private static string ResolveSetName(MirageStoreSetItem row, Lumina.Excel.ExcelSheet<Item> itemSheet)
    {
        if (itemSheet.TryGetRow(row.RowId, out var setItem))
        {
            var setName = setItem.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(setName))
                return setName;
        }

        foreach (var reader in new Func<MirageStoreSetItem, uint>[]
                 {
                     s => s.Body.RowId,
                     s => s.Head.RowId,
                     s => s.Legs.RowId,
                     s => s.MainHand.RowId,
                 })
        {
            var pieceId = reader(row);
            if (pieceId == 0 || !itemSheet.TryGetRow(pieceId, out var piece))
                continue;

            var pieceName = piece.Name.ExtractText();
            if (pieceName.Contains("Attire", StringComparison.OrdinalIgnoreCase)
                || pieceName.Contains("Costume", StringComparison.OrdinalIgnoreCase)
                || pieceName.Contains("Set", StringComparison.OrdinalIgnoreCase))
                return pieceName;
        }

        return $"Outfit set #{row.RowId}";
    }
}

internal readonly record struct OutfitSetOverviewStats(
    int DresserEligible,
    int ArmoireEligible,
    int SetsInDresser,
    int SetsInArmoire,
    int CompletedInDresser,
    int CompletedInArmoire);

internal sealed class OutfitSetInfo
{
    public OutfitSetInfo(uint setId, string name)
    {
        this.SetId = setId;
        this.Name = name;
    }

    public uint SetId { get; }
    public string Name { get; }
    public bool IsUnlocked { get; set; }
    public OutfitSetStorageLocation SetStorage { get; set; }
    public int OwnedPieceCount { get; set; }
    public List<OutfitPieceInfo> Pieces { get; set; } = [];

    public int TotalPieces => this.Pieces.Count;
    public int MissingPieces => this.Pieces.Count(p => p.Storage == GlamourStorageLocation.None);
}

internal readonly record struct OutfitPieceInfo(uint SetRowId, uint ItemId, int SlotIndex, string SlotLabel, GlamourStorageLocation Storage);
