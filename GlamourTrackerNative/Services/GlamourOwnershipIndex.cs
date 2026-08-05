using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

internal sealed class GlamourOwnershipIndex
{
    private const int MaxDresserSlots = 800;

    private static readonly (int SlotIndex, Func<MirageStoreSetItem, uint> ItemId)[] SetSlotReaders =
    [
        (0, s => s.MainHand.RowId),
        (1, s => s.OffHand.RowId),
        (2, s => s.Head.RowId),
        (3, s => s.Body.RowId),
        (4, s => s.Hands.RowId),
        (5, s => s.Legs.RowId),
        (6, s => s.Feet.RowId),
        (7, s => s.Earrings.RowId),
        (8, s => s.Necklace.RowId),
        (9, s => s.Bracelets.RowId),
        (10, s => s.Ring.RowId),
    ];

    private readonly CabinetCatalog cabinetCatalog;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;
    private readonly IClientState clientState;
    private readonly Func<ulong> getContentId;

    /// <summary>Only physical glamour dresser slots — not outfit unlock flags.</summary>
    private readonly HashSet<uint> cachedDresserBaseIds = [];
    /// <summary>Sets with at least one Mirage slot unlocked (not inferred from piece ItemId == set RowId).</summary>
    private readonly HashSet<uint> cachedDresserSetRowIds = [];
    /// <summary>Sets with every glam piece slot unlocked via Mirage.</summary>
    private readonly HashSet<uint> cachedDresserCompleteSetRowIds = [];
    private readonly HashSet<uint> cachedArmoireBaseIds = [];

    private HashSet<uint>? mirageStoreSetRowIds;

    private int dresserSlotsUsed;
    private DateTime lastRefresh = DateTime.MinValue;
    private ulong activeContentId;
    private bool pendingContentIdLoad;

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

    public DateTime LastRefresh => this.lastRefresh;
    public int DresserUniqueCount => this.cachedDresserBaseIds.Count;
    public int DresserSlotsUsed => this.dresserSlotsUsed;
    public int ArmoireCount => this.cachedArmoireBaseIds.Count;
    public int OutfitSetsInDresser => this.cachedDresserSetRowIds.Count;
    public int OutfitSetsCompleteInDresser => this.cachedDresserCompleteSetRowIds.Count;
    public bool HasPersistedData => this.cachedDresserBaseIds.Count > 0 || this.cachedArmoireBaseIds.Count > 0;

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
        SavePersistedForCharacter(this.activeContentId, dresserAuthoritative: false);
        this.pendingContentIdLoad = false;
    }

    public void ClearRuntimeCache()
    {
        this.cachedDresserBaseIds.Clear();
        this.cachedDresserSetRowIds.Clear();
        this.cachedDresserCompleteSetRowIds.Clear();
        this.cachedArmoireBaseIds.Clear();
        this.dresserSlotsUsed = 0;
        this.lastRefresh = DateTime.MinValue;
    }

    public void Refresh(bool force = false)
    {
        if (!this.clientState.IsLoggedIn)
            return;

        if (this.pendingContentIdLoad && TryFinishPendingLoginLoad())
        {
            // Loaded cache; still try live merge below.
        }

        var contentId = this.getContentId();
        if (contentId == 0)
            return;

        if (this.activeContentId != contentId)
            LoadPersistedForCharacter(contentId);

        if (!force && (DateTime.UtcNow - this.lastRefresh).TotalSeconds < 5)
            return;

        try
        {
            var liveDresser = new HashSet<uint>();
            var liveArmoire = new HashSet<uint>();
            var slotsUsed = 0;
            var dresserAuthoritative = false;
            var armoireRead = false;

            var dresserRead = ReadDresserItems(liveDresser, ref slotsUsed, out dresserAuthoritative);
            if (ReadArmoire(liveArmoire))
                armoireRead = true;

            if (!dresserRead && !armoireRead)
                return;

            var dresserChanged = false;
            if (dresserRead)
            {
                // Finder lists can be partial at login — only Prism Box / agent may replace.
                dresserChanged = MergeLiveDresser(
                    liveDresser,
                    replaceMissing: dresserAuthoritative && liveDresser.Count > 0);
                if (slotsUsed > 0 && slotsUsed != this.dresserSlotsUsed)
                {
                    this.dresserSlotsUsed = slotsUsed;
                    dresserChanged = true;
                }
                else if (slotsUsed > 0)
                {
                    this.dresserSlotsUsed = slotsUsed;
                }

            }

            // Sets appear in the dresser item list as MirageStoreSetItem.RowId entries.
            // That is "on the set list" (presence), NOT "every piece owned" (complete).
            if (this.cachedDresserBaseIds.Count > 0 && RebuildSetPresenceFromDresserItems())
                dresserChanged = true;

            // Optional: add any unlock-bits the finder knows about (never wipe item-derived presence).
            try
            {
                if (AddSetPresenceFromFinderUnlockBits())
                    dresserChanged = true;
            }
            catch (Exception ex)
            {
                PluginFileLog.Error("ownership.finder-sets", "Sync set unlock bits failed", ex);
            }

            // Complete sets: Mirage slot unlocks when Prism Box is loaded, plus all-pieces fallback.
            try
            {
                if (RebuildDresserCompleteSetRowIdsFromMirage())
                    dresserChanged = true;
            }
            catch (Exception ex)
            {
                PluginFileLog.Error("ownership.mirage-sets", "Rebuild complete set unlocks failed", ex);
            }

            if (RebuildCompleteSetsFromOwnedPieces())
                dresserChanged = true;

            // If live scans found nothing this tick, keep any completes already on disk.
            if (this.cachedDresserCompleteSetRowIds.Count == 0
                && TryHydrateCompleteSetsFromConfig(contentId))
            {
                dresserChanged = true;
            }

            // Cabinet.IsCabinetLoaded() means the full armoire bitfield is available — safe to drop removed items.
            var armoireChanged = armoireRead && MergeLiveArmoire(liveArmoire, replaceMissing: true);
            this.lastRefresh = DateTime.UtcNow;

            if (dresserChanged || armoireChanged)
            {
                SavePersistedForCharacter(contentId, dresserAuthoritative);
                PluginFileLog.Info(
                    "ownership.refresh",
                    $"dresser={this.cachedDresserBaseIds.Count} slots={this.dresserSlotsUsed} " +
                    $"sets={this.cachedDresserSetRowIds.Count} completeSets={this.cachedDresserCompleteSetRowIds.Count} " +
                    $"armoire={this.cachedArmoireBaseIds.Count} auth={dresserAuthoritative}");
            }
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("ownership.refresh", "Dresser/armoire refresh failed", ex);
        }
    }

    /// <summary>
    /// Merge live dresser ids into cache. When <paramref name="replaceMissing"/> is true and live
    /// data is non-empty, drop cached ids that are no longer present (true dresser resync).
    /// Empty live never clears a non-empty cache.
    /// </summary>
    private bool MergeLiveDresser(HashSet<uint> liveDresser, bool replaceMissing)
    {
        var changed = false;

        if (replaceMissing && liveDresser.Count > 0)
        {
            var remove = this.cachedDresserBaseIds.Where(id => !liveDresser.Contains(id)).ToList();
            foreach (var id in remove)
            {
                this.cachedDresserBaseIds.Remove(id);
                changed = true;
            }
        }

        foreach (var id in liveDresser)
        {
            if (!this.cachedDresserBaseIds.Add(id))
                continue;

            changed = true;
        }

        return changed;
    }

    private bool MergeLiveArmoire(HashSet<uint> liveArmoire, bool replaceMissing)
    {
        var changed = false;

        if (replaceMissing)
        {
            var remove = this.cachedArmoireBaseIds.Where(id => !liveArmoire.Contains(id)).ToList();
            foreach (var id in remove)
            {
                this.cachedArmoireBaseIds.Remove(id);
                changed = true;
            }
        }

        foreach (var id in liveArmoire)
        {
            if (this.cachedArmoireBaseIds.Add(id))
                changed = true;
        }

        return changed;
    }

    public GlamourStorageLocation GetStorage(uint itemId)
    {
        var location = GlamourStorageLocation.None;
        var baseId = ItemIdHelper.GlamourBaseId(itemId);

        if (this.cachedDresserBaseIds.Contains(baseId))
            location |= GlamourStorageLocation.Dresser;

        if (this.cachedArmoireBaseIds.Contains(baseId))
            location |= GlamourStorageLocation.Armoire;

        return location;
    }

    public bool IsStored(uint itemId) => GetStorage(itemId) != GlamourStorageLocation.None;

    public bool IsInDresser(uint itemId) =>
        this.cachedDresserBaseIds.Contains(ItemIdHelper.GlamourBaseId(itemId));

    public bool IsInArmoire(uint itemId) =>
        this.cachedArmoireBaseIds.Contains(ItemIdHelper.GlamourBaseId(itemId));

    /// <summary>True when the set is on the dresser set list (persisted unlock bits / Mirage presence).</summary>
    public bool IsOutfitSetInDresser(uint setRowId) =>
        this.cachedDresserSetRowIds.Contains(setRowId);

    /// <summary>True when Mirage reports every glam piece slot unlocked for this outfit set.</summary>
    public bool IsOutfitSetCompleteInDresser(uint setRowId) =>
        this.cachedDresserCompleteSetRowIds.Contains(setRowId);

    /// <summary>Live ItemFinder unlock-bit check (same source as 0.1.102 IsUnlocked).</summary>
    public unsafe bool IsOutfitSetUnlockedLive(uint setRowId)
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return false;

        return IsFinderSetUnlockBitSet(finder, setRowId);
    }

    public unsafe bool IsMiragePrismReady()
    {
        var mirage = MirageManager.Instance();
        return mirage != null && mirage->PrismBoxLoaded;
    }

    /// <summary>
    /// <see cref="MirageManager.IsSetSlotUnlocked"/> takes a Prism Box slot index, not a set RowId.
    /// Finds the dresser index that holds this outfit set, then queries that slot.
    /// </summary>
    public unsafe bool IsOutfitSlotUnlocked(uint setRowId, int slotIndex)
    {
        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
            return false;

        var baseId = ItemIdHelper.GlamourBaseId(setRowId);
        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ItemIdHelper.GlamourBaseId(ids[i]) != baseId)
                continue;

            return mirage->IsSetSlotUnlocked((uint)i, slotIndex);
        }

        return false;
    }

    public static bool IsGlamourGear(Item item) =>
        item.EquipSlotCategory.RowId != 0 && item.ItemUICategory.RowId is not 59 and not 60;

    private void LoadPersistedForCharacter(ulong contentId)
    {
        this.activeContentId = contentId;
        ClearRuntimeCache();

        if (contentId == 0)
            return;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var cache))
            return;

        foreach (var id in cache.DresserBaseIds)
            this.cachedDresserBaseIds.Add(id);

        foreach (var id in cache.DresserSetRowIds)
            this.cachedDresserSetRowIds.Add(id);

        foreach (var id in cache.DresserCompleteSetRowIds)
            this.cachedDresserCompleteSetRowIds.Add(id);

        foreach (var id in cache.ArmoireBaseIds)
            this.cachedArmoireBaseIds.Add(id);

        if (cache.DresserSlotsUsed > 0)
            this.dresserSlotsUsed = cache.DresserSlotsUsed;

        // Older saves / wiped set lists: recover presence from set-row ids stored as dresser items.
        if (this.cachedDresserSetRowIds.Count == 0 && this.cachedDresserBaseIds.Count > 0)
            RebuildSetPresenceFromDresserItems();

        if (cache.LastSavedUtc != default)
            this.lastRefresh = cache.LastSavedUtc;
    }

    private void SavePersistedForCharacter(ulong contentId, bool dresserAuthoritative)
    {
        if (contentId == 0)
            return;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var existing))
        {
            existing = new CharacterGlamourCache();
            config.CharacterCaches[contentId] = existing;
        }

        var dresserSnapshot = this.cachedDresserBaseIds.ToArray();
        var armoireSnapshot = this.cachedArmoireBaseIds.ToArray();
        var setSnapshot = this.cachedDresserSetRowIds.ToArray();
        var completeSnapshot = this.cachedDresserCompleteSetRowIds.ToArray();

        // Never overwrite a populated dresser cache with empty unless we authoritatively saw a live dresser.
        if (dresserSnapshot.Length == 0
            && existing.DresserBaseIds.Count > 0
            && !dresserAuthoritative)
        {
            PluginFileLog.Warn(
                "ownership.save",
                $"Skipped empty dresser overwrite ({existing.DresserBaseIds.Count} saved ids kept)");
            dresserSnapshot = existing.DresserBaseIds.ToArray();
            foreach (var id in dresserSnapshot)
                this.cachedDresserBaseIds.Add(id);

            if (this.dresserSlotsUsed <= 0 && existing.DresserSlotsUsed > 0)
                this.dresserSlotsUsed = existing.DresserSlotsUsed;
        }

        // Never wipe a known complete-set list with empty (Mirage may be unavailable this tick).
        if (completeSnapshot.Length == 0 && existing.DresserCompleteSetRowIds.Count > 0)
        {
            PluginFileLog.Warn(
                "ownership.save",
                $"Skipped empty complete-set overwrite ({existing.DresserCompleteSetRowIds.Count} saved ids kept)");
            completeSnapshot = existing.DresserCompleteSetRowIds.ToArray();
            foreach (var id in completeSnapshot)
                this.cachedDresserCompleteSetRowIds.Add(id);
        }

        var dresserSame = SetsEqual(existing.DresserBaseIds, dresserSnapshot);
        var armoireSame = SetsEqual(existing.ArmoireBaseIds, armoireSnapshot);
        var setsSame = SetsEqual(existing.DresserSetRowIds, setSnapshot);
        var completeSame = SetsEqual(existing.DresserCompleteSetRowIds, completeSnapshot);
        var slotsSame = existing.DresserSlotsUsed == this.dresserSlotsUsed;

        if (dresserSame && armoireSame && setsSame && completeSame && slotsSame)
        {
            existing.LastSavedUtc = DateTime.UtcNow;
            return;
        }

        // Merge in place — keep Fashion Report fields / plates.
        existing.DresserBaseIds = dresserSnapshot.ToList();
        existing.DresserSetRowIds = setSnapshot.ToList();
        existing.DresserCompleteSetRowIds = completeSnapshot.ToList();
        existing.ArmoireBaseIds = armoireSnapshot.ToList();
        existing.DresserSlotsUsed = this.dresserSlotsUsed;
        existing.LastSavedUtc = DateTime.UtcNow;
        config.Save();
    }

    private static bool SetsEqual(List<uint> persisted, uint[] current)
    {
        if (persisted.Count != current.Length)
            return false;

        var set = new HashSet<uint>(persisted);
        foreach (var id in current)
        {
            if (!set.Remove(id))
                return false;
        }

        return set.Count == 0;
    }

    private static void AddItemId(HashSet<uint> target, uint itemId)
    {
        if (itemId == 0)
            return;

        target.Add(ItemIdHelper.GlamourBaseId(itemId));
    }

    private void EnsureMirageStoreSetRowIds()
    {
        this.mirageStoreSetRowIds ??= this.dataManager.GetExcelSheet<MirageStoreSetItem>()
            .Where(row => row.RowId != 0)
            .Select(row => row.RowId)
            .ToHashSet();
    }

    /// <summary>
    /// Rebuild which outfit sets are on the dresser list from physical dresser item ids.
    /// The game stores owned sets as <see cref="MirageStoreSetItem.RowId"/> entries in the Prism Box.
    /// </summary>
    private bool RebuildSetPresenceFromDresserItems()
    {
        EnsureMirageStoreSetRowIds();
        var next = new HashSet<uint>();
        foreach (var id in this.cachedDresserBaseIds)
        {
            if (this.mirageStoreSetRowIds!.Contains(id))
                next.Add(id);
        }

        if (next.SetEquals(this.cachedDresserSetRowIds))
            return false;

        this.cachedDresserSetRowIds.Clear();
        foreach (var id in next)
            this.cachedDresserSetRowIds.Add(id);

        PluginFileLog.Info(
            "ownership.set-presence",
            $"Set-list presence from dresser items: {next.Count} sets (of {this.cachedDresserBaseIds.Count} items)");
        return true;
    }

    /// <summary>
    /// Add set-list presence from ItemFinder unlock bits. Add-only — never clears item-derived rows.
    /// </summary>
    private unsafe bool AddSetPresenceFromFinderUnlockBits()
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return false;

        EnsureMirageStoreSetRowIds();
        var changed = false;
        foreach (var setId in this.mirageStoreSetRowIds!)
        {
            if (!IsFinderSetUnlockBitSet(finder, setId))
                continue;

            if (this.cachedDresserSetRowIds.Add(setId))
                changed = true;
        }

        return changed;
    }

    private static unsafe bool IsFinderSetUnlockBitSet(ItemFinderModule* finder, uint setId)
    {
        var bitIndex = (int)setId;
        var bits = finder->GlamourDresserItemSetUnlockBits;
        var wordIndex = bitIndex / 16;
        if (wordIndex < 0 || wordIndex >= bits.Length)
            return false;

        return (bits[wordIndex] & (1 << (bitIndex % 16))) != 0;
    }

    /// <summary>
    /// Rebuild which outfit sets are fully unlocked via Mirage slot flags.
    /// Does not touch set-list presence. Empty Mirage scans never wipe a good cache.
    /// </summary>
    private unsafe bool RebuildDresserCompleteSetRowIdsFromMirage()
    {
        var mirage = MirageManager.Instance();
        // Slot unlock bits are only trustworthy while the Prism Box is loaded (dresser open / just opened).
        if (mirage == null || !mirage->PrismBoxLoaded)
            return false;

        EnsureMirageStoreSetRowIds();
        var setSheet = this.dataManager.GetExcelSheet<MirageStoreSetItem>();
        var nextComplete = new HashSet<uint>();
        var setEntries = 0;

        // IsSetSlotUnlocked(itemIndex, slot) — itemIndex is the PrismBoxItemIds index, NOT the set RowId.
        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            var setId = ItemIdHelper.GlamourBaseId(ids[i]);
            if (setId == 0 || !this.mirageStoreSetRowIds!.Contains(setId))
                continue;

            if (!setSheet.TryGetRow(setId, out var row))
                continue;

            setEntries++;
            var slots = 0;
            var unlocked = 0;
            foreach (var (slotIndex, reader) in SetSlotReaders)
            {
                if (reader(row) == 0)
                    continue;

                slots++;
                if (mirage->IsSetSlotUnlocked((uint)i, slotIndex))
                    unlocked++;
            }

            if (slots > 0 && unlocked == slots)
                nextComplete.Add(setId);
        }

        // Empty Mirage result must not wipe a previously good complete list.
        if (nextComplete.Count == 0)
        {
            PluginFileLog.Info(
                "ownership.mirage-sets",
                $"Complete scan found 0 (setEntries={setEntries}, prismLen={ids.Length}) — keeping cache={this.cachedDresserCompleteSetRowIds.Count}");
            return false;
        }

        if (nextComplete.SetEquals(this.cachedDresserCompleteSetRowIds))
            return false;

        this.cachedDresserCompleteSetRowIds.Clear();
        foreach (var id in nextComplete)
            this.cachedDresserCompleteSetRowIds.Add(id);

        PluginFileLog.Info(
            "ownership.mirage-sets",
            $"Complete sets from Mirage slots: {nextComplete.Count} (setEntries={setEntries})");
        return true;
    }

    private bool TryHydrateCompleteSetsFromConfig(ulong contentId)
    {
        if (contentId == 0 || this.cachedDresserCompleteSetRowIds.Count > 0)
            return false;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var cache)
            || cache.DresserCompleteSetRowIds.Count == 0)
            return false;

        foreach (var id in cache.DresserCompleteSetRowIds)
            this.cachedDresserCompleteSetRowIds.Add(id);

        PluginFileLog.Info(
            "ownership.hydrate",
            $"Restored {this.cachedDresserCompleteSetRowIds.Count} complete sets from saved data");
        return true;
    }

    /// <summary>
    /// Mark sets complete when every glam piece item id is present in the dresser item list.
    /// Merges into the complete cache (does not remove Mirage-detected completes).
    /// </summary>
    private bool RebuildCompleteSetsFromOwnedPieces()
    {
        if (this.cachedDresserSetRowIds.Count == 0 || this.cachedDresserBaseIds.Count == 0)
            return false;

        var setSheet = this.dataManager.GetExcelSheet<MirageStoreSetItem>();
        var itemSheet = this.dataManager.GetExcelSheet<Item>();
        var changed = false;

        foreach (var setId in this.cachedDresserSetRowIds)
        {
            if (this.cachedDresserCompleteSetRowIds.Contains(setId))
                continue;

            if (!setSheet.TryGetRow(setId, out var row))
                continue;

            var glamIds = new List<uint>();
            foreach (var (_, reader) in SetSlotReaders)
            {
                var itemId = reader(row);
                if (itemId == 0 || !itemSheet.TryGetRow(itemId, out var item) || !IsGlamourGear(item))
                    continue;

                glamIds.Add(ItemIdHelper.GlamourBaseId(itemId));
            }

            if (glamIds.Count == 0 || glamIds.Any(id => !this.cachedDresserBaseIds.Contains(id)))
                continue;

            if (this.cachedDresserCompleteSetRowIds.Add(setId))
                changed = true;
        }

        return changed;
    }

    private unsafe bool ReadDresserItems(HashSet<uint> dresser, ref int slotsUsed, out bool authoritative)
    {
        authoritative = false;

        // ItemFinder can be "cached" at login with an incomplete list. Use it to ADD ids only —
        // never as a replace source (that wiped saved piece ids and broke "completed in dresser").
        var finder = ItemFinderModule.Instance();
        if (finder != null && finder->IsGlamourDresserCached)
        {
            foreach (var id in finder->GlamourDresserItemIds)
                AddItemId(dresser, id);
        }

        var mirage = MirageManager.Instance();
        if (mirage != null && mirage->PrismBoxLoaded)
        {
            var filled = 0;
            foreach (var id in mirage->PrismBoxItemIds)
            {
                if (id == 0)
                    continue;

                filled++;
                AddItemId(dresser, id);
            }

            if (slotsUsed == 0)
                slotsUsed = filled;

            if (filled > 0)
                authoritative = true;
        }

        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent != null && agent->Data != null)
        {
            var data = agent->Data;
            slotsUsed = data->UsedSlots;

            foreach (ref var entry in data->PrismBoxItems)
            {
                if (entry.ItemId == 0 || entry.Slot >= MaxDresserSlots)
                    continue;

                AddItemId(dresser, entry.ItemId);
            }

            // Empty agent Data (dresser still loading) must not be treated as authoritative.
            if (slotsUsed > 0 || dresser.Count > 0)
                authoritative = true;
        }

        return dresser.Count > 0 || authoritative;
    }

    private unsafe bool ReadArmoire(HashSet<uint> armoire)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return false;

        var cabinet = uiState->Cabinet;
        if (!cabinet.IsCabinetLoaded())
            return false;

        foreach (var (cabinetRow, itemId) in this.cabinetCatalog.CabinetToItem)
        {
            if (cabinet.IsItemInCabinet(cabinetRow))
                AddItemId(armoire, itemId);
        }

        return armoire.Count > 0;
    }
}
