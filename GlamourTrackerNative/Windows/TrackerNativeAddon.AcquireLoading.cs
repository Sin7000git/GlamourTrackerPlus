using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;

namespace GlamourTracker.Windows;

internal sealed partial class TrackerNativeAddon
{
    /// <summary>Window-scoped token so background source loads stop when the window closes.</summary>
    private CancellationToken WindowToken => (windowCts ??= new CancellationTokenSource()).Token;

    /// <summary>Sources load once per set; a failed load is retried after a cooldown, not every frame.</summary>
    private bool NeedsAcquireLoad(uint setId)
    {
        if (setAcquireLoaded.ContainsKey(setId))
            return false;

        return !setAcquireRetryAfter.TryGetValue(setId, out var retryAt) || DateTime.UtcNow >= retryAt;
    }

    private async Task LoadSetAcquireAsync(OutfitSetInfo set, bool refreshUi, CancellationToken ct)
    {
        if (refreshUi)
            setAcquirePendingUi[set.SetId] = 1;

        // One in-flight load per set — concurrent expand/scan calls were rebuilding detail repeatedly.
        if (!setAcquireInFlight.TryAdd(set.SetId, 1))
            return;

        try
        {
            if (!setAcquireLoaded.ContainsKey(set.SetId))
            {
                var pieces = set.Pieces
                    .Where(p => p.ItemId != 0)
                    .GroupBy(p => p.ItemId)
                    .Select(g => g.First())
                    .ToList();

                using var gate = new SemaphoreSlim(4);
                var tasks = pieces.Select(async piece =>
                {
                    if (itemAcquireCache.ContainsKey(piece.ItemId))
                        return;

                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var name = TrackerNativeHelpers.ResolveItemName(piece.ItemId);
                        if (name.StartsWith("Item #", StringComparison.Ordinal))
                            return;

                        var resolved = await plugin.FashionReport
                            .ResolveNamedItemAsync(name, ct)
                            .ConfigureAwait(false);
                        var key = resolved.ItemId != 0 ? resolved.ItemId : piece.ItemId;
                        itemAcquireCache[key] = resolved;
                        if (piece.ItemId != key && piece.ItemId != 0)
                            itemAcquireCache.TryAdd(piece.ItemId, resolved);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                var kinds = set.Pieces
                    .Select(p => itemAcquireCache.TryGetValue(p.ItemId, out var r) ? r.AcquireKind : FashionItemAcquireKind.Unknown);
                setCategoryCache[set.SetId] = TrackerNativeHelpers.AggregateSetCategory(kinds);
                setAcquireLoaded[set.SetId] = 1;
                setAcquireRetryAfter.TryRemove(set.SetId, out _);
            }

            var wantUi = refreshUi || setAcquirePendingUi.TryRemove(set.SetId, out _);
            if (wantUi)
                await RefreshSelectedSetDetailAsync(set.SetId).ConfigureAwait(false);
            else
                setAcquirePendingUi.TryRemove(set.SetId, out _);
        }
        catch (OperationCanceledException)
        {
            setAcquirePendingUi.TryRemove(set.SetId, out _);
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("outfit.acquire", $"Failed loading sources for set {set.SetId}", ex);
            setAcquireRetryAfter[set.SetId] = DateTime.UtcNow.AddMinutes(AcquireRetryCooldownMinutes);
        }
        finally
        {
            setAcquireInFlight.TryRemove(set.SetId, out _);
        }
    }

    private Task RefreshSelectedSetDetailAsync(uint setId) =>
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!IsOpen || selectedTab != TabOutfitSets)
                return;
            if (selectedBrowserKey != $"set|{setId}")
                return;

            // Refresh only this set's detail once — do not rebuild on every list/scan tick.
            detailRebuildEpoch++;
            suppressDetailScrollTop = true;
            lastBrowserDetailKey = string.Empty;
            var select = BuildOutfitRows().FirstOrDefault(r => r.Key == selectedBrowserKey);
            if (select != null)
                RebuildBrowserDetail(select, force: true);
        });

    /// <summary>Background-scan every outfit set so source filters can match the full catalog.</summary>
    private async Task ScanAllSetCategoriesAsync()
    {
        categoryScanCts?.Cancel();
        categoryScanCts?.Dispose();
        categoryScanCts = CancellationTokenSource.CreateLinkedTokenSource(WindowToken);
        var ct = categoryScanCts.Token;
        categoryScanRunning = true;

        try
        {
            var sets = plugin.OutfitSets.GetSets()
                .Where(s => NeedsAcquireLoad(s.SetId))
                .ToList();

            var completed = 0;
            using var gate = new SemaphoreSlim(2);
            var tasks = sets.Select(async set =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await LoadSetAcquireAsync(set, refreshUi: false, ct).ConfigureAwait(false);
                    var n = Interlocked.Increment(ref completed);
                    if (n % 15 == 0 || n == sets.Count)
                    {
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (!IsOpen || selectedTab != TabOutfitSets)
                                return;
                            lastBrowserListSignature = string.Empty;
                            RefreshBrowserList(force: true, rebuildDetail: false);
                        }).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            PluginFileLog.Info("outfit.acquire", $"Category scan finished; cached items={itemAcquireCache.Count} sets={setAcquireLoaded.Count}");
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("outfit.acquire", $"Category scan failed: {ex.Message}");
        }
        finally
        {
            categoryScanRunning = false;
        }
    }

}
