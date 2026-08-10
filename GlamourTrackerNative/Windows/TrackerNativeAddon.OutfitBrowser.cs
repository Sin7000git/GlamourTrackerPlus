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
        missingOnlyCheckbox.Position = new Vector2(0f, 34f);
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
        ownedOnlyCheckbox.Position = new Vector2(140f, 34f);
        ownedOnlyCheckbox.TextTooltip = "Sets where you own at least one piece (includes incomplete sets).";
        ownedOnlyCheckbox.AttachNode(browserToolbar);
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

        var rows = BuildOutfitRows();
        // Do not include growing category-cache counts — that forced rebuilds every frame during scans.
        var signature =
            $"{outfitFilter}|{showMissingOnly}|{showOwnedOnly}|{(int)sortMode}|{(int)categoryFilter}|{(int)storageFilter}|"
            + string.Join('|', rows.Select(r => $"{r.Key}:{r.Badge}:{r.Subtitle}"));
        if (!force && signature == lastBrowserListSignature)
            return;
        lastBrowserListSignature = signature;

        browserList.OptionsList = rows;
        browserList.Update();

        TrackerNativeListRow? select = null;
        if (!string.IsNullOrEmpty(selectedBrowserKey))
            select = rows.FirstOrDefault(r => r.Key == selectedBrowserKey);
        select ??= rows.FirstOrDefault();

        if (select != null)
        {
            var selectionChanged = selectedBrowserKey != select.Key;
            selectedBrowserKey = select.Key;
            if (selectionChanged || rebuildDetail)
                RebuildBrowserDetail(select, force: selectionChanged || rebuildDetail);
        }
        else
        {
            selectedBrowserKey = string.Empty;
            ClearBrowserDetail(
                categoryFilter != OutfitCategoryFilter.All && categoryScanRunning
                    ? "Still checking where these sets come from — results will fill in shortly."
                    : "No outfit sets match your filters.");
        }
    }

    private List<TrackerNativeListRow> BuildOutfitRows()
    {
        IEnumerable<OutfitSetInfo> sets = plugin.OutfitSets.GetSets();

        if (!string.IsNullOrWhiteSpace(outfitFilter))
            sets = sets.Where(s => s.Name.Contains(outfitFilter, StringComparison.OrdinalIgnoreCase));

        // Missing = incomplete sets. Owned = any stored pieces (includes partial sets).
        if (showMissingOnly)
            sets = sets.Where(s => s.MissingPieces > 0);
        else if (showOwnedOnly)
            sets = sets.Where(s => s.OwnedPieceCount > 0);

        if (categoryFilter != OutfitCategoryFilter.All)
        {
            sets = sets.Where(s =>
                setCategoryCache.TryGetValue(s.SetId, out var cat) && cat == categoryFilter);
        }

        if (storageFilter != OutfitStorageFilter.All)
            sets = sets.Where(s => TrackerNativeHelpers.SetMatchesStorage(s, storageFilter));

        sets = sortMode switch
        {
            OutfitSortMode.Progress => sets
                .OrderByDescending(s => s.TotalPieces == 0 ? 0f : s.OwnedPieceCount / (float)s.TotalPieces)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            OutfitSortMode.MissingFirst => sets
                .OrderByDescending(s => s.MissingPieces)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            _ => sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
        };

        var rows = new List<TrackerNativeListRow>();
        foreach (var set in sets)
        {
            var (stored, missing, _) = TrackerNativeHelpers.SplitPiecesForFilter(
                set,
                storageFilter,
                IsGlamourPiece,
                plugin.CabinetCatalog.IsArmoireEligible);

            var iconPiece = missing.FirstOrDefault();
            if (iconPiece.ItemId == 0)
                iconPiece = stored.FirstOrDefault();
            if (iconPiece.ItemId == 0)
                iconPiece = set.Pieces.FirstOrDefault();

            var status = TrackerNativeHelpers.FormatSetCollectionStatus(
                set,
                storageFilter,
                IsGlamourPiece,
                plugin.CabinetCatalog.IsArmoireEligible);
            rows.Add(new TrackerNativeListRow
            {
                Key = $"set|{set.SetId}",
                Title = set.Name,
                Subtitle = status,
                IconId = TrackerNativeHelpers.ResolveItemIcon(iconPiece.ItemId),
                Badge = missing.Count == 0 ? "Complete" : $"{missing.Count} missing",
                BadgeColor = TrackerNativeHelpers.GetSetStatusColor(stored.Count, missing.Count),
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
        RebuildBrowserDetail(row, force: true);
    }

    private void RebuildBrowserDetail(TrackerNativeListRow row, bool force)
    {
        if (browserDetail == null || row.OutfitSet == null)
            return;

        var set = row.OutfitSet;
        var loaded = setAcquireLoaded.ContainsKey(set.SetId);
        // Only rebuild when the selected set / load state / ownership changes — not on global cache growth.
        var detailKey = $"{row.Key}|{set.OwnedPieceCount}|{set.MissingPieces}|{loaded}|{(int)storageFilter}|{detailRebuildEpoch}";
        if (!force && detailKey == lastBrowserDetailKey)
            return;
        lastBrowserDetailKey = detailKey;

        var list = browserDetail.ContentNode;
        list.Clear();
        var width = MathF.Max(120f, browserDetail.Width - 18f);

        BuildOutfitDetail(list, set, width);

        list.RecalculateLayout();
        browserDetail.RecalculateSizes();
        if (!suppressDetailScrollTop)
            browserDetail.ScrollToTop();
        suppressDetailScrollTop = false;

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
        browserDetail.RecalculateSizes();
    }

}
