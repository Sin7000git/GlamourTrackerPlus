namespace GlamourTracker.Services;

/// <summary>
/// Where one character's glamour items live.
/// </summary>
/// <remarks>
/// The dresser needs three lists rather than one. A stored outfit takes a single slot and hides the
/// pieces inside it, so the item list holds loose pieces and outfit rows, while the two set lists
/// record which outfits are present and which of those are finished. <see cref="Version"/> moves
/// whenever any of that changes, which is how views know a redraw is due.
///
/// Every member takes a lock. Refreshes arrive from framework ticks, dresser UI events, commands and
/// async completions, while tooltips and delivery markers read from wherever they happen to be drawn,
/// and two of those overlapping once was enough to throw out of the middle of a rebuild.
/// </remarks>
internal sealed class OwnershipSnapshot
{
    private readonly Lock gate = new();
    private readonly HashSet<uint> dresserItems = [];
    private readonly HashSet<uint> dresserOutfitPieces = [];
    private readonly HashSet<uint> setsInDresser = [];
    private readonly HashSet<uint> completeSetsInDresser = [];
    private readonly HashSet<uint> armoireItems = [];

    private int version;
    private int dresserSlotsUsed;
    private int totalCount;
    private int totalCountVersion = -1;

    public int Version
    {
        get { lock (this.gate) return this.version; }
    }

    public int DresserSlotsUsed
    {
        get { lock (this.gate) return this.dresserSlotsUsed; }
    }

    /// <summary>Rows in the dresser's own item list. Diagnostics — see <see cref="DresserTotalCount"/>.</summary>
    public int DresserItemCount
    {
        get { lock (this.gate) return this.dresserItems.Count; }
    }

    /// <summary>
    /// Everything the dresser can hand back: its item list plus the pieces held inside stored outfits.
    /// The game reports those separately, but to anyone reading a total they are the same thing.
    /// </summary>
    public int DresserTotalCount
    {
        get
        {
            lock (this.gate)
            {
                if (this.totalCountVersion == this.version)
                    return this.totalCount;

                var total = this.dresserItems.Count;
                foreach (var id in this.dresserOutfitPieces)
                {
                    if (!this.dresserItems.Contains(id))
                        total++;
                }

                this.totalCount = total;
                this.totalCountVersion = this.version;
                return total;
            }
        }
    }

    public int DresserOutfitPieceCount
    {
        get { lock (this.gate) return this.dresserOutfitPieces.Count; }
    }

    public int ArmoireItemCount
    {
        get { lock (this.gate) return this.armoireItems.Count; }
    }

    public int SetsInDresserCount
    {
        get { lock (this.gate) return this.setsInDresser.Count; }
    }

    public int CompleteSetsInDresserCount
    {
        get { lock (this.gate) return this.completeSetsInDresser.Count; }
    }

    public bool HasAnyItems
    {
        get { lock (this.gate) return this.dresserItems.Count > 0 || this.armoireItems.Count > 0; }
    }

    // Copies, so callers can iterate without holding the lock or racing the next refresh.
    public IReadOnlyCollection<uint> DresserItems => Copy(this.dresserItems);
    public IReadOnlyCollection<uint> DresserOutfitPieces => Copy(this.dresserOutfitPieces);
    public IReadOnlyCollection<uint> SetsInDresser => Copy(this.setsInDresser);
    public IReadOnlyCollection<uint> CompleteSetsInDresser => Copy(this.completeSetsInDresser);
    public IReadOnlyCollection<uint> ArmoireItems => Copy(this.armoireItems);

    public bool HasDresserItem(uint baseId)
    {
        lock (this.gate)
            return this.dresserItems.Contains(baseId);
    }

    /// <summary>
    /// A piece the dresser is holding inside a stored outfit rather than in a slot of its own. The
    /// outfit does not have to be complete — the box tracks each slot separately.
    /// </summary>
    public bool HasDresserOutfitPiece(uint baseId)
    {
        lock (this.gate)
            return this.dresserOutfitPieces.Contains(baseId);
    }

    public bool HasArmoireItem(uint baseId)
    {
        lock (this.gate)
            return this.armoireItems.Contains(baseId);
    }

    public bool HasSetInDresser(uint setRowId)
    {
        lock (this.gate)
            return this.setsInDresser.Contains(setRowId);
    }

    public bool HasCompleteSetInDresser(uint setRowId)
    {
        lock (this.gate)
            return this.completeSetsInDresser.Contains(setRowId);
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.dresserItems.Clear();
            this.dresserOutfitPieces.Clear();
            this.setsInDresser.Clear();
            this.completeSetsInDresser.Clear();
            this.armoireItems.Clear();
            this.dresserSlotsUsed = 0;
            this.version++;
        }
    }

    /// <summary>Refill from saved data. Callers clear first; this does not merge.</summary>
    public void Restore(
        IEnumerable<uint> dresserItemIds,
        IEnumerable<uint> outfitPieceIds,
        IEnumerable<uint> setRowIds,
        IEnumerable<uint> completeSetRowIds,
        IEnumerable<uint> armoireItemIds,
        int slotsUsed)
    {
        lock (this.gate)
        {
            this.dresserItems.UnionWith(dresserItemIds);
            this.dresserOutfitPieces.UnionWith(outfitPieceIds);
            this.setsInDresser.UnionWith(setRowIds);
            this.completeSetsInDresser.UnionWith(completeSetRowIds);
            this.armoireItems.UnionWith(armoireItemIds);
            this.dresserSlotsUsed = Math.Max(0, slotsUsed);
            this.version++;
        }
    }

    public bool SetDresserSlotsUsed(int slotsUsed)
    {
        lock (this.gate)
        {
            if (slotsUsed <= 0 || slotsUsed == this.dresserSlotsUsed)
                return false;

            this.dresserSlotsUsed = slotsUsed;
            return Changed();
        }
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
        lock (this.gate)
            return Replace(this.setsInDresser, next);
    }

    public bool AddSetInDresser(uint setRowId)
    {
        lock (this.gate)
            return this.setsInDresser.Add(setRowId) && Changed();
    }

    public bool AddCompleteSetsInDresser(IEnumerable<uint> setRowIds)
    {
        lock (this.gate)
        {
            var changed = false;
            foreach (var setRowId in setRowIds)
                changed |= this.completeSetsInDresser.Add(setRowId);

            return changed && Changed();
        }
    }

    public bool ReplaceCompleteSetsInDresser(HashSet<uint> next)
    {
        lock (this.gate)
            return Replace(this.completeSetsInDresser, next);
    }

    private bool Merge(HashSet<uint> target, HashSet<uint> live, bool replaceMissing)
    {
        lock (this.gate)
        {
            var changed = false;

            if (replaceMissing)
                changed = target.RemoveWhere(id => !live.Contains(id)) > 0;

            foreach (var id in live)
                changed |= target.Add(id);

            return changed && Changed();
        }
    }

    private IReadOnlyCollection<uint> Copy(HashSet<uint> source)
    {
        lock (this.gate)
            return source.ToArray();
    }

    private bool Replace(HashSet<uint> target, HashSet<uint> next)
    {
        if (next.SetEquals(target))
            return false;

        target.Clear();
        target.UnionWith(next);
        return Changed();
    }

    private bool Changed()
    {
        this.version++;
        return true;
    }
}
