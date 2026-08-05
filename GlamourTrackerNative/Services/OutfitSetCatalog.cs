using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

internal sealed class OutfitSetCatalog
{
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
            // Complete = all glam pieces in dresser OR every glam Mirage slot unlocked for the set.
            // Never treat presence alone as complete (that was the false 262/262 bug).
            if (set.InDresser)
            {
                setsInDresser++;
                if (this.ownershipIndex.IsOutfitSetComplete(set.SetId))
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
            foreach (var (label, slotIndex, readItemId) in OutfitSetSlots.All)
            {
                var itemId = readItemId(row);
                if (itemId == 0)
                    continue;

                var storage = this.ownershipIndex.GetStorage(itemId, row.RowId, slotIndex);
                pieces.Add(new OutfitPieceInfo(row.RowId, itemId, slotIndex, label, storage));
            }

            if (pieces.Count == 0)
                continue;

            var set = new OutfitSetInfo(row.RowId, ResolveSetName(row, itemSheet))
            {
                IsUnlocked = this.ownershipIndex.IsOutfitSetUnlockedLive(row.RowId),
                InDresser = ResolveInDresser(row.RowId, pieces),
                SetStorage = ResolveSetStorage(row, pieces),
                Pieces = pieces,
            };

            set.OwnedPieceCount = pieces.Count(p => p.Storage != GlamourStorageLocation.None);
            sets.Add(set);
        }

        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Presence in the dresser, which is not the same as being finished: the game stores an outfit
    /// either as a set row or as loose pieces, so both count.
    /// </summary>
    private bool ResolveInDresser(uint setId, List<OutfitPieceInfo> pieces) =>
        this.ownershipIndex.IsOutfitSetUnlockedLive(setId)
        || this.ownershipIndex.IsOutfitSetInDresser(setId)
        || pieces.Any(p => this.IsGlamourPiece(p.ItemId) && this.ownershipIndex.IsInDresser(p.ItemId));

    private OutfitSetStorageLocation ResolveSetStorage(MirageStoreSetItem row, List<OutfitPieceInfo> pieces)
    {
        var dresserComplete = this.ownershipIndex.IsOutfitSetComplete(row.RowId);
        var armoireComplete = IsArmoireSetComplete(pieces);

        if (dresserComplete && armoireComplete)
            return OutfitSetStorageLocation.Both;

        if (dresserComplete)
            return OutfitSetStorageLocation.Dresser;

        if (armoireComplete)
            return OutfitSetStorageLocation.Armoire;

        return OutfitSetStorageLocation.None;
    }

    private bool IsArmoireSetComplete(List<OutfitPieceInfo> pieces)
    {
        var armoirePieces = pieces.Where(p => this.cabinetCatalog.IsArmoireEligible(p.ItemId)).ToList();
        if (armoirePieces.Count == 0)
            return false;

        return armoirePieces.All(p => p.Storage.HasFlag(GlamourStorageLocation.Armoire));
    }

    private bool IsGlamourPiece(uint itemId) => this.ownershipIndex.Sets.IsGlamourPiece(itemId);

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

    /// <summary>
    /// The set is in the dresser in any form — on the stored set list, unlocked, or with at least one
    /// glam piece stored. One rule for the Overview counts and the Outfit sets filter.
    /// </summary>
    public bool InDresser { get; set; }

    public OutfitSetStorageLocation SetStorage { get; set; }
    public int OwnedPieceCount { get; set; }
    public List<OutfitPieceInfo> Pieces { get; set; } = [];

    public int TotalPieces => this.Pieces.Count;
    public int MissingPieces => this.Pieces.Count(p => p.Storage == GlamourStorageLocation.None);
}

internal readonly record struct OutfitPieceInfo(uint SetRowId, uint ItemId, int SlotIndex, string SlotLabel, GlamourStorageLocation Storage);
