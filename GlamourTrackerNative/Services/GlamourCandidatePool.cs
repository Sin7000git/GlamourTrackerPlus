using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>Builds randomizer pools from live dresser (Prism Box) and armoire (Cabinet) data.</summary>
internal sealed class GlamourCandidatePool
{
    private const int MaxDresserSlots = 800;

    private readonly IDataManager dataManager;
    private readonly CabinetCatalog cabinetCatalog;

    public GlamourCandidatePool(IDataManager dataManager, CabinetCatalog cabinetCatalog)
    {
        this.dataManager = dataManager;
        this.cabinetCatalog = cabinetCatalog;
    }

    public unsafe List<GlamourCandidate> BuildLiveCandidates(bool includeDresser, bool includeArmoire)
    {
        var candidates = new List<GlamourCandidate>();
        var itemSheet = this.dataManager.GetExcelSheet<Item>();

        if (includeDresser)
            AppendDresserCandidates(candidates, itemSheet);

        if (includeArmoire)
            AppendArmoireCandidates(candidates, itemSheet);

        return candidates;
    }

    public IReadOnlyList<GlamourCandidate> FilterForPlateSlot(
        IReadOnlyList<GlamourCandidate> all,
        GlamourPlateSlot plateSlot,
        HashSet<(AgentMiragePrismMiragePlateData.ItemSource Source, uint SourceId)>? excludeSources = null)
    {
        var categorySheet = this.dataManager.GetExcelSheet<EquipSlotCategory>();
        var matches = new List<GlamourCandidate>();

        foreach (var candidate in all)
        {
            if (candidate.EquipSlotCategory == 0)
                continue;

            if (excludeSources != null
                && candidate.Source == AgentMiragePrismMiragePlateData.ItemSource.PrismBox
                && excludeSources.Contains((candidate.Source, candidate.SourceId)))
                continue;

            if (!categorySheet.TryGetRow(candidate.EquipSlotCategory, out var category))
                continue;

            if (GlamourPlateSlotMap.Fits(category, plateSlot))
                matches.Add(candidate);
        }

        return matches;
    }

    /// <summary>
    /// Applies job / level filters from config, and always drops race/sex-incompatible gear when
    /// <paramref name="raceId"/> is known (those pieces cannot be used on this character).
    /// </summary>
    public List<GlamourCandidate> ApplyConfigFilters(
        IReadOnlyList<GlamourCandidate> all,
        Configuration config,
        uint classJobId,
        uint raceId,
        bool? isFemale)
    {
        var needJob = config.RandomizeJobFilter != RandomizeJobFilterMode.Any && classJobId != 0;
        var needReq = config.RandomizeLimitRequiredLevel;
        var needIlvl = config.RandomizeLimitItemLevel;
        var needRace = raceId != 0 && isFemale.HasValue;
        if (!needJob && !needReq && !needIlvl && !needRace)
            return all as List<GlamourCandidate> ?? all.ToList();

        var itemSheet = this.dataManager.GetExcelSheet<Item>();
        var classJobs = this.dataManager.GetExcelSheet<ClassJob>();
        var filtered = new List<GlamourCandidate>(all.Count);

        foreach (var candidate in all)
        {
            var baseId = ItemIdHelper.GlamourBaseId(candidate.ItemId);
            if (!itemSheet.TryGetRow(baseId, out var item))
                continue;

            if (needRace && !ItemEquipFilter.MatchesRaceAndSex(item, raceId, isFemale!.Value))
                continue;

            if (needJob && !ItemEquipFilter.MatchesJob(item, classJobId, classJobs))
                continue;

            if (!ItemEquipFilter.MatchesRequiredLevel(
                    item,
                    needReq,
                    config.RandomizeMinRequiredLevel,
                    config.RandomizeMaxRequiredLevel))
                continue;

            if (!ItemEquipFilter.MatchesItemLevel(
                    item,
                    needIlvl,
                    config.RandomizeMinItemLevel,
                    config.RandomizeMaxItemLevel))
                continue;

            filtered.Add(candidate);
        }

        return filtered;
    }

    private unsafe void AppendDresserCandidates(List<GlamourCandidate> candidates, ExcelSheet<Item> itemSheet)
    {
        // Prefer Prism Box agent entries — SourceId must be PrismBoxItem.Slot (Glamaholic).
        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent != null && agent->Data != null)
        {
            var seenSlots = new HashSet<uint>();
            foreach (ref var entry in agent->Data->PrismBoxItems)
            {
                if (entry.ItemId == 0 || entry.Slot >= MaxDresserSlots)
                    continue;

                if (!seenSlots.Add(entry.Slot))
                    continue;

                if (!TryResolveEquipSlot(itemSheet, entry.ItemId, out var equipSlot))
                    continue;

                var s0 = entry.Stains.Length > 0 ? entry.Stains[0] : (byte)0;
                var s1 = entry.Stains.Length > 1 ? entry.Stains[1] : (byte)0;
                candidates.Add(new GlamourCandidate(
                    AgentMiragePrismMiragePlateData.ItemSource.PrismBox,
                    entry.Slot,
                    entry.ItemId,
                    s0,
                    s1,
                    equipSlot));
            }

            if (candidates.Count > 0)
                return;
        }

        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
            return;

        var ids = mirage->PrismBoxItemIds;
        var stain0 = mirage->PrismBoxStain0Ids;
        var stain1 = mirage->PrismBoxStain1Ids;
        var count = Math.Min(ids.Length, MaxDresserSlots);

        for (var i = 0; i < count; i++)
        {
            var itemId = ids[i];
            if (itemId == 0)
                continue;

            if (!TryResolveEquipSlot(itemSheet, itemId, out var equipSlot))
                continue;

            candidates.Add(new GlamourCandidate(
                AgentMiragePrismMiragePlateData.ItemSource.PrismBox,
                (uint)i,
                itemId,
                i < stain0.Length ? stain0[i] : (byte)0,
                i < stain1.Length ? stain1[i] : (byte)0,
                equipSlot));
        }
    }

    private unsafe void AppendArmoireCandidates(List<GlamourCandidate> candidates, ExcelSheet<Item> itemSheet)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return;

        var cabinet = uiState->Cabinet;
        if (!cabinet.IsCabinetLoaded())
            return;

        foreach (var (cabinetRow, itemId) in this.cabinetCatalog.CabinetToItem)
        {
            if (!cabinet.IsItemInCabinet(cabinetRow))
                continue;

            if (!TryResolveEquipSlot(itemSheet, itemId, out var equipSlot))
                continue;

            candidates.Add(new GlamourCandidate(
                AgentMiragePrismMiragePlateData.ItemSource.Cabinet,
                cabinetRow,
                itemId,
                0,
                0,
                equipSlot));
        }
    }

    private static bool TryResolveEquipSlot(ExcelSheet<Item> itemSheet, uint itemId, out uint equipSlotCategory)
    {
        equipSlotCategory = 0;
        var baseId = ItemIdHelper.GlamourBaseId(itemId);
        if (!itemSheet.TryGetRow(baseId, out var item))
            return false;

        if (!GlamourOwnershipIndex.IsGlamourGear(item))
            return false;

        equipSlotCategory = item.EquipSlotCategory.RowId;
        return equipSlotCategory != 0;
    }
}
