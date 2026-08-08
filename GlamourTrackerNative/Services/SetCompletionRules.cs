using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

internal readonly record struct SetPiece(uint ItemId, int SlotIndex);

/// <summary>
/// The single definition of a finished outfit, plus the set metadata needed to answer it.
/// </summary>
/// <remarks>
/// An outfit is finished when every glamour piece it contains is either sitting in the dresser as an
/// item or unlocked in that outfit's Mirage slot. Being on the dresser's set list means the outfit is
/// present, never that it is finished; conflating the two is what once reported 262 of 262.
/// </remarks>
internal sealed class SetCompletionRules
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, bool> isGlamourPiece = [];

    private ExcelSheet<Item>? itemSheet;
    private HashSet<uint>? allSetRowIds;
    private Dictionary<uint, SetPiece[]>? glamourPiecesBySet;
    private Dictionary<uint, uint[]>? setsByPieceItemId;

    public SetCompletionRules(IDataManager dataManager) => this.dataManager = dataManager;

    /// <summary>Every outfit set in the game. Dresser item ids matching one of these are set rows.</summary>
    public HashSet<uint> AllSetRowIds
    {
        get
        {
            this.allSetRowIds ??= this.dataManager.GetExcelSheet<MirageStoreSetItem>()
                .Where(row => row.RowId != 0)
                .Select(row => row.RowId)
                .ToHashSet();
            return this.allSetRowIds;
        }
    }

    public bool IsGlamourPiece(uint itemId)
    {
        if (this.isGlamourPiece.TryGetValue(itemId, out var known))
            return known;

        this.itemSheet ??= this.dataManager.GetExcelSheet<Item>();
        var result = this.itemSheet.TryGetRow(itemId, out var item) && GlamourOwnershipIndex.IsGlamourGear(item);
        this.isGlamourPiece[itemId] = result;
        return result;
    }

    public IReadOnlyList<SetPiece> GlamourPieces(uint setRowId)
    {
        EnsureSetIndexes();
        return this.glamourPiecesBySet!.TryGetValue(setRowId, out var pieces) ? pieces : [];
    }

    /// <summary>
    /// Whether this outfit is finished: every glamour piece of it either stored loose in the dresser
    /// or, when live slot flags may be consulted, held in this outfit's own slot.
    /// </summary>
    /// <remarks>
    /// A piece only counts as loose when the dresser lists it outside any outfit. The dresser's item
    /// list unfolds stored outfits, so it also names pieces that belong to other outfits entirely,
    /// and accepting those declared an outfit finished on the strength of somebody else's pieces.
    /// </remarks>
    public bool IsComplete(uint setRowId, OwnershipSnapshot snapshot, bool useMirageSlots)
    {
        var pieces = GlamourPieces(setRowId);
        if (pieces.Count == 0)
            return false;

        foreach (var piece in pieces)
        {
            var baseId = ItemIdHelper.GlamourBaseId(piece.ItemId);
            if (snapshot.HasDresserItem(baseId) && !snapshot.HasDresserOutfitPiece(baseId))
                continue;

            if (useMirageSlots && OwnershipGameReader.IsSetSlotUnlocked(setRowId, piece.SlotIndex))
                continue;

            return false;
        }

        return true;
    }

    /// <summary>
    /// True when this piece belongs to an outfit already known to be finished. A stored outfit takes a
    /// single dresser slot, so until the dresser is opened its pieces are absent from the item list.
    /// </summary>
    public bool IsPieceOfCompleteSet(uint baseItemId, OwnershipSnapshot snapshot)
    {
        if (baseItemId == 0 || snapshot.CompleteSetsInDresserCount == 0)
            return false;

        EnsureSetIndexes();
        if (!this.setsByPieceItemId!.TryGetValue(baseItemId, out var setRowIds))
            return false;

        foreach (var setRowId in setRowIds)
        {
            if (snapshot.HasCompleteSetInDresser(setRowId))
                return true;
        }

        return false;
    }

    private void EnsureSetIndexes()
    {
        if (this.glamourPiecesBySet != null)
            return;

        var bySet = new Dictionary<uint, SetPiece[]>();
        var byPiece = new Dictionary<uint, HashSet<uint>>();

        foreach (var row in this.dataManager.GetExcelSheet<MirageStoreSetItem>())
        {
            if (row.RowId == 0)
                continue;

            var pieces = new List<SetPiece>();
            foreach (var (_, slotIndex, readItemId) in OutfitSetSlots.All)
            {
                var itemId = readItemId(row);
                if (itemId == 0 || !IsGlamourPiece(itemId))
                    continue;

                pieces.Add(new SetPiece(itemId, slotIndex));

                var baseId = ItemIdHelper.GlamourBaseId(itemId);
                if (!byPiece.TryGetValue(baseId, out var owners))
                    byPiece[baseId] = owners = [];

                owners.Add(row.RowId);
            }

            if (pieces.Count > 0)
                bySet[row.RowId] = pieces.ToArray();
        }

        this.glamourPiecesBySet = bySet;
        this.setsByPieceItemId = byPiece.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }
}
