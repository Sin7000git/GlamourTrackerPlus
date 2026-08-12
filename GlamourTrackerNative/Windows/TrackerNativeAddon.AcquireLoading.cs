using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;

namespace GlamourTracker.Windows;

internal sealed partial class TrackerNativeAddon
{
    private const int CategoryScanItemConcurrency = 24;
    private const int AcquirePieceConcurrency = 8;

    /// <summary>Window-scoped token so background source loads stop when the window closes.</summary>
    private CancellationToken WindowToken => (windowCts ??= new CancellationTokenSource()).Token;

    /// <summary>Sources load once per set; a failed load is retried after a cooldown, not every frame.</summary>
    private bool NeedsAcquireLoad(uint setId)
    {
        if (setAcquireLoaded.ContainsKey(setId))
            return false;

        return !setAcquireRetryAfter.TryGetValue(setId, out var retryAt) || DateTime.UtcNow >= retryAt;
    }

    private bool NeedsCategory(uint setId) => !setCategoryCache.ContainsKey(setId);

    private void RememberSetCategory(uint setId, OutfitCategoryFilter cat, bool persist = false)
    {
        setCategoryCache[setId] = cat;
        categoryCacheEpoch++;
        if (persist)
            plugin.OutfitSetCategories.Upsert(setId, cat);
    }

    private void RememberItemKind(uint itemId, FashionItemAcquireKind kind)
    {
        if (itemId == 0)
            return;
        // Cache Unknown too so we do not re-fetch the same miss every scan.
        itemAcquireKindCache[itemId] = kind;
    }

    /// <summary>
    /// Classify Craft sets from the local Recipe sheet (no network). Majority craftable pieces → Craft.
    /// </summary>
    private int SeedCategoriesFromLocalRecipes()
    {
        var seeded = 0;
        foreach (var set in plugin.OutfitSets.GetSets())
        {
            if (!NeedsCategory(set.SetId))
                continue;

            if (!TryClassifyCraftFromRecipes(set, out var cat))
                continue;

            setCategoryCache[set.SetId] = cat;
            seeded++;
        }

        if (seeded > 0)
        {
            categoryCacheEpoch++;
            plugin.OutfitSetCategories.UpsertMany(
                setCategoryCache.Where(kv => kv.Value == OutfitCategoryFilter.Craft));
            PluginFileLog.Info("outfit.acquire", $"Seeded {seeded} Craft set categories from local recipes");
        }

        return seeded;
    }

    private bool TryClassifyCraftFromRecipes(OutfitSetInfo set, out OutfitCategoryFilter cat)
    {
        cat = OutfitCategoryFilter.Other;
        var pieces = set.Pieces.Where(static p => p.ItemId != 0).ToList();
        if (pieces.Count == 0)
            return false;

        var craft = 0;
        foreach (var piece in pieces)
        {
            if (plugin.RecipeLookup.TryGetRecipeId(piece.ItemId, out _))
                craft++;
        }

        // Strict majority — same idea as AggregateSetCategory once kinds are known.
        if (craft * 2 <= pieces.Count)
            return false;

        cat = OutfitCategoryFilter.Craft;
        return true;
    }

    private FashionItemAcquireKind? PeekPieceKind(uint itemId)
    {
        if (itemId == 0)
            return FashionItemAcquireKind.Unknown;

        if (itemAcquireCache.TryGetValue(itemId, out var resolved))
            return resolved.AcquireKind;

        if (itemAcquireKindCache.TryGetValue(itemId, out var kind))
            return kind;

        if (plugin.RecipeLookup.TryGetRecipeId(itemId, out _))
            return FashionItemAcquireKind.Craft;

        return null;
    }

    /// <summary>Try to classify a set from kinds already known (cache / recipes). Returns true when locked.</summary>
    private bool TryClassifySetFromKnownKinds(OutfitSetInfo set, out OutfitCategoryFilter cat)
    {
        cat = OutfitCategoryFilter.Other;
        var pieces = set.Pieces
            .Where(static p => p.ItemId != 0)
            .GroupBy(static p => p.ItemId)
            .Select(static g => g.Key)
            .ToList();
        if (pieces.Count == 0)
            return true;

        var slots = new FashionItemAcquireKind?[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
            slots[i] = PeekPieceKind(pieces[i]);

        return TrackerNativeHelpers.TryAggregateSetCategoryPartial(slots, out cat);
    }

    private int ClassifyPendingSetsFromKnownKinds(IReadOnlyList<OutfitSetInfo> pending)
    {
        var classified = 0;
        foreach (var set in pending)
        {
            if (!NeedsCategory(set.SetId))
                continue;
            if (!TryClassifySetFromKnownKinds(set, out var cat))
                continue;
            RememberSetCategory(set.SetId, cat);
            classified++;
        }

        return classified;
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

                using var gate = new SemaphoreSlim(AcquirePieceConcurrency);
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
                        RememberItemKind(key, resolved.AcquireKind);
                        if (piece.ItemId != key && piece.ItemId != 0)
                        {
                            itemAcquireCache.TryAdd(piece.ItemId, resolved);
                            RememberItemKind(piece.ItemId, resolved.AcquireKind);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                var kinds = set.Pieces
                    .Select(p => itemAcquireCache.TryGetValue(p.ItemId, out var r) ? r.AcquireKind : FashionItemAcquireKind.Unknown);
                RememberSetCategory(set.SetId, TrackerNativeHelpers.AggregateSetCategory(kinds), persist: true);
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
            // Prefer cached browser rows; rebuild only when the cache is cold.
            var rows = cachedOutfitRows ?? BuildOutfitRows();
            var select = rows.FirstOrDefault(r => r.Key == selectedBrowserKey);
            if (select != null)
                RebuildBrowserDetail(select, force: true, scrollToTop: false);
        });

    private Task RefreshBrowserListAfterCategoryProgressAsync() =>
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!IsOpen || selectedTab != TabOutfitSets)
                return;
            lastBrowserListSignature = string.Empty;
            RefreshBrowserList(force: true, rebuildDetail: false);
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
            plugin.OutfitSetCategories.Hydrate(setCategoryCache);
            plugin.OutfitSetCategories.HydrateItemKinds(itemAcquireKindCache);

            // Instant Craft filter: local Excel recipes, no HTTP.
            SeedCategoriesFromLocalRecipes();
            await RefreshBrowserListAfterCategoryProgressAsync().ConfigureAwait(false);

            // Sets still missing a category after craft seed + disk hydrate.
            var pendingSets = plugin.OutfitSets.GetSets()
                .Where(s => NeedsCategory(s.SetId))
                .ToList();

            // Reclassify from persisted / in-memory item kinds (no HTTP).
            if (ClassifyPendingSetsFromKnownKinds(pendingSets) > 0)
                await RefreshBrowserListAfterCategoryProgressAsync().ConfigureAwait(false);

            pendingSets = plugin.OutfitSets.GetSets()
                .Where(s => NeedsCategory(s.SetId))
                .ToList();

            if (pendingSets.Count == 0)
            {
                PluginFileLog.Info(
                    "outfit.acquire",
                    $"Category scan skipped; all {setCategoryCache.Count} set categories already known");
                return;
            }

            // One HTTP resolve per unique piece across all pending sets (not per set).
            var itemIds = pendingSets
                .SelectMany(s => s.Pieces)
                .Select(p => p.ItemId)
                .Where(id => id != 0 && PeekPieceKind(id) is null)
                .Distinct()
                .ToList();

            var completedItems = 0;
            using var gate = new SemaphoreSlim(CategoryScanItemConcurrency);
            var tasks = itemIds.Select(async itemId =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (PeekPieceKind(itemId) is not null)
                        return;

                    var name = TrackerNativeHelpers.ResolveItemName(itemId);
                    if (name.StartsWith("Item #", StringComparison.Ordinal))
                    {
                        RememberItemKind(itemId, FashionItemAcquireKind.Unknown);
                        return;
                    }

                    var kind = await plugin.FashionReport
                        .ResolveAcquireKindAsync(name, ct)
                        .ConfigureAwait(false);
                    RememberItemKind(itemId, kind);

                    var n = Interlocked.Increment(ref completedItems);
                    // Classify + refresh often early, then every 40 items.
                    if (n <= 30 || n % 40 == 0 || n == itemIds.Count)
                    {
                        ClassifyPendingSetsFromKnownKinds(pendingSets);
                        await RefreshBrowserListAfterCategoryProgressAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Final pass: anything still open becomes Aggregate of what we know (Unknown slots ignored).
            foreach (var set in pendingSets)
            {
                if (!NeedsCategory(set.SetId))
                    continue;

                if (TryClassifySetFromKnownKinds(set, out var cat))
                {
                    RememberSetCategory(set.SetId, cat);
                    continue;
                }

                var kinds = set.Pieces
                    .Where(static p => p.ItemId != 0)
                    .Select(p => PeekPieceKind(p.ItemId) ?? FashionItemAcquireKind.Unknown);
                RememberSetCategory(set.SetId, TrackerNativeHelpers.AggregateSetCategory(kinds));
            }

            plugin.OutfitSetCategories.UpsertMany(setCategoryCache);
            plugin.OutfitSetCategories.UpsertItemKinds(itemAcquireKindCache);
            PluginFileLog.Info(
                "outfit.acquire",
                $"Category scan finished; sets={setCategoryCache.Count} itemKinds={itemAcquireKindCache.Count} httpItems={itemIds.Count}");
        }
        catch (OperationCanceledException)
        {
            // Persist progress so a cancelled scan still helps next time.
            plugin.OutfitSetCategories.UpsertMany(setCategoryCache);
            plugin.OutfitSetCategories.UpsertItemKinds(itemAcquireKindCache);
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("outfit.acquire", $"Category scan failed: {ex.Message}");
        }
        finally
        {
            categoryScanRunning = false;
            await RefreshBrowserListAfterCategoryProgressAsync().ConfigureAwait(false);
        }
    }

}
