using GlamourTracker;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionReportService
{
    public Task RefreshAsync(bool force = false)
    {
        CancellationToken ct;
        var now = DateTime.UtcNow;
        lock (stateGate)
        {
            // Reuse a recent fetch, but never one from before the weekly reset — that is last
            // week's theme and hints.
            if (!force
                && Snapshot != null
                && LastFetchUtc is { } last
                && (now - last).TotalMinutes < 10
                && last >= FashionReportWeek.LastWeeklyResetUtc(now))
            {
                RebindOwnership();
                return Task.CompletedTask;
            }

            // Soft refreshes should not pile up; forced refresh may supersede an in-flight one.
            if (IsRefreshing && !force)
                return Task.CompletedTask;

            refreshCts?.Cancel();
            refreshCts?.Dispose();
            refreshCts = new CancellationTokenSource();
            ct = refreshCts.Token;
            IsRefreshing = true;
            LastError = null;
        }

        // Do not pass ct into Task.Run — a pre-cancelled token can skip the body and leave
        // IsRefreshing stuck true ("Loading…" forever on later opens).
        return Task.Run(async () =>
        {
            try
            {
                await RefreshCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (stateGate)
                {
                    if (refreshCts is null || refreshCts.Token == ct)
                        IsRefreshing = false;
                }
            }
        });
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            EnsureItemNameIndex();
            var (playerContext, inventory) = await framework
                .RunOnFrameworkThread(() => (vendorLocator.CapturePlayerContext(), inventoryIndex.Scan()))
                .ConfigureAwait(false);

            var state = await client.GetReportStateAsync(ct).ConfigureAwait(false);
            if (state?.LastOptions == null)
            {
                LastError = "Could not load this week's Fashion Report.";
                PluginFileLog.Warn("fashion.sync", "report-state returned empty");
                return;
            }

            var hints = state.LastOptions.Hints ?? [];
            var hintViews = new List<FashionHintSlotView>();
            foreach (var hint in hints)
            {
                if (string.IsNullOrWhiteSpace(hint.Hint) || string.IsNullOrWhiteSpace(hint.Slot))
                    continue;

                var hintItems = await client.GetHintItemsAsync(hint.Hint, hint.Slot, ct).ConfigureAwait(false);
                var cards = hintItems is { Found: true, Items: not null } ? hintItems.Items : [];
                var resolved = new List<FashionResolvedItem>();

                // Bound parallelism keeps refresh responsive without hammering the API.
                using var gate = new SemaphoreSlim(4);
                var tasks = cards
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(async card =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var detail = await GetCachedItemDetailAsync(card.Name!, ct).ConfigureAwait(false);
                            return ResolveItem(
                                card.Name!,
                                card.GarlandUrl,
                                detail,
                                hint.Slot,
                                LabelForSlot(hint.Slot),
                                playerContext,
                                inventory);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    })
                    .ToArray();

                resolved.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
                resolved = RankItems(resolved);
                hintViews.Add(new FashionHintSlotView
                {
                    SlotKey = hint.Slot,
                    SlotLabel = LabelForSlot(hint.Slot),
                    Hint = hint.Hint,
                    RingNote = hint.RingNote is null or "none" ? null : hint.RingNote,
                    Items = resolved,
                    BestPick = resolved.FirstOrDefault(),
                    OwnedCount = resolved.Count(i => i.Owned),
                });
            }

            var dyes = BuildDyeViews(state);
            var easy80 = await BuildEasyAsync("Easy 80", state.Easy80, state.Easy80Fresh, playerContext, inventory, ct)
                .ConfigureAwait(false);
            var easy100 = await BuildEasyAsync("Easy 100", state.Easy100, state.Easy100Fresh, playerContext, inventory, ct)
                .ConfigureAwait(false);

            Snapshot = new FashionReportSnapshot
            {
                Week = state.LastOptions.Week ?? string.Empty,
                Title = state.LastOptions.ReportTitle ?? "Fashion Report",
                DyesFresh = state.DyesFresh,
                TheorycraftUrl = state.Links?.Theorycraft,
                ResultsUrl = state.Links?.Results,
                Hints = hintViews,
                Dyes = dyes,
                Easy80 = easy80,
                Easy100 = easy100,
                FetchedUtc = DateTime.UtcNow,
            };
            LastFetchUtc = Snapshot.FetchedUtc;
            LastError = null;

            var ownedHints = hintViews.Sum(h => h.OwnedCount);
            var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
            PluginFileLog.Info(
                "fashion.sync",
                $"week={Snapshot.Week} title={Snapshot.Title} hints={hintViews.Count} ownedMatches={ownedHints} durationMs={durationMs:0}");
        }
        catch (OperationCanceledException)
        {
            PluginFileLog.Info("fashion.sync", "Refresh cancelled");
        }
        catch (Exception ex)
        {
            // Keep any previous Snapshot so the UI does not blank out on a failed refresh.
            LastError = "Fashion Report refresh failed. See log for details.";
            PluginFileLog.Error("fashion.sync", "Refresh failed", ex);
            this.log.Error(ex, "Fashion Report refresh failed");
        }
    }

    private async Task<FashionReportItemDetailDto?> GetCachedItemDetailAsync(string name, CancellationToken ct)
    {
        if (itemDetailCache.TryGetValue(name, out var cached))
            return cached;

        var detail = await client.GetItemAsync(name, ct).ConfigureAwait(false);
        if (detail != null)
            itemDetailCache[name] = detail;
        return detail;
    }
}
