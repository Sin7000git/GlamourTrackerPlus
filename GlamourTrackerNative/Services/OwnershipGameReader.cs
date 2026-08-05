using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>What a single pass over the game's dresser structures managed to see.</summary>
/// <param name="FoundAnything">Any id or slot count came back.</param>
/// <param name="SpeaksForWholeDresser">
/// The read covered the dresser well enough that missing ids can be treated as removed.
/// </param>
/// <param name="ReadItemFinder">
/// The ItemFinder list was available. It is the only source that names the pieces inside a stored
/// outfit, so without it the picture is too thin to prune anything.
/// </param>
internal readonly record struct DresserRead(
    bool FoundAnything,
    bool SpeaksForWholeDresser,
    bool ReadItemFinder,
    int SlotsUsed);

/// <summary>What one walk of the stored outfits in the Prism Box found.</summary>
/// <param name="Evaluated">Outfits the box listed, and so the only ones this scan may speak for.</param>
/// <param name="Complete">Of those, the ones with every slot filled.</param>
/// <param name="UnlockedPieces">Item ids held inside those outfits, whole or partial.</param>
internal readonly record struct StoredOutfitScan(
    HashSet<uint> Evaluated,
    HashSet<uint> Complete,
    HashSet<uint> UnlockedPieces,
    int PrismBoxLength);

/// <summary>
/// Every read of live glamour state out of the game. Three structures answer overlapping questions
/// and each is trustworthy only at certain times, so the rules for that all live here.
/// </summary>
internal static unsafe class OwnershipGameReader
{
    private const int MaxDresserSlots = 800;

    public static DresserRead ReadDresser(HashSet<uint> into)
    {
        var speaksForWholeDresser = false;
        var readItemFinder = false;
        var slotsUsed = 0;

        // ItemFinder can report "cached" at login while still incomplete, so it only ever adds ids.
        var finder = ItemFinderModule.Instance();
        if (finder != null && finder->IsGlamourDresserCached)
        {
            readItemFinder = true;
            foreach (var id in finder->GlamourDresserItemIds)
                Add(into, id);
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
                Add(into, id);
            }

            if (slotsUsed == 0)
                slotsUsed = filled;

            if (filled > 0)
                speaksForWholeDresser = true;
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

                Add(into, entry.ItemId);
            }

            // Empty agent data means the dresser is still loading, which speaks for nothing.
            if (slotsUsed > 0 || into.Count > 0)
                speaksForWholeDresser = true;
        }

        return new DresserRead(
            FoundAnything: into.Count > 0 || speaksForWholeDresser,
            SpeaksForWholeDresser: speaksForWholeDresser,
            ReadItemFinder: readItemFinder,
            SlotsUsed: slotsUsed);
    }

    /// <summary>
    /// Reads the armoire. A loaded cabinet is the whole truth even when it is empty, so an empty
    /// result still counts as a successful read.
    /// </summary>
    public static bool ReadArmoire(CabinetCatalog catalog, HashSet<uint> into)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return false;

        var cabinet = uiState->Cabinet;
        if (!cabinet.IsCabinetLoaded())
            return false;

        foreach (var (cabinetRow, itemId) in catalog.CabinetToItem)
        {
            if (cabinet.IsItemInCabinet(cabinetRow))
                Add(into, itemId);
        }

        return true;
    }

    public static bool IsPrismBoxLoaded()
    {
        var mirage = MirageManager.Instance();
        return mirage != null && mirage->PrismBoxLoaded;
    }

    /// <summary>ItemFinder's per-set unlock bit — the same source the plugin used before 0.1.102.</summary>
    public static bool IsSetUnlockedInFinder(uint setRowId)
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return false;

        return HasSetUnlockBit(finder, setRowId);
    }

    /// <summary>Every outfit set ItemFinder currently reports as unlocked.</summary>
    public static List<uint> UnlockedSetsInFinder(IEnumerable<uint> candidateSetRowIds)
    {
        var unlocked = new List<uint>();
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return unlocked;

        foreach (var setRowId in candidateSetRowIds)
        {
            if (HasSetUnlockBit(finder, setRowId))
                unlocked.Add(setRowId);
        }

        return unlocked;
    }

    /// <summary>
    /// <see cref="MirageManager.IsSetSlotUnlocked"/> wants a Prism Box slot index rather than a set
    /// row id, so this finds the dresser slot holding the outfit first.
    /// </summary>
    public static bool IsSetSlotUnlocked(uint setRowId, int slotIndex)
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

    /// <summary>
    /// Walks the Prism Box and reports, for every stored outfit, which of its slots are actually
    /// filled and whether that adds up to the whole outfit.
    /// </summary>
    /// <remarks>
    /// Outfits can be stored partially, so the per-slot answer is the useful one: those pieces are in
    /// the dresser and can be worn even though they never appear in the item list, which holds one
    /// row for the outfit as a whole. Slot flags mean nothing unless the box is loaded, and outfits
    /// the box never listed stay unevaluated so a partial view cannot revoke an earlier one.
    /// </remarks>
    public static bool TryScanStoredOutfits(
        ExcelSheet<MirageStoreSetItem> setSheet,
        HashSet<uint> knownSetRowIds,
        out StoredOutfitScan scan)
    {
        scan = new StoredOutfitScan([], [], [], 0);

        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
            return false;

        var evaluated = new HashSet<uint>();
        var complete = new HashSet<uint>();
        var pieces = new HashSet<uint>();

        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            var setRowId = ItemIdHelper.GlamourBaseId(ids[i]);
            if (setRowId == 0 || !knownSetRowIds.Contains(setRowId))
                continue;

            if (!setSheet.TryGetRow(setRowId, out var row))
                continue;

            evaluated.Add(setRowId);
            var slots = 0;
            var unlocked = 0;
            foreach (var (_, slotIndex, readItemId) in OutfitSetSlots.All)
            {
                var itemId = readItemId(row);
                if (itemId == 0)
                    continue;

                slots++;
                if (!mirage->IsSetSlotUnlocked((uint)i, slotIndex))
                    continue;

                unlocked++;
                pieces.Add(ItemIdHelper.GlamourBaseId(itemId));
            }

            if (slots > 0 && unlocked == slots)
                complete.Add(setRowId);
        }

        scan = new StoredOutfitScan(evaluated, complete, pieces, ids.Length);
        return true;
    }

    private static bool HasSetUnlockBit(ItemFinderModule* finder, uint setRowId)
    {
        var bitIndex = (int)setRowId;
        var bits = finder->GlamourDresserItemSetUnlockBits;
        var wordIndex = bitIndex / 16;
        if (wordIndex < 0 || wordIndex >= bits.Length)
            return false;

        return (bits[wordIndex] & (1 << (bitIndex % 16))) != 0;
    }

    private static void Add(HashSet<uint> target, uint itemId)
    {
        if (itemId == 0)
            return;

        target.Add(ItemIdHelper.GlamourBaseId(itemId));
    }
}
