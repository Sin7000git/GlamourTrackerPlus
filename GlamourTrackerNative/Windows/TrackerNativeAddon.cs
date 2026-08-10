using System.Collections.Concurrent;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GlamourTracker.Windows;

/// <summary>
/// Main window: Overview, Outfit sets, Settings.
/// Fashion Report opens via Overview button or <see cref="FashionReportNativeAddon"/>.
/// Plate randomize lives on the plate-editor ImGui overlay (not a main-window tab).
/// </summary>
internal sealed partial class TrackerNativeAddon : NativeAddon
{
    private const float TabH = 28f;
    private const float Gap = 6f;
    private const float RowH = 28f;
    private const float ToolbarH = 64f;

    private const int AcquireRetryCooldownMinutes = 5;
    private const float OverviewColumnGap = 20f;

    internal const string TabOverview = "Overview";
    internal const string TabOutfitSets = "Outfit sets";
    internal const string TabSettings = "Settings";

    private readonly Plugin plugin;
    private readonly ConcurrentDictionary<uint, OutfitCategoryFilter> setCategoryCache = new();
    private readonly ConcurrentDictionary<uint, FashionResolvedItem> itemAcquireCache = new();
    private readonly ConcurrentDictionary<uint, byte> setAcquireLoaded = new();
    private readonly ConcurrentDictionary<uint, byte> setAcquireInFlight = new();
    private readonly ConcurrentDictionary<uint, byte> setAcquirePendingUi = new();
    private readonly ConcurrentDictionary<uint, DateTime> setAcquireRetryAfter = new();
    private readonly HashSet<string> expandedPieceKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? categoryScanCts;
    private CancellationTokenSource? windowCts;
    private bool categoryScanRunning;
    private int detailRebuildEpoch;
    private bool suppressDetailScrollTop;

    private TabBarNode? tabBar;
    private ScrollingNode<VerticalListNode>? formScroll;
    private ResNode? browserToolbar;
    private SearchInputNode? outfitFilterInput;
    private CheckboxNode? missingOnlyCheckbox;
    private CheckboxNode? ownedOnlyCheckbox;
    private StringDropDownNode? sortDropDown;
    private StringDropDownNode? categoryDropDown;
    private StringDropDownNode? storageDropDown;
    private ListNode<TrackerNativeListRow, TrackerNativeListItemNode>? browserList;
    private ScrollingNode<VerticalListNode>? browserDetail;

    private string selectedTab = TabOverview;
    private string? pendingSelectTab;
    private string outfitFilter = string.Empty;
    private bool showMissingOnly;
    private bool showOwnedOnly;
    private OutfitSortMode sortMode = OutfitSortMode.Name;
    private OutfitCategoryFilter categoryFilter = OutfitCategoryFilter.All;
    private OutfitStorageFilter storageFilter = OutfitStorageFilter.All;
    private string selectedBrowserKey = string.Empty;
    private string lastFormSignature = string.Empty;
    private string lastBrowserListSignature = string.Empty;
    private string lastBrowserDetailKey = string.Empty;

    private Vector2 bodyOrigin;
    private Vector2 bodySize;
    private float listW;
    private float detailW;

    public TrackerNativeAddon(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void OpenSettingsTab()
    {
        pendingSelectTab = TabSettings;
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!IsOpen)
                Open();
            else
                ApplyPendingTab();
        });
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        var origin = ContentStartPosition;
        var content = ContentSize;
        bodyOrigin = new Vector2(origin.X, origin.Y + TabH + Gap);
        bodySize = new Vector2(content.X, content.Y - TabH - Gap);
        listW = MathF.Floor(content.X * 0.42f);
        detailW = content.X - listW - Gap;

        tabBar = new TabBarNode
        {
            Position = origin,
            Size = new Vector2(content.X, TabH),
        };
        tabBar.AttachNode(this);
        tabBar.AddTab(TabOverview, () => SelectTab(TabOverview));
        tabBar.AddTab(TabOutfitSets, () => SelectTab(TabOutfitSets));
        tabBar.AddTab(TabSettings, () => SelectTab(TabSettings));

        formScroll = new ScrollingNode<VerticalListNode>
        {
            Position = bodyOrigin,
            Size = bodySize,
            AutoHideScrollBar = true,
            ScrollSpeed = 28,
        };
        formScroll.ContentNode.FitContents = true;
        formScroll.ContentNode.FitWidth = true;
        formScroll.ContentNode.ItemSpacing = 4f;
        formScroll.AttachNode(this);

        browserToolbar = new ResNode
        {
            Position = bodyOrigin,
            Size = new Vector2(content.X, ToolbarH),
            IsVisible = false,
        };
        browserToolbar.AttachNode(this);
        BuildBrowserToolbar(content.X);

        var browserBodyY = bodyOrigin.Y + ToolbarH + 2f;
        var browserBodyH = bodySize.Y - ToolbarH - 2f;

        browserList = new ListNode<TrackerNativeListRow, TrackerNativeListItemNode>
        {
            Position = new Vector2(bodyOrigin.X, browserBodyY),
            Size = new Vector2(listW, browserBodyH),
            OptionsList = [],
            AutoResetScroll = false,
            ScrollBarWidth = 8f,
            NoResultsString = "No outfit sets match.",
            OnItemSelected = OnBrowserRowSelected,
            IsVisible = false,
        };
        // Grabber length (vertical), not bar thickness — keeps long set lists clickable.
        browserList.ScrollBarNode.MinThumbHeight = 48;
        browserList.AttachNode(this);

        browserDetail = new ScrollingNode<VerticalListNode>
        {
            Position = new Vector2(bodyOrigin.X + listW + Gap, browserBodyY),
            Size = new Vector2(detailW, browserBodyH),
            AutoHideScrollBar = true,
            ScrollSpeed = 28,
            ScrollBarWidth = 8f,
            IsVisible = false,
        };
        browserDetail.ScrollBarNode.MinThumbHeight = 48;
        browserDetail.ContentNode.FitContents = true;
        browserDetail.ContentNode.FitWidth = true;
        browserDetail.ContentNode.ItemSpacing = 3f;
        browserDetail.AttachNode(this);

        var openTab = pendingSelectTab ?? selectedTab;
        pendingSelectTab = null;
        selectedTab = openTab;
        tabBar.SelectTab(openTab);
        ApplyLayout();
        RefreshActiveTab(force: true);
        if (openTab == TabOutfitSets)
            _ = ScanAllSetCategoriesAsync();
    }

    protected override unsafe void OnShow(AtkUnitBase* addon)
    {
        base.OnShow(addon);
        plugin.RefreshAll(true);
        lastFormSignature = string.Empty;
        lastBrowserListSignature = string.Empty;
        lastBrowserDetailKey = string.Empty;
        // RefreshAll may fill dresser set completes after the first paint — force a rebuild.
        ScheduleRebuildForm();
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);
        ApplyPendingTab();
        RefreshActiveTab(force: false);
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        categoryScanCts?.Cancel();
        categoryScanCts?.Dispose();
        categoryScanCts = null;
        windowCts?.Cancel();
        windowCts?.Dispose();
        windowCts = null;
        expandedPieceKeys.Clear();
        tabBar = null;
        formScroll = null;
        browserToolbar = null;
        outfitFilterInput = null;
        missingOnlyCheckbox = null;
        ownedOnlyCheckbox = null;
        sortDropDown = null;
        categoryDropDown = null;
        storageDropDown = null;
        browserList = null;
        browserDetail = null;
        lastFormSignature = string.Empty;
        lastBrowserListSignature = string.Empty;
        lastBrowserDetailKey = string.Empty;
    }

    private void ApplyPendingTab()
    {
        if (pendingSelectTab == null || tabBar == null)
            return;

        var tab = pendingSelectTab;
        pendingSelectTab = null;
        SelectTab(tab);
        tabBar.SelectTab(tab);
    }

    private void SelectTab(string tab)
    {
        if (selectedTab == tab)
            return;

        selectedTab = tab;
        selectedBrowserKey = string.Empty;
        lastBrowserDetailKey = string.Empty;
        expandedPieceKeys.Clear();
        ApplyLayout();
        RefreshActiveTab(force: true);
        if (tab == TabOutfitSets)
            _ = ScanAllSetCategoriesAsync();
    }

    private bool IsBrowserTab => selectedTab == TabOutfitSets;

    private void ApplyLayout()
    {
        var browser = IsBrowserTab;
        var toolbarH = browser ? ToolbarH + 2f : 0f;

        if (formScroll != null)
            formScroll.IsVisible = !browser;

        if (browserToolbar != null)
            browserToolbar.IsVisible = browser;

        if (browserList != null)
        {
            browserList.IsVisible = browser;
            browserList.Position = new Vector2(bodyOrigin.X, bodyOrigin.Y + toolbarH);
            var listSize = new Vector2(listW, bodySize.Y - toolbarH);
            // Avoid Size churn — OnSizeChanged rebuilds scrollbar params and can hitch while dragging.
            if (browserList.Size != listSize)
                browserList.Size = listSize;
        }

        if (browserDetail != null)
        {
            browserDetail.IsVisible = browser;
            browserDetail.Position = new Vector2(bodyOrigin.X + listW + Gap, bodyOrigin.Y + toolbarH);
            var detailSize = new Vector2(detailW, bodySize.Y - toolbarH);
            if (browserDetail.Size != detailSize)
                browserDetail.Size = detailSize;
        }
    }

    private void RefreshActiveTab(bool force)
    {
        if (IsBrowserTab)
        {
            RefreshBrowserList(force);
            return;
        }

        // Overview/Settings: RebuildForm no-ops when the stats signature is unchanged, so this
        // stays cheap every frame but still picks up dresser/armoire counts after a refresh.
        RebuildForm(force);
    }

    // ── Form tabs ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuild on the next tick — disposing nodes inside a button/checkbox click handler
    /// crashes the game (use-after-free in ComponentNode.OnReceiveEvent).
    /// </summary>
    private void ScheduleRebuildForm()
    {
        _ = Plugin.Framework.RunOnTick(() =>
        {
            if (!IsOpen)
                return;
            RebuildForm(force: true);
        }, delayTicks: 1);
    }

    /// <summary>Called from Plugin.RefreshAll so Overview picks up dresser/armoire resyncs.</summary>
    internal void RequestFormRebuild()
    {
        _ = Plugin.Framework.RunOnTick(() =>
        {
            if (!IsOpen)
                return;

            // Signature no-op when counts unchanged — avoids rebuilding every background refresh.
            RebuildForm(force: false);
        }, delayTicks: 1);
    }

    private void RebuildForm(bool force)
    {
        if (formScroll == null)
            return;

        var signature = BuildFormSignature();
        if (!force && signature == lastFormSignature)
            return;
        lastFormSignature = signature;

        var list = formScroll.ContentNode;
        list.Clear();
        var width = MathF.Max(160f, formScroll.Width - 18f);

        switch (selectedTab)
        {
            case TabOverview:
                BuildOverview(list, width);
                break;
            case TabSettings:
                BuildSettings(list, width);
                break;
        }

        list.RecalculateLayout();
        formScroll.RecalculateSizes();
    }

    /// <summary>
    /// Layout-only signature for form tabs. Do not include every checkbox/slider value —
    /// otherwise OnUpdate rebuilds the whole tab on each click (visible flicker).
    /// </summary>
    private string BuildFormSignature()
    {
        var index = plugin.OwnershipIndex;
        var c = plugin.Configuration;
        return selectedTab switch
        {
            TabOverview =>
                BuildOverviewSignature(index),
            TabSettings =>
                $"st|{c.ShowPlateEditorOverlay}",
            _ => selectedTab,
        };
    }

}
