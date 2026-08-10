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
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<EventFramework.Delegates.ProcessEventPlay>? eventHook;

    public FashionReportProgressTracker(
        IGameInteropProvider gameInterop,
        Func<Configuration> getConfig,
        Func<ulong> getContentId,
        IFramework framework,
        IPluginLog log)
    {
        this.getConfig = getConfig;
        this.getContentId = getContentId;
        this.framework = framework;
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

        // Keep the native hook tiny: copy payload and leave. Config / disk / logging hitch the game
        // if done here (Wine makes AppendAllText especially expensive).
        try
        {
            if (gameObject == null || gameObject->BaseId != MaskedRoseBaseId || sceneData == null)
                return;

            if (scene is not (1 or 2 or 5))
                return;

            var count = sceneDataCount;
            if (count == 0)
                return;

            var copied = new uint[count];
            for (var i = 0; i < count; i++)
                copied[i] = sceneData[i];

            var sceneCopy = scene;
            _ = framework.RunOnFrameworkThread(() => ApplyMaskedRoseScene(sceneCopy, copied));
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Fashion Report progress NPC hook failed.");
            PluginFileLog.Warn("fashion.progress", $"NPC sync failed: {ex.Message}");
        }
    }

    private void ApplyMaskedRoseScene(short scene, uint[] sceneData)
    {
        try
        {
            var cache = GetOrCreateCache();
            if (cache == null)
                return;

            // Week reset + scene apply share one Save at the end.
            var weekDirty = EnsureWeekReset(save: false);
            var sceneDirty = false;

            switch (scene)
            {
                case 1 when sceneData.Length > 1:
                    cache.FashionReportHighestScore = (int)sceneData[0];
                    cache.FashionReportAllowancesRemaining = (int)sceneData[1];
                    sceneDirty = true;
                    break;
                case 2 when sceneData.Length > 0:
                    cache.FashionReportHighestScore = Math.Max((int)sceneData[0], cache.FashionReportHighestScore);
                    sceneDirty = true;
                    break;
                case 5 when sceneData.Length > 0:
                    cache.FashionReportAllowancesRemaining = (int)sceneData[0];
                    sceneDirty = true;
                    break;
            }

            if (!weekDirty && !sceneDirty)
                return;

            if (sceneDirty)
            {
                cache.FashionReportSynced = true;
                cache.FashionReportNextResetUtc = FashionReportWeek.ScoreExpiryUtc(DateTime.UtcNow);
            }

            ScheduleSave();
            if (sceneDirty)
            {
                PluginFileLog.Info(
                    "fashion.progress",
                    $"Masked Rose sync score={cache.FashionReportHighestScore} allowances={cache.FashionReportAllowancesRemaining} reset={cache.FashionReportNextResetUtc:o}");
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Fashion Report progress apply failed.");
            PluginFileLog.Warn("fashion.progress", $"NPC apply failed: {ex.Message}");
        }
    }

    private void EnsureWeekReset() => EnsureWeekReset(save: true);

    /// <returns>True when progress fields changed and still need a persist.</returns>
    private bool EnsureWeekReset(bool save)
    {
        var cache = GetOrCreateCache();
        if (cache == null)
            return false;

        var now = DateTime.UtcNow;
        var expiry = FashionReportWeek.ScoreExpiryUtc(now);

        if (cache.FashionReportNextResetUtc == default)
        {
            if (cache.FashionReportSynced || cache.FashionReportHighestScore > 0)
            {
                ClearProgress(cache, expiry);
                if (save)
                    ScheduleSave();
                PluginFileLog.Info("fashion.progress", "Cleared progress with missing expiry");
                return !save;
            }

            cache.FashionReportNextResetUtc = expiry;
            if (save)
                ScheduleSave();
            return !save;
        }

        if (now < cache.FashionReportNextResetUtc)
            return false;

        ClearProgress(cache, expiry);
        if (save)
            ScheduleSave();
        PluginFileLog.Info("fashion.progress", $"Week rolled over; score expires {expiry:o}");
        return !save;
    }

    /// <summary>Debounced config persist — never write the plugin config from the NPC hook thread.</summary>
    private void ScheduleSave() => getConfig().Save();

    private static void ClearProgress(CharacterTrackerCache cache, DateTime nextResetUtc)
    {
        cache.FashionReportHighestScore = 0;
        cache.FashionReportAllowancesRemaining = 4;
        cache.FashionReportSynced = false;
        cache.FashionReportNextResetUtc = nextResetUtc;
    }

    private CharacterTrackerCache? GetOrCreateCache()
    {
        var contentId = getContentId();
        if (contentId == 0)
            return null;

        var config = getConfig();
        if (!config.CharacterCaches.TryGetValue(contentId, out var cache))
        {
            cache = new CharacterTrackerCache();
            config.CharacterCaches[contentId] = cache;
        }

        return cache;
    }
}
