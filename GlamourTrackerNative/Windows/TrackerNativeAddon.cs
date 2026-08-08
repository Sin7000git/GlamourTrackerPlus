using System.Collections.Concurrent;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

/// <summary>
/// Main window: Overview, Outfit sets, Settings.
/// Fashion Report opens via Overview button or <see cref="FashionReportNativeAddon"/>.
/// Plate randomize lives on the plate-editor ImGui overlay (not a main-window tab).
/// </summary>
internal sealed class TrackerNativeAddon : NativeAddon
{
    private const float TabH = 28f;
    private const float Gap = 6f;
    private const float RowH = 28f;
    private const float ToolbarH = 64f;

    private const int AcquireRetryCooldownMinutes = 5;
    private const float OverviewLabelWidth = 175f;
    private const float OverviewStatRowH = 22f;
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

    private string BuildOverviewSignature(GlamourOwnershipIndex index)
    {
        var sets = plugin.OutfitSets.GetOverviewStats();
        var progress = plugin.FashionProgress.GetProgress();
        var week = plugin.FashionReport.Snapshot?.Week ?? string.Empty;

        return $"ov|{index.DresserSlotsUsed}|{index.DresserUniqueCount}|{index.ArmoireCount}|{index.HasPersistedData}|{index.LastRefresh.Ticks}|{sets.DresserEligible}|{sets.ArmoireEligible}|{sets.SetsInDresser}|{sets.SetsInArmoire}|{sets.CompletedInDresser}|{sets.CompletedInArmoire}|{week}|{(int)progress.Kind}|{progress.HighestScore}";
    }

    private void BuildOverview(VerticalListNode list, float width)
    {
        var index = plugin.OwnershipIndex;
        var setStats = plugin.OutfitSets.GetOverviewStats();
        var progress = plugin.FashionProgress.GetProgress();
        var snap = plugin.FashionReport.Snapshot;

        // —— Top: Fashion Report (full width) ——
        list.AddNode(MakeSection("Fashion Report"));
        var weekLine = snap != null ? $"Week {snap.Week}" : "Not loaded yet";
        list.AddNode(MakeOverviewStatRow("This week", weekLine, width));
        var (progressColor, progressText) = FormatOverviewFashionProgress(progress);
        list.AddNode(MakeOverviewStatRow("Judging", progressText, width, progressColor));

        var frActions = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
            X = TrackerNativeHelpers.Indent,
        };
        frActions.AddNode(new TextButtonNode
        {
            Size = new Vector2(180f, RowH),
            String = "Open Fashion Report",
            TextTooltip = "Same as /glamplus report.",
            OnClick = () => plugin.OpenFashionReportTab(),
        });
        list.AddNode(frActions);

        list.AddNode(new HorizontalLineNode { Size = new Vector2(width, 2f) });

        // —— Two columns: storage | outfit sets ——
        var colW = MathF.Floor((width - OverviewColumnGap) * 0.5f);
        var leftCol = new VerticalListNode
        {
            Size = new Vector2(colW, 1f),
            FitContents = true,
            FitWidth = true,
            ItemSpacing = 3f,
        };
        var rightCol = new VerticalListNode
        {
            Size = new Vector2(colW, 1f),
            FitContents = true,
            FitWidth = true,
            ItemSpacing = 3f,
        };

        leftCol.AddNode(MakeSection("Stored"));
        leftCol.AddNode(MakeOverviewStatRow(
            "Dresser slots",
            $"{index.DresserSlotsUsed} / 800",
            colW));
        leftCol.AddNode(MakeOverviewStatRow("Unique items in dresser", $"{index.DresserUniqueCount}", colW));
        leftCol.AddNode(MakeOverviewStatRow("Unique items in armoire", $"{index.ArmoireCount}", colW));
        var dataNote = index.HasPersistedData
            ? index.LastRefresh == DateTime.MinValue
                ? "Showing your last saved dresser/armoire list"
                : $"Last updated {index.LastRefresh.ToLocalTime():g}"
            : "No saved list yet — open your dresser or armoire once";
        leftCol.AddNode(MakeMutedIndented(dataNote, colW));
        leftCol.RecalculateLayout();

        rightCol.AddNode(MakeSection("Outfit sets"));
        rightCol.AddNode(MakeOverviewStatRow(
            "Completed in dresser",
            FormatOwnedRatio(setStats.CompletedInDresser, setStats.SetsInDresser),
            colW,
            setStats.CompletedInDresser > 0 ? TrackerNativeHelpers.ColorOk : TrackerNativeHelpers.ColorMuted));
        rightCol.AddNode(MakeOverviewStatRow(
            "Completed in armoire",
            FormatOwnedRatio(setStats.CompletedInArmoire, setStats.SetsInArmoire),
            colW,
            setStats.CompletedInArmoire > 0 ? TrackerNativeHelpers.ColorOk : TrackerNativeHelpers.ColorMuted));
        rightCol.AddNode(MakeOverviewStatRow(
            "Total sets in dresser",
            FormatRatio(setStats.SetsInDresser, setStats.DresserEligible),
            colW));
        rightCol.AddNode(MakeOverviewStatRow(
            "Total sets in armoire",
            FormatRatio(setStats.SetsInArmoire, setStats.ArmoireEligible),
            colW));
        rightCol.RecalculateLayout();

        var columnsH = MathF.Max(leftCol.Height, rightCol.Height);
        var columns = new ResNode { Size = new Vector2(width, columnsH) };
        leftCol.Position = Vector2.Zero;
        rightCol.Position = new Vector2(colW + OverviewColumnGap, 0f);
        leftCol.AttachNode(columns);
        rightCol.AttachNode(columns);
        list.AddNode(columns);

        list.AddNode(new HorizontalLineNode { Size = new Vector2(width, 2f) });

        // —— Actions ——
        var buttons = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
            X = TrackerNativeHelpers.Indent,
        };
        buttons.AddNode(new TextButtonNode
        {
            Size = new Vector2(120f, RowH),
            String = "Refresh now",
            OnClick = () =>
            {
                plugin.RefreshAll(true);
                ScheduleRebuildForm();
            },
        });
        buttons.AddNode(new TextButtonNode
        {
            Size = new Vector2(140f, RowH),
            String = "Clear saved data",
            TextTooltip =
                "Deletes saved dresser/armoire ownership. Counts stay at zero until you open the dresser or armoire again.",
            OnClick = () => plugin.ClearSavedOwnership(),
        });
        list.AddNode(buttons);
    }

    private static string FormatRatio(int have, int total) =>
        total > 0 ? $"{have} / {total}" : "—";

    /// <summary>Completed / owned ratios collapse to a dash when nothing is owned yet.</summary>
    private static string FormatOwnedRatio(int completedOrOwned, int ownedOrEligible) =>
        ownedOrEligible > 0 ? $"{completedOrOwned} / {ownedOrEligible}" : "—";

    private static (Vector4 Color, string Text) FormatOverviewFashionProgress(FashionReportProgressView progress) =>
        progress.Kind switch
        {
            FashionReportProgressKind.Complete =>
                (TrackerNativeHelpers.ColorOk, $"Complete · Score {progress.HighestScore}"),
            FashionReportProgressKind.Incomplete =>
                (TrackerNativeHelpers.ColorWarn, $"Score {progress.HighestScore} · Keep going"),
            FashionReportProgressKind.Unknown =>
                (TrackerNativeHelpers.ColorMuted, "Talk to the Masked Rose to sync"),
            _ =>
                (TrackerNativeHelpers.ColorMuted, "Judging closed"),
        };

    private static ResNode MakeOverviewStatRow(
        string label,
        string value,
        float width,
        Vector4? valueColor = null)
    {
        var row = new ResNode { Size = new Vector2(width, OverviewStatRowH) };
        var labelX = TrackerNativeHelpers.Indent;
        var labelNode = MakeText(label, 13, TrackerNativeHelpers.ColorMuted, OverviewLabelWidth, 18f);
        labelNode.Position = new Vector2(labelX, 2f);
        labelNode.AttachNode(row);

        var valueX = labelX + OverviewLabelWidth + 6f;
        var valueW = MathF.Max(48f, width - valueX - 4f);
        var valueNode = MakeText(value, 13, valueColor ?? TrackerNativeHelpers.ColorTitle, valueW, 18f);
        valueNode.Position = new Vector2(valueX, 2f);
        valueNode.AttachNode(row);

        return row;
    }

    private static TextNode MakeMutedIndented(string text, float width) =>
        new()
        {
            Size = new Vector2(width - TrackerNativeHelpers.Indent, 16f),
            X = TrackerNativeHelpers.Indent,
            FontSize = 11,
            TextColor = TrackerNativeHelpers.ColorMuted,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

    private void BuildSettings(VerticalListNode list, float width)
    {
        var config = plugin.Configuration;

        list.AddNode(MakeSection("General"));
        list.AddNode(MakeCheckbox("Enable plugin", config.Enabled, v =>
        {
            config.Enabled = v;
            config.Save();
            if (!v)
                plugin.RestoreTooltipEnhancements();
        }));

        list.AddNode(MakeSection("Item tooltips"));
        list.AddNode(MakeCheckbox("Color-code dresser/armoire icons", config.ShowTooltipIcons, v =>
        {
            config.ShowTooltipIcons = v;
            config.Save();
        }));

        list.AddNode(MakeSection("Grand Company delivery"));
        list.AddNode(MakeCheckbox("Show dresser/armoire icons", config.ShowGcExpertDeliveryStatus, v =>
        {
            config.ShowGcExpertDeliveryStatus = v;
            config.Save();
        }));

        list.AddNode(MakeSection("Plate editor"));
        list.AddNode(MakeCheckbox("Show controls above plate editor", config.ShowPlateEditorOverlay, v =>
        {
            config.ShowPlateEditorOverlay = v;
            config.Save();
            // Nested "Place on the right" appears/disappears — rebuild next tick only.
            ScheduleRebuildForm();
        }));
        if (config.ShowPlateEditorOverlay)
        {
            list.AddNode(MakeIndentedCheckbox(
                "Place on the right",
                config.PlateEditorOverlayOnRight,
                v =>
                {
                    config.PlateEditorOverlayOnRight = v;
                    config.Save();
                },
                width));
        }

        list.AddNode(MakeCheckbox("Show reroll next to each slot", config.ShowSlotRerollButtons, v =>
        {
            config.ShowSlotRerollButtons = v;
            config.Save();
        }));
#if GLAMOUR_DEV
        list.AddNode(MakeMuted(
            "Fine-tune positions via /glamplus imgui → Settings → Slot button positions.",
            width));
#else
        list.AddNode(MakeMuted(
            "Slot button positions use built-in defaults.",
            width));
#endif
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

    private void BuildOutfitDetail(VerticalListNode list, OutfitSetInfo set, float width)
    {
        var (storedPieces, missingPieces, total) = TrackerNativeHelpers.SplitPiecesForFilter(
            set,
            storageFilter,
            IsGlamourPiece,
            plugin.CabinetCatalog.IsArmoireEligible);

        list.AddNode(MakeText(set.Name, 16, TrackerNativeHelpers.ColorTitle, width, 22f));

        if (total == 0)
        {
            list.AddNode(MakeMuted(
                storageFilter == OutfitStorageFilter.Dresser
                    ? "No dresser pieces in this set."
                    : storageFilter == OutfitStorageFilter.Armoire
                        ? "No armoire pieces in this set."
                        : "No pieces in this set.",
                width));
            return;
        }

        list.AddNode(MakeMuted("Expand a piece for sources. Try on previews it.", width));

        list.AddNode(MakeText(
            $"{storedPieces.Count}/{total} stored",
            13,
            TrackerNativeHelpers.GetSetStatusColor(storedPieces.Count, missingPieces.Count),
            width,
            18f));

        foreach (var piece in storedPieces)
            AddOutfitPieceRow(list, set, piece, width);

        if (missingPieces.Count > 0)
        {
            list.AddNode(MakeText(
                $"{missingPieces.Count}/{total} missing",
                13,
                TrackerNativeHelpers.ColorMissing,
                width,
                18f));

            foreach (var piece in missingPieces)
                AddOutfitPieceRow(list, set, piece, width);
        }
    }

    private void AddOutfitPieceRow(VerticalListNode list, OutfitSetInfo set, OutfitPieceInfo piece, float width)
    {
        var name = TrackerNativeHelpers.ResolveItemName(piece.ItemId);
        var status = TrackerNativeHelpers.FormatStorage(piece.Storage);
        var pieceKey = PieceKey(set.SetId, piece);
        var expanded = expandedPieceKeys.Contains(pieceKey);
        var iconId = TrackerNativeHelpers.ResolveItemIcon(piece.ItemId);

        const float iconSize = 28f;
        const float iconGap = 4f;
        var headerWidth = iconId != 0
            ? MathF.Max(120f, width - iconSize - iconGap)
            : width;
        var contentWidth = MathF.Max(80f, headerWidth - 8f);

        var row = new HorizontalListNode
        {
            Size = new Vector2(width, iconSize),
            ItemSpacing = iconGap,
            FitToContentHeight = true,
        };

        if (iconId != 0)
        {
            var pieceIcon = new IconImageNode
            {
                Size = new Vector2(iconSize, iconSize),
                TextureSize = new Vector2(iconSize, iconSize),
                IconId = iconId,
                ImageNodeFlags = ImageNodeFlags.AutoFit,
            };
            // Native item detail tooltip (same as inventory hover).
            if (piece.ItemId != 0)
                pieceIcon.ItemTooltip = piece.ItemId;
            row.AddNode(pieceIcon);
        }

        var header = new CollapsingHeaderNode
        {
            Size = new Vector2(headerWidth, 28f),
            String = $"{piece.SlotLabel}: {name} — {status}",
            FitWidth = true,
            IsCollapsed = !expanded,
            ItemSpacing = 3f,
        };

        var tryOn = new TextButtonNode
        {
            Size = new Vector2(MathF.Min(120f, contentWidth), RowH),
            String = "Try on",
            OnClick = () => TryOnItem(piece.ItemId, name),
        };
        header.AddNode(tryOn);

        if (itemAcquireCache.TryGetValue(piece.ItemId, out var acquired))
        {
            if (!string.IsNullOrWhiteSpace(acquired.Summary))
                header.AddNode(MakeMuted(acquired.Summary, contentWidth));

            foreach (var costLine in EnumerateAcquireCosts(acquired))
            {
                header.AddNode(MakeText(
                    costLine,
                    12,
                    TrackerNativeHelpers.ColorInfo,
                    contentWidth,
                    16f));
            }

            foreach (var section in acquired.Sections)
                AddAcquireSection(header, section, acquired, contentWidth);
            if (acquired.Sections.Count == 0 && string.IsNullOrWhiteSpace(acquired.Summary))
                header.AddNode(MakeMuted("No source data for this piece.", contentWidth));
        }
        else if (setAcquireRetryAfter.ContainsKey(set.SetId))
        {
            header.AddNode(MakeMuted("Couldn't load sources. Reopen this set to try again.", contentWidth));
        }
        else if (!setAcquireLoaded.ContainsKey(set.SetId))
        {
            header.AddNode(MakeMuted("Loading sources…", contentWidth));
        }
        else
        {
            header.AddNode(MakeMuted("No source data for this piece.", contentWidth));
        }

        // Track expand state only — never rebuild the tree from OnToggle (that flickers the headers).
        header.OnToggle = visible =>
        {
            if (visible)
                expandedPieceKeys.Add(pieceKey);
            else
                expandedPieceKeys.Remove(pieceKey);

            RelayoutBrowserDetail();

            if (visible && NeedsAcquireLoad(set.SetId))
                _ = LoadSetAcquireAsync(set, refreshUi: true, WindowToken);
        };

        row.AddNode(header);
        list.AddNode(row);
    }

    /// <summary>Distinct buy/exchange cost lines (gil, tomestones, seals, etc.).</summary>
    private static IEnumerable<string> EnumerateAcquireCosts(FashionResolvedItem item)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in item.Sections)
        {
            if (!IsCostSection(section.Type) || string.IsNullOrWhiteSpace(section.Headline))
                continue;

            var line = section.Headline!;
            // Avoid duplicating the same cost already present in the summary line.
            if (!string.IsNullOrWhiteSpace(item.Summary)
                && item.Summary.Contains(line, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen.Add(line))
                yield return line;
        }

        if (item.PreferredVendor is { Gil: > 0 } vendor)
        {
            var gilLine = $"Cost: {vendor.Gil:N0} gil";
            if ((string.IsNullOrWhiteSpace(item.Summary)
                 || !item.Summary.Contains(gilLine, StringComparison.OrdinalIgnoreCase))
                && seen.Add(gilLine))
            {
                yield return gilLine;
            }
        }
    }

    private static bool IsCostSection(string type) =>
        type.Equals("vendor", StringComparison.OrdinalIgnoreCase)
        || type.Equals("barter", StringComparison.OrdinalIgnoreCase)
        || type.Equals("gc", StringComparison.OrdinalIgnoreCase);

    private void RelayoutBrowserDetail()
    {
        if (browserDetail == null)
            return;
        browserDetail.ContentNode.RecalculateLayout();
        browserDetail.RecalculateSizes();
    }

    private static string PieceKey(uint setId, OutfitPieceInfo piece) =>
        $"{setId}|{piece.SlotIndex}|{piece.ItemId}";

    private void TryOnItem(uint itemId, string name)
    {
        if (itemId == 0)
            return;

        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (!AgentTryon.TryOn(0, itemId))
                    Plugin.ChatGui.PrintError($"[Glamour Tracker+] Could not try on {name}.");
            }
            catch (Exception ex)
            {
                PluginFileLog.Error("outfit.tryon", $"Try on failed for {name} ({itemId})", ex);
                Plugin.ChatGui.PrintError($"[Glamour Tracker+] Could not try on {name}.");
            }
        });
    }

    private void AddAcquireSection(
        LayoutListNode list,
        FashionAcquireSection section,
        FashionResolvedItem item,
        float width)
    {
        list.AddNode(MakeText(
            section.Label,
            12,
            FashionReportNativeHelpers.TagColor(section.Type),
            width,
            16f));

        // Cost headlines are shown above via EnumerateAcquireCosts; skip repeating them here.
        if (!string.IsNullOrWhiteSpace(section.Headline)
            && !IsCostSection(section.Type)
            && !FashionReportNativeHelpers.IsRedundantSummary(section.Headline, item))
        {
            list.AddNode(MakeMuted(section.Headline!, width));
        }

        if (section.Type.Equals("duty_drop", StringComparison.OrdinalIgnoreCase))
        {
            var duties = new List<string>();
            if (!string.IsNullOrWhiteSpace(section.Headline))
                duties.Add(section.Headline!);
            duties.AddRange(section.Lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            foreach (var duty in duties.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dutyName = duty;
                list.AddNode(new TextButtonNode
                {
                    Size = new Vector2(MathF.Min(width, 260f), RowH),
                    String = $"Open: {Truncate(dutyName, 28)}",
                    TextTooltip = dutyName,
                    OnClick = () => _ = Plugin.Framework.RunOnFrameworkThread(() =>
                        OutfitDutyTravel.TryOpenDuty(dutyName, Plugin.DataManager, Plugin.ChatGui)),
                });
            }

            return;
        }

        if (section.Type.Equals("craft", StringComparison.OrdinalIgnoreCase) && item.ItemId != 0)
        {
            list.AddNode(new TextButtonNode
            {
                Size = new Vector2(MathF.Min(width, 200f), RowH),
                String = "Open Crafting Log",
                OnClick = () => TryOpenCraftingLog(item.ItemId, item.Name),
            });
        }

        foreach (var line in section.Lines)
        {
            if (FashionReportNativeHelpers.LineDuplicatesHeadline(line, section.Headline))
                continue;

            if (FashionReportNativeHelpers.HasMapCoordinates(line))
            {
                var target = line;
                list.AddNode(MakeVendorRow(Truncate(line, 42), target, width));
            }
            else
            {
                list.AddNode(MakeMuted(line, width));
            }
        }
    }

    private ResNode MakeVendorRow(string label, string teleportTarget, float width)
    {
        const float buttonW = 96f;
        var row = new ResNode { Size = new Vector2(width, RowH) };
        var text = MakeText(label, 12, Vector4.One, Math.Max(40f, width - buttonW - 8f), 18f);
        text.Position = new Vector2(0f, 5f);
        text.AttachNode(row);
        var teleport = new TextButtonNode
        {
            Position = new Vector2(width - buttonW, 0f),
            Size = new Vector2(buttonW, RowH),
            String = "Teleport",
            OnClick = () => plugin.VendorTravel.TeleportNearLocation(teleportTarget),
        };
        teleport.AttachNode(row);
        return row;
    }

    private void TryOpenCraftingLog(uint itemId, string name)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                try
                {
                    if (!plugin.RecipeLookup.TryGetRecipeId(itemId, out var recipeId))
                    {
                        Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {name}.");
                        return;
                    }

                    var agent = AgentRecipeNote.Instance();
                    if (agent == null)
                    {
                        Plugin.ChatGui.PrintError("[Glamour Tracker+] Crafting Log is not available.");
                        return;
                    }

                    agent->OpenRecipeByRecipeId(recipeId);
                }
                catch (Exception ex)
                {
                    PluginFileLog.Error("outfit.craft", $"Open Crafting Log failed for {name}", ex);
                    Plugin.ChatGui.PrintError("[Glamour Tracker+] Could not open the Crafting Log.");
                }
            }
        });
    }

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

    // ── Shared node helpers ───────────────────────────────────────────────

    private static TextNode MakeSection(string text) =>
        MakeText(text, 14, TrackerNativeHelpers.ColorInfo, 400f, 20f);

    private static TextNode MakeText(string text, byte fontSize, Vector4 color, float width, float height) =>
        new()
        {
            Size = new Vector2(width, height),
            FontSize = fontSize,
            TextColor = color,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

    private static TextNode MakeMuted(string text, float width) =>
        MakeText(text, 11, TrackerNativeHelpers.ColorMuted, width, 16f);

    private static ResNode MakeStatLine(string label, string value, float width)
    {
        var row = new ResNode { Size = new Vector2(width, 20f) };
        var left = MakeText(label, 13, TrackerNativeHelpers.ColorMuted, width * 0.45f, 18f);
        left.Position = new Vector2(TrackerNativeHelpers.Indent, 1f);
        left.AttachNode(row);
        var right = MakeText(value, 13, TrackerNativeHelpers.ColorTitle, width * 0.5f, 18f);
        right.Position = new Vector2(width * 0.45f, 1f);
        right.AttachNode(row);
        return row;
    }

    private static ResNode MakeIndented(NodeBase child, float width)
    {
        var wrap = new ResNode
        {
            Size = new Vector2(width, child.Height > 0 ? child.Height : RowH),
        };
        child.Position = new Vector2(TrackerNativeHelpers.Indent, 0f);
        child.AttachNode(wrap);
        return wrap;
    }

    private static ResNode MakeIndentedCheckbox(string label, bool isChecked, Action<bool> onChanged, float width)
    {
        var cb = MakeCheckbox(label, isChecked, onChanged);
        return MakeIndented(cb, width);
    }

    private static CheckboxNode MakeCheckbox(string label, bool isChecked, Action<bool> onChanged)
    {
        var node = new CheckboxNode
        {
            Size = new Vector2(24f, 24f),
            String = label,
        };
        node.IsChecked = isChecked;
        node.OnClick = onChanged;
        return node;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
