using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>
/// Keeps one <see cref="OwnershipSnapshot"/> per character in step with the game and with saved data,
/// and answers every "where does this item live" question the rest of the plugin asks.
/// </summary>
/// <remarks>
/// Reads come from three game structures that disagree about detail and are each trustworthy only at
/// certain times, so a pass never simply overwrites what came before: it declares how much it saw and
/// only that much is allowed to expire. Saved data is treated the same way, because a login that never
/// opens the dresser must still show the same numbers as the session that saved them.
/// </remarks>
internal sealed class GlamourOwnershipIndex
{
    private const int RefreshIntervalSeconds = 5;

    private readonly CabinetCatalog cabinetCatalog;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;
    private readonly IClientState clientState;
    private readonly Func<ulong> getContentId;
    private readonly OwnershipSnapshot snapshot = new();

    private DateTime lastRefresh = DateTime.MinValue;
    private ulong activeContentId;
    private bool pendingContentIdLoad;
    private HashSet<uint>? previousLiveDresser;
    private HashSet<uint>? previousLiveArmoire;

    public GlamourOwnershipIndex(
        IDataManager dataManager,
        CabinetCatalog cabinetCatalog,
        Func<Configuration> getConfiguration,
        IClientState clientState,
        Func<ulong> getContentId)
    {
        this.dataManager = dataManager;
        this.cabinetCatalog = cabinetCatalog;
        this.getConfiguration = getConfiguration;
        this.clientState = clientState;
        this.getContentId = getContentId;
        this.Sets = new SetCompletionRules(dataManager);

        if (!this.clientState.IsLoggedIn)
            return;

        var contentId = this.getContentId();
        if (contentId == 0)
        {
            // Plugin reload while logged in can race ContentId — defer like OnCharacterLogin(0).
            this.pendingContentIdLoad = true;
            return;
        }

        LoadPersistedForCharacter(contentId);
    }

    /// <summary>Outfit set metadata and the definition of a finished set, shared with the catalog.</summary>
    public SetCompletionRules Sets { get; }

    public DateTime LastRefresh => this.lastRefresh;

    /// <summary>Moves whenever stored ownership changes, so views can tell if a rebuild is needed.</summary>
    public int Revision => this.snapshot.Version;

    public int DresserUniqueCount => this.snapshot.DresserItemCount;
    public int DresserSlotsUsed => this.snapshot.DresserSlotsUsed;
    public int ArmoireCount => this.snapshot.ArmoireItemCount;
    public int OutfitSetsInDresser => this.snapshot.SetsInDresserCount;
    public bool HasPersistedData => this.snapshot.HasAnyItems;

    public void OnCharacterLogin(ulong contentId)
    {
        if (contentId == 0)
        {
            // ContentId can lag a tick behind IsLoggedIn — defer so we don't wipe cache.
            this.pendingContentIdLoad = true;
            ClearRuntimeCache();
            this.activeContentId = 0;
            return;
        }

        this.pendingContentIdLoad = false;
        LoadPersistedForCharacter(contentId);
    }

    /// <summary>Call from framework update while logged in to finish a deferred ContentId load.</summary>
    public bool TryFinishPendingLoginLoad()
    {
        if (!this.pendingContentIdLoad || !this.clientState.IsLoggedIn)
            return false;

        var contentId = this.getContentId();
        if (contentId == 0)
            return false;

        this.pendingContentIdLoad = false;
        LoadPersistedForCharacter(contentId);
        return true;
    }

    public void OnCharacterLogout()
    {
        SavePersistedForCharacter(this.activeContentId, dresserSpokeForWholeBox: false);
        this.pendingContentIdLoad = false;
    }

    public void ClearRuntimeCache()
    {
        this.snapshot.Clear();
        this.lastRefresh = DateTime.MinValue;
        this.previousLiveDresser = null;
        this.previousLiveArmoire = null;
    }

    public void Refresh(bool force = false)
    {
        if (!this.clientState.IsLoggedIn)
            return;

        if (this.pendingContentIdLoad)
            TryFinishPendingLoginLoad();

        var contentId = this.getContentId();
        if (contentId == 0)
            return;

        if (this.activeContentId != contentId)
            LoadPersistedForCharacter(contentId);

        if (!force && (DateTime.UtcNow - this.lastRefresh).TotalSeconds < RefreshIntervalSeconds)
            return;

        try
        {
            var liveDresser = new HashSet<uint>();
            var liveArmoire = new HashSet<uint>();

            var dresser = OwnershipGameReader.ReadDresser(liveDresser);
            var armoireRead = OwnershipGameReader.ReadArmoire(this.cabinetCatalog, liveArmoire);

            // Even with no live read, set presence and completion can be rebuilt from saved data, so a
            // login that never opens the dresser keeps its counts.
            if (!dresser.FoundAnything && !armoireRead && !this.snapshot.HasAnyItems)
                return;

            var dresserChanged = false;
            if (dresser.FoundAnything)
            {
                var (ids, mayPrune) = ConfirmRemovals(
                    liveDresser,
                    ref this.previousLiveDresser,
                    dresser.SpeaksForWholeDresser && liveDresser.Count > 0);

                dresserChanged = this.snapshot.MergeDresserItems(ids, mayPrune);
                dresserChanged |= this.snapshot.SetDresserSlotsUsed(dresser.SlotsUsed);
            }

            dresserChanged |= RefreshSetPresence();
            dresserChanged |= RefreshStoredOutfits(contentId);

            var armoireChanged = false;
            if (armoireRead)
            {
                // A loaded cabinet is the whole armoire, so items it stops listing really are gone.
                var (ids, mayPrune) = ConfirmRemovals(liveArmoire, ref this.previousLiveArmoire, mayPrune: true);
                armoireChanged = this.snapshot.MergeArmoireItems(ids, mayPrune);
            }
            this.lastRefresh = DateTime.UtcNow;

            if (!dresserChanged && !armoireChanged)
                return;

            SavePersistedForCharacter(contentId, dresser.SpeaksForWholeDresser);
            PluginFileLog.Info(
                "ownership.refresh",
                $"dresser={this.snapshot.DresserItemCount} outfitPieces={this.snapshot.DresserOutfitPieceCount} " +
                $"slots={this.snapshot.DresserSlotsUsed} sets={this.snapshot.SetsInDresserCount} " +
                $"completeSets={this.snapshot.CompleteSetsInDresserCount} " +
                $"armoire={this.snapshot.ArmoireItemCount} auth={dresser.SpeaksForWholeDresser}");
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("ownership.refresh", "Dresser/armoire refresh failed", ex);
        }
    }

    public GlamourStorageLocation GetStorage(uint itemId)
    {
        var location = GlamourStorageLocation.None;
        var baseId = ItemIdHelper.GlamourBaseId(itemId);

        if (IsBaseIdInDresser(baseId))
            location |= GlamourStorageLocation.Dresser;

        if (this.snapshot.HasArmoireItem(baseId))
            location |= GlamourStorageLocation.Armoire;

        return location;
    }

    /// <summary>
    /// Storage for a piece looked at as part of a particular outfit, which can say more than the item
    /// id alone: an outfit open in the dresser reports its slots individually even when only some of
    /// them are filled.
    /// </summary>
    public GlamourStorageLocation GetStorage(uint itemId, uint setRowId, int slotIndex)
    {
        var storage = GetStorage(itemId);
        if (storage.HasFlag(GlamourStorageLocation.Dresser) || !this.Sets.IsGlamourPiece(itemId))
            return storage;

        if (OwnershipGameReader.IsSetSlotUnlocked(setRowId, slotIndex))
            storage |= GlamourStorageLocation.Dresser;

        return storage;
    }

    public bool IsStored(uint itemId) => GetStorage(itemId) != GlamourStorageLocation.None;

    public bool IsInDresser(uint itemId) =>
        IsBaseIdInDresser(ItemIdHelper.GlamourBaseId(itemId));

    /// <summary>
    /// Physical dresser contents only, with no set-piece inference. Completeness math needs this so a
    /// set cannot be called finished just because other finished sets happen to share its pieces.
    /// </summary>
    public bool IsInDresserItemList(uint itemId) =>
        this.snapshot.HasDresserItem(ItemIdHelper.GlamourBaseId(itemId));

    public bool IsInArmoire(uint itemId) =>
        this.snapshot.HasArmoireItem(ItemIdHelper.GlamourBaseId(itemId));

    /// <summary>True when the outfit is on the dresser's set list, which is presence, not completion.</summary>
    public bool IsOutfitSetInDresser(uint setRowId) =>
        this.snapshot.HasSetInDresser(setRowId);

    /// <summary>True when every glamour slot of the outfit is unlocked in the dresser.</summary>
    public bool IsOutfitSetCompleteInDresser(uint setRowId) =>
        this.snapshot.HasCompleteSetInDresser(setRowId);

    public bool IsOutfitSetUnlockedLive(uint setRowId) =>
        OwnershipGameReader.IsSetUnlockedInFinder(setRowId);

    public bool IsOutfitSlotUnlocked(uint setRowId, int slotIndex) =>
        OwnershipGameReader.IsSetSlotUnlocked(setRowId, slotIndex);

    /// <summary>Whether the set counts as finished right now, including live dresser slot flags.</summary>
    public bool IsOutfitSetComplete(uint setRowId) =>
        this.snapshot.HasCompleteSetInDresser(setRowId)
        || this.Sets.IsComplete(setRowId, this.snapshot, useMirageSlots: true);

    public static bool IsGlamourGear(Item item) =>
        item.EquipSlotCategory.RowId != 0 && item.ItemUICategory.RowId is not 59 and not 60;

    private bool IsBaseIdInDresser(uint baseId) =>
        this.snapshot.HasDresserItem(baseId)
        || this.snapshot.HasDresserOutfitPiece(baseId)
        || this.Sets.IsPieceOfCompleteSet(baseId, this.snapshot);

    /// <summary>
    /// Decides what a read is allowed to forget. A single read can be a moment of nonsense — a cabinet
    /// halfway through loading, a dresser halfway through opening — and one of those quietly deleted
    /// 346 armoire entries, so an id has to be missing from two reads in a row before it counts as
    /// gone, and the first read of a session is never allowed to remove anything by itself.
    /// </summary>
    private static (HashSet<uint> Ids, bool MayPrune) ConfirmRemovals(
        HashSet<uint> live,
        ref HashSet<uint>? previousLive,
        bool mayPrune)
    {
        var previous = previousLive;
        previousLive = live;

        if (!mayPrune || previous == null)
            return (live, false);

        var spared = new HashSet<uint>(live);
        spared.UnionWith(previous);
        return (spared, true);
    }

    /// <summary>
    /// Which outfits are in the dresser. The item list holds an outfit as a set row id, and ItemFinder
    /// keeps unlock bits that can name outfits the item list has not caught up with.
    /// </summary>
    private bool RefreshSetPresence()
    {
        var changed = false;

        if (this.snapshot.DresserItemCount > 0)
        {
            var present = new HashSet<uint>();
            foreach (var id in this.snapshot.DresserItems)
            {
                if (this.Sets.AllSetRowIds.Contains(id))
                    present.Add(id);
            }

            if (this.snapshot.ReplaceSetsInDresser(present))
            {
                changed = true;
                PluginFileLog.Info(
                    "ownership.set-presence",
                    $"Set-list presence from dresser items: {present.Count} sets " +
                    $"(of {this.snapshot.DresserItemCount} items)");
            }
        }

        try
        {
            // Add-only: unlock bits must never retract what the item list established.
            foreach (var setRowId in OwnershipGameReader.UnlockedSetsInFinder(this.Sets.AllSetRowIds))
                changed |= this.snapshot.AddSetInDresser(setRowId);
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("ownership.finder-sets", "Sync set unlock bits failed", ex);
        }

        return changed;
    }

    /// <summary>
    /// Works out what the stored outfits are holding: which pieces are in them, and which of them are
    /// whole. The scan is the authority for the outfits it saw; anything else falls back to the item
    /// list, which is the only evidence for an outfit kept as loose pieces.
    /// </summary>
    private bool RefreshStoredOutfits(ulong contentId)
    {
        var changed = false;
        var scanned = new HashSet<uint>();

        try
        {
            changed |= ScanStoredOutfits(ref scanned);
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("ownership.mirage-sets", "Reading stored outfit slots failed", ex);
        }

        if (this.snapshot.SetsInDresserCount > 0 && this.snapshot.DresserItemCount > 0)
        {
            // Outfits the scan already ruled on are left alone, or the two keep overruling each other
            // and every refresh looks like a change worth saving.
            var complete = this.snapshot.SetsInDresser
                .Where(setRowId => !scanned.Contains(setRowId))
                .Where(setRowId => !this.snapshot.HasCompleteSetInDresser(setRowId))
                .Where(setRowId => this.Sets.IsComplete(setRowId, this.snapshot, useMirageSlots: false))
                .ToList();

            changed |= this.snapshot.AddCompleteSetsInDresser(complete);
        }

        if (this.snapshot.CompleteSetsInDresserCount == 0 && TryHydrateCompleteSetsFromConfig(contentId))
            changed = true;

        return changed;
    }

    private bool ScanStoredOutfits(ref HashSet<uint> evaluated)
    {
        if (!OwnershipGameReader.TryScanStoredOutfits(
                this.dataManager.GetExcelSheet<MirageStoreSetItem>(),
                this.Sets.AllSetRowIds,
                out var scan))
            return false;

        if (scan.Evaluated.Count == 0)
        {
            PluginFileLog.Info(
                "ownership.mirage-sets",
                $"Outfit scan saw no set rows (prismLen={scan.PrismBoxLength}) — " +
                $"keeping cache={this.snapshot.CompleteSetsInDresserCount}");
            return false;
        }

        evaluated = scan.Evaluated;

        var completes = new HashSet<uint>(this.snapshot.CompleteSetsInDresser);
        completes.ExceptWith(scan.Evaluated);
        completes.UnionWith(scan.Complete);

        var before = this.snapshot.CompleteSetsInDresserCount;
        var changed = this.snapshot.ReplaceCompleteSetsInDresser(completes);

        // Only a scan that covered every outfit we know about may forget pieces.
        var sawEverything = scan.Evaluated.Count >= this.snapshot.SetsInDresserCount;
        changed |= this.snapshot.MergeDresserOutfitPieces(scan.UnlockedPieces, replaceMissing: sawEverything);

        if (changed)
        {
            PluginFileLog.Info(
                "ownership.mirage-sets",
                $"Stored outfits: {scan.Evaluated.Count} scanned, {completes.Count} complete (was {before}), " +
                $"{this.snapshot.DresserOutfitPieceCount} pieces held inside them");
        }

        return changed;
    }

    private bool TryHydrateCompleteSetsFromConfig(ulong contentId)
    {
        if (contentId == 0 || this.snapshot.CompleteSetsInDresserCount > 0)
            return false;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var cache)
            || cache.DresserCompleteSetRowIds.Count == 0)
            return false;

        this.snapshot.AddCompleteSetsInDresser(cache.DresserCompleteSetRowIds);
        PluginFileLog.Info(
            "ownership.hydrate",
            $"Restored {this.snapshot.CompleteSetsInDresserCount} complete sets from saved data");
        return true;
    }

    private void LoadPersistedForCharacter(ulong contentId)
    {
        this.activeContentId = contentId;
        ClearRuntimeCache();

        if (contentId == 0)
            return;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var cache))
            return;

        this.snapshot.Restore(
            cache.DresserBaseIds,
            cache.DresserOutfitPieceIds,
            cache.DresserSetPresenceRowIds,
            cache.DresserCompleteSetRowIds,
            cache.ArmoireBaseIds,
            cache.DresserSlotsUsed);

        // Older saves and wiped set lists: recover presence from set rows stored as dresser items.
        if (this.snapshot.SetsInDresserCount == 0 && this.snapshot.DresserItemCount > 0)
            RefreshSetPresence();

        if (cache.LastSavedUtc != default)
            this.lastRefresh = cache.LastSavedUtc;
    }

    private void SavePersistedForCharacter(ulong contentId, bool dresserSpokeForWholeBox)
    {
        if (contentId == 0)
            return;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var saved))
        {
            saved = new CharacterGlamourCache();
            config.CharacterCaches[contentId] = saved;
        }

        // A read that saw only part of the dresser must never blank a list already on disk. The dresser
        // item list is the exception: a read that spoke for the whole box may legitimately empty it.
        var dresserIds = dresserSpokeForWholeBox
            ? this.snapshot.DresserItems.ToList()
            : KeepSavedWhenEmpty(
                "dresser",
                this.snapshot.DresserItems,
                saved.DresserBaseIds,
                ids => this.snapshot.MergeDresserItems(ids, replaceMissing: false));

        if (this.snapshot.DresserSlotsUsed <= 0 && saved.DresserSlotsUsed > 0)
            this.snapshot.SetDresserSlotsUsed(saved.DresserSlotsUsed);

        var outfitPieceIds = KeepSavedWhenEmpty(
            "outfit-piece",
            this.snapshot.DresserOutfitPieces,
            saved.DresserOutfitPieceIds,
            ids => this.snapshot.MergeDresserOutfitPieces(ids, replaceMissing: false));

        var setIds = KeepSavedWhenEmpty(
            "set-presence",
            this.snapshot.SetsInDresser,
            saved.DresserSetPresenceRowIds,
            ids => this.snapshot.ReplaceSetsInDresser(ids));

        var completeSetIds = KeepSavedWhenEmpty(
            "complete-set",
            this.snapshot.CompleteSetsInDresser,
            saved.DresserCompleteSetRowIds,
            ids => this.snapshot.ReplaceCompleteSetsInDresser(ids));

        var armoireIds = this.snapshot.ArmoireItems.ToList();

        if (Same(saved.DresserBaseIds, dresserIds)
            && Same(saved.DresserOutfitPieceIds, outfitPieceIds)
            && Same(saved.ArmoireBaseIds, armoireIds)
            && Same(saved.DresserSetPresenceRowIds, setIds)
            && Same(saved.DresserCompleteSetRowIds, completeSetIds)
            && saved.DresserSlotsUsed == this.snapshot.DresserSlotsUsed)
        {
            saved.LastSavedUtc = DateTime.UtcNow;
            return;
        }

        // Merged in place so the Fashion Report fields and saved plates survive.
        saved.DresserBaseIds = dresserIds;
        saved.DresserOutfitPieceIds = outfitPieceIds;
        saved.DresserSetPresenceRowIds = setIds;
        saved.DresserCompleteSetRowIds = completeSetIds;
        saved.ArmoireBaseIds = armoireIds;
        saved.DresserSlotsUsed = this.snapshot.DresserSlotsUsed;
        saved.LastSavedUtc = DateTime.UtcNow;
        config.Save();
    }

    private static List<uint> KeepSavedWhenEmpty(
        string what,
        IReadOnlyCollection<uint> fresh,
        List<uint> saved,
        Action<HashSet<uint>> putBack)
    {
        if (fresh.Count > 0 || saved.Count == 0)
            return fresh.ToList();

        PluginFileLog.Warn("ownership.save", $"Skipped empty {what} overwrite ({saved.Count} kept)");
        putBack([.. saved]);
        return [.. saved];
    }

    private static bool Same(List<uint> saved, List<uint> current)
    {
        if (saved.Count != current.Count)
            return false;

        var remaining = new HashSet<uint>(saved);
        foreach (var id in current)
        {
            if (!remaining.Remove(id))
                return false;
        }

        return remaining.Count == 0;
    }
}
