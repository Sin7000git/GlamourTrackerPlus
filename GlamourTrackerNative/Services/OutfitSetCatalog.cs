using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>
/// The outfit sets in the game and where each of their pieces currently lives.
/// </summary>
/// <remarks>
/// What an outfit is made of never changes, so the sheets are read once into templates. Only the
/// storage answers are rebuilt when ownership moves, and the Overview totals fall out of that same
/// pass — the tab and the Overview therefore count from one set of numbers and cannot disagree.
/// </remarks>
internal sealed class OutfitSetCatalog
{
    private readonly IDataManager dataManager;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly CabinetCatalog cabinetCatalog;

    private List<OutfitSetTemplate>? templates;
    private List<OutfitSetInfo>? sets;
    private OutfitSetOverviewStats overview;

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

    /// <summary>Drops the storage answers. The set metadata behind them is kept.</summary>
    public void Invalidate() => this.sets = null;

    /// <summary>Outfit-set totals for the Overview tab, tallied while the sets were built.</summary>
    public OutfitSetOverviewStats GetOverviewStats()
    {
        GetSets();
        return this.overview;
    }

    public int CountSetsInArmoire() => GetOverviewStats().SetsInArmoire;

    private List<OutfitSetInfo> BuildSets()
    {
        var built = new List<OutfitSetInfo>(Templates.Count);
        var tally = default(OverviewTally);

        foreach (var template in Templates)
            built.Add(BuildSet(template, ref tally));

        this.overview = tally.ToStats();
        return built;
    }

    private OutfitSetInfo BuildSet(OutfitSetTemplate template, ref OverviewTally tally)
    {
        var pieces = new List<OutfitPieceInfo>(template.Pieces.Length);
        var owned = 0;
        var armoireStored = 0;
        var hasOwnDresserPresence = false;

        foreach (var piece in template.Pieces)
        {
            var storage = this.ownershipIndex.GetStorage(piece.ItemId, template.SetId, piece.SlotIndex);
            pieces.Add(new OutfitPieceInfo(template.SetId, piece.ItemId, piece.SlotIndex, piece.SlotLabel, storage));

            if (storage != GlamourStorageLocation.None)
                owned++;

            if (piece.IsArmoireEligible && storage.HasFlag(GlamourStorageLocation.Armoire))
                armoireStored++;

            if (piece.IsGlamourPiece && IsOwnDresserPresence(piece.ItemId, template.SetId, piece.SlotIndex))
                hasOwnDresserPresence = true;
        }

        var dresserState = ResolveDresserState(template.SetId, hasOwnDresserPresence);
        var armoireComplete = template.ArmoirePieceCount > 0 && armoireStored == template.ArmoirePieceCount;

        Tally(ref tally, template, armoireStored, armoireComplete);

        return new OutfitSetInfo
        {
            SetId = template.SetId,
            Name = template.Name,
            IsUnlocked = this.ownershipIndex.IsOutfitSetUnlockedLive(template.SetId),
            DresserState = dresserState,
            SetStorage = ResolveSetStorage(dresserState == SetDresserState.Complete, armoireComplete),
            Pieces = pieces,
            OwnedPieceCount = owned,
        };
    }

    /// <summary>
    /// Presence in the dresser is not the same as being finished: the game stores an outfit either as
    /// a set row or as loose pieces, so both count as present.
    /// </summary>
    private SetDresserState ResolveDresserState(uint setId, bool hasOwnDresserPresence)
    {
        if (this.ownershipIndex.IsOutfitSetInDresser(setId)
            && this.ownershipIndex.IsOutfitSetComplete(setId))
            return SetDresserState.Complete;

        if (this.ownershipIndex.IsOutfitSetInDresser(setId) || hasOwnDresserPresence)
            return SetDresserState.Partial;

        return SetDresserState.None;
    }

    /// <summary>
    /// Whether this outfit itself accounts for the piece in the dresser — not merely that the same
    /// appearance sits inside some other stored outfit (that is what inflated 264 to 276).
    /// </summary>
    private bool IsOwnDresserPresence(uint itemId, uint setRowId, int slotIndex)
    {
        if (this.ownershipIndex.IsOutfitSlotUnlocked(setRowId, slotIndex))
            return true;

        // Loose dresser row only — pieces held inside outfits are attributed to those outfits.
        return this.ownershipIndex.IsInDresserItemList(itemId)
               && !this.ownershipIndex.IsDresserOutfitPiece(itemId);
    }

    private void Tally(
        ref OverviewTally tally,
        OutfitSetTemplate template,
        int armoireStored,
        bool armoireComplete)
    {
        if (template.GlamourPieceCount > 0)
            tally.DresserEligible++;

        if (template.ArmoirePieceCount > 0)
            tally.ArmoireEligible++;

        // Overview denominators are the outfits stored as set rows in the dresser (264), not every
        // sheet row that happens to share a piece with one of those outfits (that was the 276).
        if (this.ownershipIndex.IsOutfitSetInDresser(template.SetId))
        {
            tally.SetsInDresser++;
            if (this.ownershipIndex.IsOutfitSetComplete(template.SetId))
                tally.CompletedInDresser++;
        }

        // "In armoire" = at least one armoire-eligible piece stored there; complete = all of them.
        if (template.ArmoirePieceCount > 0 && armoireStored > 0)
        {
            tally.SetsInArmoire++;
            if (armoireComplete)
                tally.CompletedInArmoire++;
        }
    }

    private static OutfitSetStorageLocation ResolveSetStorage(bool dresserComplete, bool armoireComplete) =>
        (dresserComplete, armoireComplete) switch
        {
            (true, true) => OutfitSetStorageLocation.Both,
            (true, false) => OutfitSetStorageLocation.Dresser,
            (false, true) => OutfitSetStorageLocation.Armoire,
            _ => OutfitSetStorageLocation.None,
        };

    private List<OutfitSetTemplate> Templates => this.templates ??= BuildTemplates();

    private List<OutfitSetTemplate> BuildTemplates()
    {
        var itemSheet = this.dataManager.GetExcelSheet<Item>();
        var built = new List<OutfitSetTemplate>();

        foreach (var row in this.dataManager.GetExcelSheet<MirageStoreSetItem>())
        {
            if (row.RowId == 0)
                continue;

            var pieces = new List<OutfitPieceTemplate>();
            var glamourPieces = 0;
            var armoirePieces = 0;

            foreach (var (label, slotIndex, readItemId) in OutfitSetSlots.All)
            {
                var itemId = readItemId(row);
                if (itemId == 0)
                    continue;

                var isGlamourPiece = this.ownershipIndex.Sets.IsGlamourPiece(itemId);
                var isArmoireEligible = this.cabinetCatalog.IsArmoireEligible(itemId);
                if (isGlamourPiece)
                    glamourPieces++;
                if (isArmoireEligible)
                    armoirePieces++;

                pieces.Add(new OutfitPieceTemplate(itemId, slotIndex, label, isGlamourPiece, isArmoireEligible));
            }

            if (pieces.Count == 0)
                continue;

            built.Add(new OutfitSetTemplate(
                row.RowId,
                ResolveSetName(row, itemSheet),
                pieces.ToArray(),
                glamourPieces,
                armoirePieces));
        }

        return built.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ResolveSetName(MirageStoreSetItem row, ExcelSheet<Item> itemSheet)
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

    private struct OverviewTally
    {
        public int DresserEligible;
        public int ArmoireEligible;
        public int SetsInDresser;
        public int SetsInArmoire;
        public int CompletedInDresser;
        public int CompletedInArmoire;

        // Incomplete counts are derived when needed: owned − completed.
        public readonly OutfitSetOverviewStats ToStats() =>
            new(
                DresserEligible: this.DresserEligible,
                ArmoireEligible: this.ArmoireEligible,
                SetsInDresser: this.SetsInDresser,
                SetsInArmoire: this.SetsInArmoire,
                CompletedInDresser: this.CompletedInDresser,
                CompletedInArmoire: this.CompletedInArmoire);
    }
}

/// <summary>What an outfit is made of. Read from the sheets once and then reused.</summary>
internal sealed record OutfitSetTemplate(
    uint SetId,
    string Name,
    OutfitPieceTemplate[] Pieces,
    int GlamourPieceCount,
    int ArmoirePieceCount);

internal readonly record struct OutfitPieceTemplate(
    uint ItemId,
    int SlotIndex,
    string SlotLabel,
    bool IsGlamourPiece,
    bool IsArmoireEligible);

internal readonly record struct OutfitSetOverviewStats(
    int DresserEligible,
    int ArmoireEligible,
    int SetsInDresser,
    int SetsInArmoire,
    int CompletedInDresser,
    int CompletedInArmoire);

/// <summary>How much of an outfit the dresser holds. One answer for the Overview and the tab.</summary>
internal enum SetDresserState
{
    /// <summary>Nothing of this outfit is in the dresser.</summary>
    None = 0,

    /// <summary>The dresser holds the outfit or some of its pieces, but not all of them.</summary>
    Partial = 1,

    /// <summary>Every glamour slot of the outfit is accounted for.</summary>
    Complete = 2,
}

internal sealed class OutfitSetInfo
{
    public required uint SetId { get; init; }
    public required string Name { get; init; }
    public bool IsUnlocked { get; init; }
    public SetDresserState DresserState { get; init; }
    public OutfitSetStorageLocation SetStorage { get; init; }
    public int OwnedPieceCount { get; init; }
    public required List<OutfitPieceInfo> Pieces { get; init; }

    /// <summary>The dresser holds this outfit in some form. Used by the Overview and the tab filter.</summary>
    public bool InDresser => this.DresserState != SetDresserState.None;

    public int TotalPieces => this.Pieces.Count;
    public int MissingPieces => this.TotalPieces - this.OwnedPieceCount;
}

internal readonly record struct OutfitPieceInfo(uint SetRowId, uint ItemId, int SlotIndex, string SlotLabel, GlamourStorageLocation Storage);
