using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace GlamourTracker.Services.FashionReport;

internal enum FashionReportProgressKind
{
    /// <summary>Before Friday judging opens.</summary>
    Unavailable,

    /// <summary>Judging is open, but we have not synced from Masked Rose (or DailyDuty) yet.</summary>
    Unknown,

    /// <summary>Judged this week but best score is under 80.</summary>
    Incomplete,

    /// <summary>Best score this week is 80+.</summary>
    Complete,
}

internal readonly record struct FashionReportProgressView(
    FashionReportProgressKind Kind,
    int HighestScore,
    int AllowancesRemaining,
    bool FromDailyDuty);

/// <summary>
/// Standalone Fashion Report completion tracking (same Masked Rose scene data DailyDuty uses).
/// Optionally reads DailyDuty's saved data when present — no DailyDuty IPC or source changes.
/// </summary>
internal sealed unsafe class FashionReportProgressTracker : IDisposable
{
    private const uint MaskedRoseBaseId = 1025176;
    private const int CompleteScoreThreshold = 80;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Func<Configuration> getConfig;
    private readonly Func<ulong> getContentId;
    private readonly IPluginLog log;
    private readonly Hook<EventFramework.Delegates.ProcessEventPlay>? eventHook;

    private DateTime lastDailyDutyPollUtc = DateTime.MinValue;

    public FashionReportProgressTracker(
        IDalamudPluginInterface pluginInterface,
        IGameInteropProvider gameInterop,
        Func<Configuration> getConfig,
        Func<ulong> getContentId,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
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
            log.Warning(ex, "Could not hook Fashion Report NPC events; completion status needs DailyDuty data or Manual sync.");
            PluginFileLog.Error("fashion.progress", "ProcessEventPlay hook failed", ex);
        }
    }

    public FashionReportProgressView GetProgress()
    {
        EnsureWeekReset();
        TryImportDailyDuty(quiet: true);

        var utc = DateTime.UtcNow;
        if (!FashionReportWeek.IsJudgingOpen(utc))
            return new FashionReportProgressView(FashionReportProgressKind.Unavailable, 0, 4, false);

        var cache = GetOrCreateCache();
        if (cache == null)
            return new FashionReportProgressView(FashionReportProgressKind.Unknown, 0, 4, false);

        if (!cache.FashionReportSynced)
            return new FashionReportProgressView(FashionReportProgressKind.Unknown, cache.FashionReportHighestScore, cache.FashionReportAllowancesRemaining, cache.FashionReportFromDailyDuty);

        var kind = cache.FashionReportHighestScore >= CompleteScoreThreshold
            ? FashionReportProgressKind.Complete
            : FashionReportProgressKind.Incomplete;

        return new FashionReportProgressView(
            kind,
            cache.FashionReportHighestScore,
            cache.FashionReportAllowancesRemaining,
            cache.FashionReportFromDailyDuty);
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
                $"Masked Rose sync score={cache.FashionReportHighestScore} allowances={cache.FashionReportAllowancesRemaining}");
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

        if (cache.FashionReportNextResetUtc == default)
        {
            cache.FashionReportNextResetUtc = FashionReportWeek.NextJudgingResetUtc();
            return;
        }

        if (DateTime.UtcNow < cache.FashionReportNextResetUtc)
            return;

        cache.FashionReportHighestScore = 0;
        cache.FashionReportAllowancesRemaining = 4;
        cache.FashionReportSynced = false;
        cache.FashionReportFromDailyDuty = false;
        cache.FashionReportNextResetUtc = FashionReportWeek.NextJudgingResetUtc();
        getConfig().Save();
    }

    private void TryImportDailyDuty(bool quiet)
    {
        // Cheap poll — DailyDuty writes when the player talks to Masked Rose.
        if ((DateTime.UtcNow - lastDailyDutyPollUtc).TotalSeconds < 5)
            return;
        lastDailyDutyPollUtc = DateTime.UtcNow;

        var cache = GetOrCreateCache();
        if (cache == null)
            return;

        // Prefer our own NPC sync; only fill gaps / refresh when DailyDuty has data.
        var contentId = getContentId();
        if (contentId == 0)
            return;

        try
        {
            var configsRoot = pluginInterface.ConfigDirectory.Parent;
            if (configsRoot == null)
                return;

            var path = Path.Combine(configsRoot.FullName, "DailyDuty", contentId.ToString(), "FashionReport.data.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("HighestWeeklyScore", out var scoreEl)
                || !root.TryGetProperty("AllowancesRemaining", out var allowEl))
                return;

            var score = scoreEl.GetInt32();
            var allowances = allowEl.GetInt32();

            // Ignore empty defaults when we already have a better local sync.
            if (cache.FashionReportSynced
                && !cache.FashionReportFromDailyDuty
                && cache.FashionReportHighestScore >= score)
                return;

            if (score == 0 && allowances == 4 && !cache.FashionReportSynced)
            {
                // Likely untouched DailyDuty defaults — still mark unknown, not incomplete.
                return;
            }

            cache.FashionReportHighestScore = Math.Max(score, cache.FashionReportHighestScore);
            cache.FashionReportAllowancesRemaining = allowances;
            cache.FashionReportSynced = true;
            cache.FashionReportFromDailyDuty = true;
            cache.FashionReportNextResetUtc = FashionReportWeek.NextJudgingResetUtc();
            getConfig().Save();

            if (!quiet)
                PluginFileLog.Info("fashion.progress", $"Imported DailyDuty score={score} allowances={allowances}");
        }
        catch (Exception ex)
        {
            if (!quiet)
                PluginFileLog.Warn("fashion.progress", $"DailyDuty import failed: {ex.Message}");
        }
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
