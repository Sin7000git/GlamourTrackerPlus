using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace GlamourTracker.Services.FashionReport;

internal enum FashionReportProgressKind
{
    /// <summary>Before Friday judging opens.</summary>
    Unavailable,

    /// <summary>Judging is open, but we have not synced from Masked Rose yet.</summary>
    Unknown,

    /// <summary>Judged this week but best score is under 80.</summary>
    Incomplete,

    /// <summary>Best score this week is 80+.</summary>
    Complete,
}

internal readonly record struct FashionReportProgressView(
    FashionReportProgressKind Kind,
    int HighestScore,
    int AllowancesRemaining);

/// <summary>
/// Standalone Fashion Report completion tracking via Masked Rose NPC scene data.
/// </summary>
internal sealed unsafe class FashionReportProgressTracker : IDisposable
{
    private const uint MaskedRoseBaseId = 1025176;
    private const int CompleteScoreThreshold = 80;

    private readonly Func<Configuration> getConfig;
    private readonly Func<ulong> getContentId;
    private readonly IPluginLog log;
    private readonly Hook<EventFramework.Delegates.ProcessEventPlay>? eventHook;

    public FashionReportProgressTracker(
        IGameInteropProvider gameInterop,
        Func<Configuration> getConfig,
        Func<ulong> getContentId,
        IPluginLog log)
    {
        this.getConfig = getConfig;
        this.getContentId = getContentId;
        this.log = log;

        try
        {
            eventHook = gameInterop.HookFromAddress<EventFramework.Delegates.ProcessEventPlay>(
                EventFramework.MemberFunctionPointers.ProcessEventPlay,
                OnProcessEventPlay);
            eventHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not hook Fashion Report NPC events; talk to the Masked Rose to sync completion.");
            PluginFileLog.Error("fashion.progress", "ProcessEventPlay hook failed", ex);
        }
    }

    public FashionReportProgressView GetProgress()
    {
        EnsureWeekReset();

        var utc = DateTime.UtcNow;
        if (!FashionReportWeek.IsJudgingOpen(utc))
            return new FashionReportProgressView(FashionReportProgressKind.Unavailable, 0, 4);

        var cache = GetOrCreateCache();
        if (cache == null)
            return new FashionReportProgressView(FashionReportProgressKind.Unknown, 0, 4);

        if (!cache.FashionReportSynced)
            return new FashionReportProgressView(
                FashionReportProgressKind.Unknown,
                cache.FashionReportHighestScore,
                cache.FashionReportAllowancesRemaining);

        var kind = cache.FashionReportHighestScore >= CompleteScoreThreshold
            ? FashionReportProgressKind.Complete
            : FashionReportProgressKind.Incomplete;

        return new FashionReportProgressView(
            kind,
            cache.FashionReportHighestScore,
            cache.FashionReportAllowancesRemaining);
    }

    public void Dispose()
    {
        eventHook?.Disable();
        eventHook?.Dispose();
    }

    private void OnProcessEventPlay(
        EventFramework* thisPtr,
        GameObject* gameObject,
        EventId eventId,
        short scene,
        ulong sceneFlags,
        uint* sceneData,
        byte sceneDataCount)
    {
        eventHook!.Original(thisPtr, gameObject, eventId, scene, sceneFlags, sceneData, sceneDataCount);

        try
        {
            if (gameObject == null || gameObject->BaseId != MaskedRoseBaseId || sceneData == null)
                return;

            var cache = GetOrCreateCache();
            if (cache == null)
                return;

            EnsureWeekReset();

            var dirty = false;
            switch (scene)
            {
                case 1 when sceneDataCount > 1:
                    cache.FashionReportHighestScore = (int)sceneData[0];
                    cache.FashionReportAllowancesRemaining = (int)sceneData[1];
                    dirty = true;
                    break;
                case 2 when sceneDataCount > 0:
                    cache.FashionReportHighestScore = Math.Max((int)sceneData[0], cache.FashionReportHighestScore);
                    dirty = true;
                    break;
                case 5 when sceneDataCount > 0:
                    cache.FashionReportAllowancesRemaining = (int)sceneData[0];
                    dirty = true;
                    break;
            }

            if (!dirty)
                return;

            cache.FashionReportSynced = true;
            cache.FashionReportFromDailyDuty = false;
            cache.FashionReportNextResetUtc = FashionReportWeek.NextJudgingResetUtc();
            getConfig().Save();
            PluginFileLog.Info(
                "fashion.progress",
                $"Masked Rose sync score={cache.FashionReportHighestScore} allowances={cache.FashionReportAllowancesRemaining} reset={cache.FashionReportNextResetUtc:o}");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Fashion Report progress NPC hook failed.");
            PluginFileLog.Warn("fashion.progress", $"NPC sync failed: {ex.Message}");
        }
    }

    private void EnsureWeekReset()
    {
        var cache = GetOrCreateCache();
        if (cache == null)
            return;

        var nextReset = FashionReportWeek.NextJudgingResetUtc();
        var now = DateTime.UtcNow;

        if (cache.FashionReportNextResetUtc == default)
        {
            if (cache.FashionReportSynced || cache.FashionReportHighestScore > 0)
            {
                ClearProgress(cache, nextReset);
                getConfig().Save();
                PluginFileLog.Info("fashion.progress", "Cleared progress with missing NextResetUtc");
                return;
            }

            cache.FashionReportNextResetUtc = nextReset;
            return;
        }

        if (now < cache.FashionReportNextResetUtc)
            return;

        ClearProgress(cache, nextReset);
        getConfig().Save();
        PluginFileLog.Info("fashion.progress", $"Week rolled over; next reset {nextReset:o}");
    }

    private static void ClearProgress(CharacterGlamourCache cache, DateTime nextResetUtc)
    {
        cache.FashionReportHighestScore = 0;
        cache.FashionReportAllowancesRemaining = 4;
        cache.FashionReportSynced = false;
        cache.FashionReportFromDailyDuty = false;
        cache.FashionReportNextResetUtc = nextResetUtc;
    }

    private CharacterGlamourCache? GetOrCreateCache()
    {
        var contentId = getContentId();
        if (contentId == 0)
            return null;

        var config = getConfig();
        if (!config.CharacterCaches.TryGetValue(contentId, out var cache))
        {
            cache = new CharacterGlamourCache();
            config.CharacterCaches[contentId] = cache;
        }

        return cache;
    }
}
