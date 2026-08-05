namespace GlamourTracker.Services;

/// <summary>
/// Where one character's glamour items live.
/// </summary>
/// <remarks>
/// The dresser needs three lists rather than one. A stored outfit takes a single slot and hides the
/// pieces inside it, so the item list holds loose pieces and outfit rows, while the two set lists
/// record which outfits are present and which of those are finished. <see cref="Version"/> moves
/// whenever any of that changes, which is how views know a redraw is due.
/// </remarks>
internal sealed class OwnershipSnapshot
{
    private readonly HashSet<uint> dresserItems = [];
    private readonly HashSet<uint> dresserOutfitPieces = [];
    private readonly HashSet<uint> setsInDresser = [];
    private readonly HashSet<uint> completeSetsInDresser = [];
    private readonly HashSet<uint> armoireItems = [];

    public int Version { get; private set; }

    public int DresserSlotsUsed { get; private set; }

    public int DresserItemCount => this.dresserItems.Count;
    public int DresserOutfitPieceCount => this.dresserOutfitPieces.Count;
    public int ArmoireItemCount => this.armoireItems.Count;
    public int SetsInDresserCount => this.setsInDresser.Count;
    public int CompleteSetsInDresserCount => this.completeSetsInDresser.Count;

    public bool HasAnyItems => this.dresserItems.Count > 0 || this.armoireItems.Count > 0;

    public IReadOnlyCollection<uint> DresserItems => this.dresserItems;
    public IReadOnlyCollection<uint> DresserOutfitPieces => this.dresserOutfitPieces;
    public IReadOnlyCollection<uint> SetsInDresser => this.setsInDresser;
    public IReadOnlyCollection<uint> CompleteSetsInDresser => this.completeSetsInDresser;
    public IReadOnlyCollection<uint> ArmoireItems => this.armoireItems;

    public bool HasDresserItem(uint baseId) => this.dresserItems.Contains(baseId);

    /// <summary>
    /// A piece the dresser is holding inside a stored outfit rather than in a slot of its own. The
    /// outfit does not have to be complete — the box tracks each slot separately.
    /// </summary>
    public bool HasDresserOutfitPiece(uint baseId) => this.dresserOutfitPieces.Contains(baseId);

    public bool HasArmoireItem(uint baseId) => this.armoireItems.Contains(baseId);

    public bool HasSetInDresser(uint setRowId) => this.setsInDresser.Contains(setRowId);

    public bool HasCompleteSetInDresser(uint setRowId) => this.completeSetsInDresser.Contains(setRowId);

    public void Clear()
    {
        this.dresserItems.Clear();
        this.dresserOutfitPieces.Clear();
        this.setsInDresser.Clear();
        this.completeSetsInDresser.Clear();
        this.armoireItems.Clear();
        this.DresserSlotsUsed = 0;
        this.Version++;
    }

    /// <summary>Refill from saved data. Callers clear first; this does not merge.</summary>
    public void Restore(
        IEnumerable<uint> dresserItemIds,
        IEnumerable<uint> outfitPieceIds,
        IEnumerable<uint> setRowIds,
        IEnumerable<uint> completeSetRowIds,
        IEnumerable<uint> armoireItemIds,
        int dresserSlotsUsed)
    {
        this.dresserItems.UnionWith(dresserItemIds);
        this.dresserOutfitPieces.UnionWith(outfitPieceIds);
        this.setsInDresser.UnionWith(setRowIds);
        this.completeSetsInDresser.UnionWith(completeSetRowIds);
        this.armoireItems.UnionWith(armoireItemIds);
        this.DresserSlotsUsed = Math.Max(0, dresserSlotsUsed);
        this.Version++;
    }

    public bool SetDresserSlotsUsed(int slotsUsed)
    {
        if (slotsUsed <= 0)
            return false;

        if (slotsUsed == this.DresserSlotsUsed)
            return false;

        this.DresserSlotsUsed = slotsUsed;
        return Changed();
    }

    /// <summary>
    /// Add live ids, and when <paramref name="replaceMissing"/> is set, drop cached ids the live read
    /// did not mention. Only pass that for a read complete enough to speak for the whole dresser.
    /// </summary>
    public bool MergeDresserItems(HashSet<uint> live, bool replaceMissing) =>
        Merge(this.dresserItems, live, replaceMissing);

    public bool MergeArmoireItems(HashSet<uint> live, bool replaceMissing) =>
        Merge(this.armoireItems, live, replaceMissing);

    /// <summary>
    /// Replaces the pieces held inside stored outfits. Only pass <paramref name="replaceMissing"/> for
    /// a scan that walked the whole box, since a partial one has no idea what it did not look at.
    /// </summary>
    public bool MergeDresserOutfitPieces(HashSet<uint> live, bool replaceMissing) =>
        Merge(this.dresserOutfitPieces, live, replaceMissing);

    public bool ReplaceSetsInDresser(HashSet<uint> next)
    {
        if (next.SetEquals(this.setsInDresser))
            return false;

        this.setsInDresser.Clear();
        this.setsInDresser.UnionWith(next);
        return Changed();
    }

    public bool AddSetInDresser(uint setRowId) =>
        this.setsInDresser.Add(setRowId) && Changed();

    public bool AddCompleteSetInDresser(uint setRowId) =>
        this.completeSetsInDresser.Add(setRowId) && Changed();

    public bool AddCompleteSetsInDresser(IEnumerable<uint> setRowIds)
    {
        var changed = false;
        foreach (var setRowId in setRowIds)
            changed |= this.completeSetsInDresser.Add(setRowId);

        return changed && Changed();
    }

    public bool ReplaceCompleteSetsInDresser(HashSet<uint> next)
    {
        if (next.SetEquals(this.completeSetsInDresser))
            return false;

        this.completeSetsInDresser.Clear();
        this.completeSetsInDresser.UnionWith(next);
        return Changed();
    }

    private bool Merge(HashSet<uint> target, HashSet<uint> live, bool replaceMissing)
    {
        var changed = false;

        if (replaceMissing)
            changed = target.RemoveWhere(id => !live.Contains(id)) > 0;

        foreach (var id in live)
            changed |= target.Add(id);

        return changed && Changed();
    }

    private bool Changed()
    {
        this.Version++;
        return true;
    }
}
