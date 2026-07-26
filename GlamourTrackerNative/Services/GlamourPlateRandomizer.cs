using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourTracker.Services;

/// <summary>
/// Randomizes the open glamour plate. SetSelectedItemData often fails to stick in one frame
/// (same pattern Glamaholic uses), so application is verified and retried across framework ticks.
/// </summary>
internal sealed class GlamourPlateRandomizer
{
    private const int MaxRetryPasses = 10;

    private readonly GlamourCandidatePool candidatePool;
    private readonly Func<Configuration> getConfiguration;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly Random random = new();

    private List<PendingPlateSlot>? pending;
    private int plannedCount;
    private int skippedCount;
    private int retryPass;
    private uint restoreSelectedIndex;
    private Action<PlateRandomizeResult>? onComplete;

    public GlamourPlateRandomizer(
        GlamourCandidatePool candidatePool,
        Func<Configuration> getConfiguration,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.candidatePool = candidatePool;
        this.getConfiguration = getConfiguration;
        this.objectTable = objectTable;
        this.log = log;
    }

    public bool IsBusy => this.pending != null;

    public unsafe bool IsPlateEditorOpen()
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        return agent != null && agent->Data != null && agent->IsAgentActive();
    }

    /// <summary>Starts randomization. May finish immediately or continue via <see cref="Tick"/>.</summary>
    public unsafe PlateRandomizeResult BeginRandomize(Action<PlateRandomizeResult>? onComplete = null) =>
        BeginRandomizeInternal(slotFilter: null, onComplete);

    /// <summary>Rerolls a single plate slot (0–11). Ignores slot locks.</summary>
    public unsafe PlateRandomizeResult BeginRandomizeSlot(int slot, Action<PlateRandomizeResult>? onComplete = null)
    {
        if (!GlamourPlateSlotMap.IsValidIndex(slot))
            return PlateRandomizeResult.Fail("Invalid equipment slot.");

        return BeginRandomizeInternal(slot, onComplete);
    }

    private unsafe PlateRandomizeResult BeginRandomizeInternal(int? slotFilter, Action<PlateRandomizeResult>? onComplete)
    {
        if (this.pending != null)
            return PlateRandomizeResult.Fail("Already randomizing — wait a moment.");

        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null || !agent->IsAgentActive())
        {
            return PlateRandomizeResult.Fail(
                "Open the glamour plate editor at a dresser first (Edit Glamour Plates).");
        }

        var config = this.getConfiguration();
        EnsureLockArray(config);

        if (!TryBuildFilteredPool(config, out var all, out var beforeFilter, out var jobId, out var fail))
            return fail;

        var usedPrismSlots = CollectUsedPrismSources(agent, excludeSlot: slotFilter);
        var plan = new List<PendingPlateSlot>();
        var skippedLocked = 0;
        var skippedEmptyPool = 0;
        var locks = config.RandomizeLockedSlots;

        for (var slot = 0; slot < GlamourPlateSlotMap.SlotCount; slot++)
        {
            if (slotFilter is int only && slot != only)
                continue;

            // Full-plate run respects locks; single-slot reroll does not.
            if (slotFilter == null && locks.Length > slot && locks[slot])
            {
                skippedLocked++;
                continue;
            }

            var plateSlot = (GlamourPlateSlot)slot;
            var pool = this.candidatePool.FilterForPlateSlot(all, plateSlot, usedPrismSlots);
            if (pool.Count == 0)
            {
                skippedEmptyPool++;
                this.log.Debug($"Plate randomizer: no candidates for {GlamourPlateSlotMap.Label(slot)}.");
                continue;
            }

            if (slotFilter != null)
                pool = PreferDifferentItem(pool, agent->Data->CurrentItems[slot].ItemId);

            var pick = pool[this.random.Next(pool.Count)];
            if (pick.Source == AgentMiragePrismMiragePlateData.ItemSource.PrismBox)
                usedPrismSlots.Add((pick.Source, pick.SourceId));

            plan.Add(new PendingPlateSlot(slot, pick));
        }

        if (plan.Count == 0)
        {
            if (slotFilter != null)
            {
                return PlateRandomizeResult.Fail(
                    $"No valid pieces for {GlamourPlateSlotMap.Label(slotFilter.Value)} with the current filters.");
            }

            return PlateRandomizeResult.Fail(
                skippedLocked == GlamourPlateSlotMap.SlotCount
                    ? "All slots are locked."
                    : "Could not plan any slots with the current filters. Unlock slots or widen filters.");
        }

        this.restoreSelectedIndex = agent->Data->SelectedItemIndex;
        this.plannedCount = plan.Count;
        this.skippedCount = skippedLocked + skippedEmptyPool;
        this.pending = plan;
        this.retryPass = 0;
        this.onComplete = onComplete;

        var planned = this.plannedCount;
        var skipped = this.skippedCount;
        var scope = slotFilter == null
            ? $"{planned} slots"
            : GlamourPlateSlotMap.Label(slotFilter.Value);
        this.log.Information(
            $"Plate randomizer: planned {scope} from {all.Count}/{beforeFilter} items " +
            $"(locked {skippedLocked}, no pool {skippedEmptyPool}, job {jobId}).");

        ApplyAndVerify(agent);

        if (this.pending == null)
        {
            return slotFilter == null
                ? PlateRandomizeResult.Ok(planned, skipped, PlateIndex(agent))
                : PlateRandomizeResult.OkSlot(slotFilter.Value, PlateIndex(agent));
        }

        return slotFilter == null
            ? PlateRandomizeResult.Started(planned, PlateIndex(agent))
            : PlateRandomizeResult.StartedSlot(slotFilter.Value, PlateIndex(agent));
    }

    private bool TryBuildFilteredPool(
        Configuration config,
        out List<GlamourCandidate> all,
        out int beforeFilter,
        out uint jobId,
        out PlateRandomizeResult fail)
    {
        all = this.candidatePool.BuildLiveCandidates(
            config.RandomizeIncludeDresser,
            config.RandomizeIncludeArmoire);
        beforeFilter = all.Count;
        jobId = 0;
        fail = default;

        if (all.Count == 0)
        {
            fail = PlateRandomizeResult.Fail(
                "No dresser/armoire items available. Open the dresser once while the plate editor is open, then try again.");
            return false;
        }

        jobId = ResolveFilterJobId(config);
        if (config.RandomizeJobFilter != RandomizeJobFilterMode.Any && jobId == 0)
        {
            fail = PlateRandomizeResult.Fail(
                config.RandomizeJobFilter == RandomizeJobFilterMode.CurrentJob
                    ? "Could not read your current job — log in and try again."
                    : "Choose a job in the Randomize filters, or set job restriction to Any job.");
            return false;
        }

        TryGetPlayerRaceSex(out var raceId, out var isFemale);
        all = this.candidatePool.ApplyConfigFilters(all, config, jobId, raceId, isFemale);
        if (all.Count == 0)
        {
            fail = PlateRandomizeResult.Fail(
                $"No items left after filters ({beforeFilter} before filtering). Widen job or level limits.");
            return false;
        }

        return true;
    }

    private static unsafe HashSet<(AgentMiragePrismMiragePlateData.ItemSource, uint)> CollectUsedPrismSources(
        AgentMiragePrismMiragePlate* agent,
        int? excludeSlot)
    {
        var used = new HashSet<(AgentMiragePrismMiragePlateData.ItemSource, uint)>();
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            if (excludeSlot is int skip && i == skip)
                continue;

            ref var item = ref agent->Data->CurrentItems[i];
            if (item.ItemId == 0)
                continue;

            if (item.Source == AgentMiragePrismMiragePlateData.ItemSource.PrismBox)
                used.Add((item.Source, item.SourceId));
        }

        return used;
    }

    private static IReadOnlyList<GlamourCandidate> PreferDifferentItem(
        IReadOnlyList<GlamourCandidate> pool,
        uint currentItemId)
    {
        if (currentItemId == 0 || pool.Count <= 1)
            return pool;

        var alternatives = new List<GlamourCandidate>(pool.Count);
        foreach (var candidate in pool)
        {
            if (!ItemIdsMatch(candidate.ItemId, currentItemId))
                alternatives.Add(candidate);
        }

        return alternatives.Count > 0 ? alternatives : pool;
    }

    /// <summary>Call from Framework.Update while <see cref="IsBusy"/>.</summary>
    public unsafe void Tick()
    {
        if (this.pending == null)
            return;

        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null || !agent->IsAgentActive())
        {
            Finish(PlateRandomizeResult.Fail("Plate editor closed during randomization."));
            return;
        }

        this.retryPass++;
        ApplyAndVerify(agent);

        if (this.pending == null)
            return;

        if (this.retryPass < MaxRetryPasses)
            return;

        var remaining = this.pending.Count;
        var applied = Math.Max(0, this.plannedCount - remaining);
        agent->Data->SelectedItemIndex = this.restoreSelectedIndex;
        agent->Data->HasChanges = true;
        Finish(PlateRandomizeResult.Partial(applied, remaining, PlateIndex(agent)));
    }

    private unsafe void ApplyAndVerify(AgentMiragePrismMiragePlate* agent)
    {
        if (this.pending == null)
            return;

        var stillPending = new List<PendingPlateSlot>();

        foreach (var entry in this.pending)
        {
            agent->Data->SelectedItemIndex = (uint)entry.Slot;
            agent->SetSelectedItemData(
                entry.Pick.Source,
                entry.Pick.SourceId,
                entry.Pick.ItemId,
                entry.Pick.Stain0Id,
                entry.Pick.Stain1Id);

            ref var current = ref agent->Data->CurrentItems[entry.Slot];
            if (!ItemIdsMatch(current.ItemId, entry.Pick.ItemId))
                stillPending.Add(entry);
        }

        agent->Data->HasChanges = true;

        if (stillPending.Count > 0)
        {
            this.pending = stillPending;
            this.log.Debug(
                $"Plate randomizer: {stillPending.Count}/{this.plannedCount} slots pending after pass {this.retryPass}.");
            return;
        }

        agent->Data->SelectedItemIndex = this.restoreSelectedIndex;
        Finish(PlateRandomizeResult.Ok(this.plannedCount, this.skippedCount, PlateIndex(agent)));
    }

    private void Finish(PlateRandomizeResult result)
    {
        var cb = this.onComplete;
        this.pending = null;
        this.retryPass = 0;
        this.plannedCount = 0;
        this.skippedCount = 0;
        this.onComplete = null;

        if (!result.InProgress)
            this.log.Information($"Plate randomizer done: {result.Message}");

        cb?.Invoke(result);
    }

    private static unsafe int PlateIndex(AgentMiragePrismMiragePlate* agent) =>
        (int)agent->Data->SelectedMiragePlateIndex + 1;

    private static bool ItemIdsMatch(uint appliedId, uint wantedId)
    {
        if (appliedId == 0)
            return false;

        if (appliedId == wantedId)
            return true;

        return ItemIdHelper.GlamourBaseId(appliedId) == ItemIdHelper.GlamourBaseId(wantedId);
    }

    public static void EnsureLockArray(Configuration config)
    {
        if (config.RandomizeLockedSlots is { Length: GlamourPlateSlotMap.SlotCount })
            return;

        var locks = new bool[GlamourPlateSlotMap.SlotCount];
        if (config.RandomizeLockedSlots != null)
        {
            var copy = Math.Min(config.RandomizeLockedSlots.Length, locks.Length);
            Array.Copy(config.RandomizeLockedSlots, locks, copy);
        }

        config.RandomizeLockedSlots = locks;
    }

    private uint ResolveFilterJobId(Configuration config) =>
        config.RandomizeJobFilter switch
        {
            RandomizeJobFilterMode.CurrentJob => this.objectTable.LocalPlayer?.ClassJob.RowId ?? 0,
            RandomizeJobFilterMode.SpecificJob => config.RandomizeSpecificJobId,
            _ => 0,
        };

    private void TryGetPlayerRaceSex(out uint raceId, out bool? isFemale)
    {
        raceId = 0;
        isFemale = null;

        var player = this.objectTable.LocalPlayer;
        if (player == null)
            return;

        var customize = player.CustomizeData;
        raceId = customize.Race;
        // Game: 0 = male, 1 = female (CustomizeData.Sex is a byte).
        isFemale = customize.Sex == 1;
    }

    private readonly record struct PendingPlateSlot(int Slot, GlamourCandidate Pick);
}

internal readonly record struct PlateRandomizeResult(
    bool Success,
    bool InProgress,
    string Message,
    int Filled,
    int Skipped,
    int PlateIndex)
{
    public static PlateRandomizeResult Started(int planned, int plateIndex) =>
        new(
            true,
            true,
            $"Randomizing plate {plateIndex} ({planned} slots)…",
            planned,
            0,
            plateIndex);

    public static PlateRandomizeResult StartedSlot(int slot, int plateIndex) =>
        new(
            true,
            true,
            $"Rerolling {GlamourPlateSlotMap.Label(slot)} on plate {plateIndex}…",
            1,
            0,
            plateIndex);

    public static PlateRandomizeResult Ok(int filled, int skipped, int plateIndex) =>
        new(
            true,
            false,
            $"Randomized plate {plateIndex}: {filled} slots applied" +
            (skipped > 0 ? $" ({skipped} locked or empty pool)." : "."),
            filled,
            skipped,
            plateIndex);

    public static PlateRandomizeResult OkSlot(int slot, int plateIndex) =>
        new(
            true,
            false,
            $"Rerolled {GlamourPlateSlotMap.Label(slot)} on plate {plateIndex}.",
            1,
            0,
            plateIndex);

    public static PlateRandomizeResult Partial(int filled, int remaining, int plateIndex) =>
        new(
            true,
            false,
            $"Plate {plateIndex}: applied {filled} slots, {remaining} did not stick — click Randomize again.",
            filled,
            remaining,
            plateIndex);

    public static PlateRandomizeResult Fail(string message) =>
        new(false, false, message, 0, 0, 0);
}
