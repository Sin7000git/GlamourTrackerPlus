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

    private readonly CabinetCatalog cabinetCatalog;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;
    private readonly IClientState clientState;
    private readonly Func<ulong> getContentId;

    /// <summary>Only physical glamour dresser slots — not outfit unlock flags.</summary>
    private readonly HashSet<uint> cachedDresserBaseIds = [];
    private readonly HashSet<uint> cachedDresserSetRowIds = [];
    private readonly HashSet<uint> cachedArmoireBaseIds = [];

    private HashSet<uint>? mirageStoreSetRowIds;

    private int dresserSlotsUsed;
    private DateTime lastRefresh = DateTime.MinValue;
    private ulong activeContentId;

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

        if (this.clientState.IsLoggedIn)
            LoadPersistedForCharacter(this.getContentId());
    }

    public DateTime LastRefresh => this.lastRefresh;
    public int DresserUniqueCount => this.cachedDresserBaseIds.Count;
    public int DresserSlotsUsed => this.dresserSlotsUsed;
    public int ArmoireCount => this.cachedArmoireBaseIds.Count;
    public int OutfitSetsInDresser => this.cachedDresserSetRowIds.Count;
    public bool HasPersistedData => this.cachedDresserBaseIds.Count > 0 || this.cachedArmoireBaseIds.Count > 0;

    public void OnCharacterLogin(ulong contentId)
    {
        LoadPersistedForCharacter(contentId);
    }

    public void OnCharacterLogout()
    {
        SavePersistedForCharacter(this.activeContentId);
    }

    public void ClearRuntimeCache()
    {
        this.cachedDresserBaseIds.Clear();
        this.cachedDresserSetRowIds.Clear();
        this.cachedArmoireBaseIds.Clear();
        this.dresserSlotsUsed = 0;
        this.lastRefresh = DateTime.MinValue;
    }

    public void Refresh(bool force = false)
    {
        if (!this.clientState.IsLoggedIn)
            return;

        if (!force && (DateTime.UtcNow - this.lastRefresh).TotalSeconds < 5)
            return;

        try
        {
            var liveDresser = new HashSet<uint>();
            var liveArmoire = new HashSet<uint>();
            var slotsUsed = 0;
            var liveDataRead = false;

            if (ReadDresserItems(liveDresser, ref slotsUsed))
                liveDataRead = true;

            if (ReadArmoire(liveArmoire))
                liveDataRead = true;

            if (!liveDataRead)
                return;

            var dresserChanged = MergeLiveDresser(liveDresser);
            var armoireChanged = MergeLiveArmoire(liveArmoire);
            var slotsChanged = slotsUsed > 0 && slotsUsed != this.dresserSlotsUsed;

            if (slotsUsed > 0)
                this.dresserSlotsUsed = slotsUsed;

            this.lastRefresh = DateTime.UtcNow;

            if (dresserChanged || armoireChanged || slotsChanged)
                SavePersistedForCharacter(this.getContentId());
        }
        catch (Exception)
        {
            // Client memory may be unavailable during load or zone transitions.
        }
    }

    private bool MergeLiveDresser(HashSet<uint> liveDresser)
    {
        var changed = false;
        foreach (var id in liveDresser)
        {
            if (!this.cachedDresserBaseIds.Add(id))
                continue;

            changed = true;
            if (IsMirageStoreSetRow(id))
                this.cachedDresserSetRowIds.Add(id);
        }

        return changed;
    }

    private bool MergeLiveArmoire(HashSet<uint> liveArmoire)
    {
        var changed = false;
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

    public bool IsOutfitSetInDresser(uint setRowId) =>
        this.cachedDresserSetRowIds.Contains(setRowId);

    public unsafe bool IsOutfitSlotUnlocked(uint setRowId, int slotIndex)
    {
        var mirage = MirageManager.Instance();
        if (mirage == null)
            return false;

        return mirage->IsSetSlotUnlocked(setRowId, slotIndex);
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
        {
            this.cachedDresserBaseIds.Add(id);
            if (IsMirageStoreSetRow(id))
                this.cachedDresserSetRowIds.Add(id);
        }

        foreach (var id in cache.ArmoireBaseIds)
            this.cachedArmoireBaseIds.Add(id);
    }

    private void SavePersistedForCharacter(ulong contentId)
    {
        if (contentId == 0)
            return;

        var config = this.getConfiguration();
        if (!config.CharacterCaches.TryGetValue(contentId, out var existing))
            existing = new CharacterGlamourCache();

        var dresserSnapshot = this.cachedDresserBaseIds.ToArray();
        var armoireSnapshot = this.cachedArmoireBaseIds.ToArray();
        if (SetsEqual(existing.DresserBaseIds, dresserSnapshot)
            && SetsEqual(existing.ArmoireBaseIds, armoireSnapshot))
            return;

        config.CharacterCaches[contentId] = new CharacterGlamourCache
        {
            DresserBaseIds = dresserSnapshot.ToList(),
            ArmoireBaseIds = armoireSnapshot.ToList(),
            GlamourPlates = existing.GlamourPlates,
            LastSavedUtc = DateTime.UtcNow,
        };
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

    private bool IsMirageStoreSetRow(uint rowId)
    {
        this.mirageStoreSetRowIds ??= this.dataManager.GetExcelSheet<MirageStoreSetItem>()
            .Where(row => row.RowId != 0)
            .Select(row => row.RowId)
            .ToHashSet();

        return this.mirageStoreSetRowIds.Contains(rowId);
    }

    private unsafe bool ReadDresserItems(HashSet<uint> dresser, ref int slotsUsed)
    {
        var readAny = false;

        var finder = ItemFinderModule.Instance();
        if (finder != null && finder->IsGlamourDresserCached)
        {
            foreach (var id in finder->GlamourDresserItemIds)
                AddItemId(dresser, id);

            readAny = dresser.Count > 0;
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

            readAny = true;
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

                // Stored outfit-set entries use the set row id; individual pieces are separate slots.
                AddItemId(dresser, entry.ItemId);
            }

            readAny = true;
        }

        return readAny;
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
