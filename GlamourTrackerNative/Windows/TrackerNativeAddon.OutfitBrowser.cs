using System.Numerics;
using GlamourTracker.Services;
using GlamourTracker.Windows.Native;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;

using static GlamourTracker.Windows.TrackerNativeNodeFactory;

namespace GlamourTracker.Windows;

internal sealed partial class TrackerNativeAddon
{
    private void BuildBrowserToolbar(float width)
    {
        if (browserToolbar == null)
            return;

        outfitFilterInput = new SearchInputNode
        {
            Position = new Vector2(0f, 2f),
            Size = new Vector2(200f, RowH),
            PlaceholderString = "Filter by name…",
        };
        outfitFilterInput.OnInputComplete = s =>
        {
            outfitFilter = s.ToString();
            RefreshBrowserList(force: true);
        };
        outfitFilterInput.AttachNode(browserToolbar);

        sortDropDown = new StringDropDownNode
        {
            Position = new Vector2(210f, 2f),
            Size = new Vector2(150f, RowH),
            Options = TrackerNativeHelpers.SortModeLabels.ToList(),
            SelectedOption = TrackerNativeHelpers.SortModeLabels[(int)sortMode],
            MaxListOptions = 3,
        };
        sortDropDown.OnOptionSelected = label =>
        {
            var idx = Array.IndexOf(TrackerNativeHelpers.SortModeLabels, label);
            if (idx < 0)
                return;
            sortMode = (OutfitSortMode)idx;
            RefreshBrowserList(force: true);
        };
        sortDropDown.AttachNode(browserToolbar);

        categoryDropDown = new StringDropDownNode
        {
            Position = new Vector2(370f, 2f),
            Size = new Vector2(150f, RowH),
            Options = TrackerNativeHelpers.CategoryFilterLabels.ToList(),
            SelectedOption = TrackerNativeHelpers.CategoryFilterLabels[(int)categoryFilter],
            MaxListOptions = 7,
        };
        categoryDropDown.OnOptionSelected = label =>
        {
            var idx = Array.IndexOf(TrackerNativeHelpers.CategoryFilterLabels, label);
            if (idx < 0)
                return;
            categoryFilter = (OutfitCategoryFilter)idx;
            _ = ScanAllSetCategoriesAsync();
            RefreshBrowserList(force: true);
        };
        categoryDropDown.AttachNode(browserToolbar);

        storageDropDown = new StringDropDownNode
        {
            Position = new Vector2(530f, 2f),
            Size = new Vector2(140f, RowH),
            Options = TrackerNativeHelpers.StorageFilterLabels.ToList(),
            SelectedOption = TrackerNativeHelpers.StorageFilterLabels[(int)storageFilter],
            MaxListOptions = 3,
        };
        storageDropDown.OnOptionSelected = label =>
        {
            var idx = Array.IndexOf(TrackerNativeHelpers.StorageFilterLabels, label);
            if (idx < 0)
                return;
            storageFilter = (OutfitStorageFilter)idx;
            RefreshBrowserList(force: true, rebuildDetail: true);
        };
        storageDropDown.AttachNode(browserToolbar);

        wishlistOnlyCheckbox = MakeCheckbox("Wishlist", showWishlistOnly, v =>
        {
            showWishlistOnly = v;
            RefreshBrowserList(force: true);
        });
        wishlistOnlyCheckbox.TextTooltip =
            "Only sets on your wishlist, or sets that contain a wishlisted piece.";
        wishlistOnlyCheckbox.AttachNode(browserToolbar);

        missingOnlyCheckbox = MakeCheckbox("Missing pieces", showMissingOnly, v =>
        {
            showMissingOnly = v;
            if (v)
            {
                showOwnedOnly = false;
                SyncOwnedCheckbox();
            }

            RefreshBrowserList(force: true);
        });
        missingOnlyCheckbox.TextTooltip = "Sets that are not fully complete.";
        missingOnlyCheckbox.AttachNode(browserToolbar);

        ownedOnlyCheckbox = MakeCheckbox("Owned pieces", showOwnedOnly, v =>
        {
            showOwnedOnly = v;
            if (v)
            {
                showMissingOnly = false;
                SyncMissingCheckbox();
            }

            RefreshBrowserList(force: true);
        });
        ownedOnlyCheckbox.TextTooltip = "Sets where you own at least one piece (includes incomplete sets).";
        ownedOnlyCheckbox.AttachNode(browserToolbar);

        // Pack by real label width — hardcoded X left a big gap after the short "Wishlist" label.
        const float filterGap = 12f;
        var filterX = 0f;
        wishlistOnlyCheckbox.Position = new Vector2(filterX, 34f);
        filterX += wishlistOnlyCheckbox.Width + filterGap;
        missingOnlyCheckbox.Position = new Vector2(filterX, 34f);
        filterX += missingOnlyCheckbox.Width + filterGap;
        ownedOnlyCheckbox.Position = new Vector2(filterX, 34f);
    }

    private void SyncBrowserFilterControls()
    {
        if (outfitFilterInput != null)
            outfitFilterInput.String = outfitFilter;

        if (sortDropDown != null)
            sortDropDown.SelectedOption = TrackerNativeHelpers.SortModeLabels[(int)sortMode];
        if (categoryDropDown != null)
            categoryDropDown.SelectedOption = TrackerNativeHelpers.CategoryFilterLabels[(int)categoryFilter];
        if (storageDropDown != null)
            storageDropDown.SelectedOption = TrackerNativeHelpers.StorageFilterLabels[(int)storageFilter];

        SyncMissingCheckbox();
        SyncOwnedCheckbox();
        SyncWishlistCheckbox();
    }

    private void SyncWishlistCheckbox()
    {
        if (wishlistOnlyCheckbox == null)
            return;
        wishlistOnlyCheckbox.OnClick = null;
        wishlistOnlyCheckbox.IsChecked = showWishlistOnly;
        wishlistOnlyCheckbox.OnClick = v =>
        {
            showWishlistOnly = v;
            RefreshBrowserList(force: true);
        };
    }

    private void SyncOwnedCheckbox()
    {
        if (ownedOnlyCheckbox == null)
            return;
        ownedOnlyCheckbox.OnClick = null;
        ownedOnlyCheckbox.IsChecked = showOwnedOnly;
        ownedOnlyCheckbox.OnClick = v =>
        {
            showOwnedOnly = v;
            if (v)
            {
                showMissingOnly = false;
                SyncMissingCheckbox();
            }

            RefreshBrowserList(force: true);
        };
    }

    private void SyncMissingCheckbox()
    {
        if (missingOnlyCheckbox == null)
            return;
        missingOnlyCheckbox.OnClick = null;
        missingOnlyCheckbox.IsChecked = showMissingOnly;
        missingOnlyCheckbox.OnClick = v =>
        {
            showMissingOnly = v;
            if (v)
            {
                showOwnedOnly = false;
                SyncOwnedCheckbox();
            }

            RefreshBrowserList(force: true);
        };
    }

    // ── Outfit sets browser ───────────────────────────────────────────────

    /// <param name="rebuildDetail">
    /// Only rebuild the piece list when selection changes or acquire data for the open set finished.
    /// Default false — list/scan refreshes must not tear down CollapsingHeaders (causes flicker).
    /// </param>
    private void RefreshBrowserList(bool force, bool rebuildDetail = false)
    {
        if (browserList == null)
            return;

        var inputSig = BuildOutfitRowsInputSignature();
        if (force || cachedOutfitRows == null || inputSig != cachedOutfitRowsInputSig)
        {
            cachedOutfitRows = BuildOutfitRows();
            cachedOutfitRowsInputSig = inputSig;
        }

        var rows = cachedOutfitRows;
        // Input signature already covers ownership, filters, and category-cache epoch — no per-row Join.
        if (!force && inputSig == lastBrowserListSignature)
        {
            if (rebuildDetail && !string.IsNullOrEmpty(selectedBrowserKey))
            {
                var selected = rows.FirstOrDefault(r => r.Key == selectedBrowserKey);
                if (selected != null)
                    RebuildBrowserDetail(selected, force: true, scrollToTop: false);
            }

            return;
        }

        lastBrowserListSignature = inputSig;

        browserList.OptionsList = rows;
        browserList.SelectedItems.Clear();

        TrackerNativeListRow? select = null;
        if (!string.IsNullOrEmpty(selectedBrowserKey))
            select = rows.FirstOrDefault(r => r.Key == selectedBrowserKey);
        select ??= rows.FirstOrDefault();

        if (select != null)
            browserList.SelectedItems.Add(select);

        browserList.Update();

        if (select != null)
        {
            var selectionChanged = selectedBrowserKey != select.Key;
            selectedBrowserKey = select.Key;
            var forceDetail = selectionChanged || rebuildDetail || pendingForceSelectDetail;
            var scrollDetail = selectionChanged || pendingForceSelectDetail;
            pendingForceSelectDetail = false;
            if (forceDetail)
                RebuildBrowserDetail(select, force: true, scrollToTop: scrollDetail);

            // Scroll the list so the selected row is in view after a deep-link.
            if (forceDetail)
            {
                var idx = rows.IndexOf(select);
                if (idx >= 0)
                {
                    var spacing = browserList.ItemSpacing;
                    browserList.ScrollBarNode.ScrollPosition =
                        (int)(idx * (TrackerNativeListItemNode.ItemHeight + spacing));
                    browserList.Update();
                }
            }
        }
        else
        {
            pendingForceSelectDetail = false;
            selectedBrowserKey = string.Empty;
            ClearBrowserDetail(
                categoryFilter != OutfitCategoryFilter.All && categoryScanRunning
                    ? "Still checking where these sets come from. Crafting is instant; other sources share one lookup per piece and get faster after the first pass."
                    : "No outfit sets match your filters.");
        }
    }

    private string BuildOutfitRowsInputSignature() =>
        $"{plugin.OwnershipIndex.Revision}|{plugin.OutfitSets.CatalogEpoch}|{categoryCacheEpoch}|{wishlistRevision}|"
        + $"{outfitFilter}|{showMissingOnly}|{showOwnedOnly}|{showWishlistOnly}|{(int)sortMode}|{(int)categoryFilter}|{(int)storageFilter}";

    private List<TrackerNativeListRow> BuildOutfitRows()
    {
        var isArmoireEligible = plugin.CabinetCatalog.IsArmoireEligible;
        var matched = new List<OutfitSetInfo>();
        var wishlistCache = showWishlistOnly ? CurrentWishlistCache() : null;

        foreach (var set in plugin.OutfitSets.GetSets())
        {
            if (!string.IsNullOrWhiteSpace(outfitFilter)
                && !set.Name.Contains(outfitFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Missing = incomplete sets. Owned = any stored pieces (includes partial sets).
            if (showMissingOnly)
            {
                if (set.MissingPieces <= 0)
                    continue;
            }
            else if (showOwnedOnly && set.OwnedPieceCount <= 0)
            {
                continue;
            }

            if (showWishlistOnly
                && (wishlistCache == null || !OutfitWishlist.SetHasWishlistMatch(wishlistCache, set)))
                continue;

            if (categoryFilter != OutfitCategoryFilter.All
                && (!setCategoryCache.TryGetValue(set.SetId, out var cat) || cat != categoryFilter))
                continue;

            if (storageFilter != OutfitStorageFilter.All
                && !TrackerNativeHelpers.SetMatchesStorage(set, storageFilter))
                continue;

            matched.Add(set);
        }

        switch (sortMode)
        {
            case OutfitSortMode.Progress:
                matched.Sort(static (a, b) =>
                {
                    var ap = a.TotalPieces == 0 ? 0f : a.OwnedPieceCount / (float)a.TotalPieces;
                    var bp = b.TotalPieces == 0 ? 0f : b.OwnedPieceCount / (float)b.TotalPieces;
                    var c = bp.CompareTo(ap);
                    return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                break;
            case OutfitSortMode.MissingFirst:
                matched.Sort(static (a, b) =>
                {
                    var c = b.MissingPieces.CompareTo(a.MissingPieces);
                    return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                break;
            default:
                matched.Sort(static (a, b) =>
                    string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                break;
        }

        var rows = new List<TrackerNativeListRow>(matched.Count);
        foreach (var set in matched)
        {
            var (storedCount, missingCount, total) = TrackerNativeHelpers.SplitPiecesForFilter(
                set,
                storageFilter,
                IsGlamourPiece,
                isArmoireEligible,
                splitStoredScratch,
                splitMissingScratch);

            var iconPiece = splitMissingScratch.Count > 0
                ? splitMissingScratch[0]
                : splitStoredScratch.Count > 0
                    ? splitStoredScratch[0]
                    : set.Pieces.Count > 0 ? set.Pieces[0] : default;

            var status = TrackerNativeHelpers.FormatSetCollectionStatus(
                set,
                storageFilter,
                storedCount,
                missingCount,
                total);
            rows.Add(new TrackerNativeListRow
            {
                Key = $"set|{set.SetId}",
                Title = set.Name,
                Subtitle = status,
                IconId = TrackerNativeHelpers.ResolveItemIcon(iconPiece.ItemId),
                Badge = missingCount == 0 ? "Complete" : $"{missingCount} missing",
                BadgeColor = TrackerNativeHelpers.GetSetStatusColor(storedCount, missingCount),
                OutfitSet = set,
            });
        }

        return rows;
    }

    private bool IsGlamourPiece(uint itemId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return false;
        return GlamourOwnershipIndex.IsGlamourGear(item);
    }

    private void OnBrowserRowSelected(TrackerNativeListRow? row)
    {
        if (row == null)
            return;

        if (selectedBrowserKey != row.Key)
            expandedPieceKeys.Clear();

        selectedBrowserKey = row.Key;
        RebuildBrowserDetail(row, force: true, scrollToTop: true);
    }

    private void RebuildBrowserDetail(TrackerNativeListRow row, bool force, bool scrollToTop = false)
    {
        if (browserDetail == null || row.OutfitSet == null)
            return;

        var set = row.OutfitSet;
        var loaded = setAcquireLoaded.ContainsKey(set.SetId);
        if (pendingExpandItemId is uint expandId && expandId != 0)
        {
            foreach (var piece in set.Pieces)
            {
                if (ItemIdHelper.GlamourBaseId(piece.ItemId) == ItemIdHelper.GlamourBaseId(expandId))
                    expandedPieceKeys.Add(PieceKey(set.SetId, piece));
            }

            pendingExpandItemId = null;
            force = true;
            scrollToTop = true;
        }

        // Only rebuild when the selected set / load state / ownership changes — not on global cache growth.
        var detailKey = $"{row.Key}|{set.OwnedPieceCount}|{set.MissingPieces}|{loaded}|{(int)storageFilter}|{detailRebuildEpoch}|{wishlistRevision}";
        if (!force && detailKey == lastBrowserDetailKey)
            return;
        lastBrowserDetailKey = detailKey;

        var list = browserDetail.ContentNode;
        list.Clear();
        var width = MathF.Max(120f, browserDetail.Width - 18f);

        BuildOutfitDetail(list, set, width);

        list.RecalculateLayout();
        var doScrollTop = scrollToTop && !suppressDetailScrollTop;
        suppressDetailScrollTop = false;

        // Always reset-through-0 then restore (or stay at 0). RecalculateSizes alone preserves a
        // stale ContentNode.Y and leaves empty space after wishlist rebuilds / height changes.
        ApplyBrowserDetailScroll(scrollToTop: doScrollTop);

        var key = row.Key;
        var keepTop = doScrollTop;
        _ = Plugin.Framework.RunOnTick(() =>
        {
            if (browserDetail == null || selectedBrowserKey != key)
                return;
            ApplyBrowserDetailScroll(scrollToTop: keepTop);
        }, delayTicks: 1);

        if (NeedsAcquireLoad(set.SetId))
            _ = LoadSetAcquireAsync(set, refreshUi: true, WindowToken);
    }

    private void ClearBrowserDetail(string message)
    {
        if (browserDetail == null)
            return;
        lastBrowserDetailKey = string.Empty;
        var list = browserDetail.ContentNode;
        list.Clear();
        list.AddNode(MakeMuted(message, MathF.Max(120f, browserDetail.Width - 18f)));
        list.RecalculateLayout();
        ApplyBrowserDetailScroll(scrollToTop: true);
    }

}
