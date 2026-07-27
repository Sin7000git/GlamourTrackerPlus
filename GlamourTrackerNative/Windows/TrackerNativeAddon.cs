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
/// Native main window: Overview, Outfit sets, Randomize, Settings.
/// Fashion Report stays in <see cref="FashionReportNativeAddon"/>.
/// </summary>
internal sealed class TrackerNativeAddon : NativeAddon
{
    private const float TabH = 28f;
    private const float Gap = 6f;
    private const float RowH = 28f;
    private const float ToolbarH = 64f;

    internal const string TabOverview = "Overview";
    internal const string TabOutfitSets = "Outfit sets";
    internal const string TabRandomize = "Randomize";
    internal const string TabSettings = "Settings";

    private readonly Plugin plugin;
    private readonly ConcurrentDictionary<uint, OutfitCategoryFilter> setCategoryCache = new();
    private readonly ConcurrentDictionary<uint, FashionResolvedItem> itemAcquireCache = new();
    private readonly ConcurrentDictionary<uint, byte> setAcquireLoaded = new();
    private readonly ConcurrentDictionary<uint, byte> setAcquireInFlight = new();
    private readonly ConcurrentDictionary<uint, byte> setAcquirePendingUi = new();
    private readonly HashSet<string> expandedPieceKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? categoryScanCts;
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
    private ListNode<TrackerNativeListRow, TrackerNativeListItemNode>? browserList;
    private ScrollingNode<VerticalListNode>? browserDetail;

    private string selectedTab = TabOverview;
    private string? pendingSelectTab;
    private string outfitFilter = string.Empty;
    private bool showMissingOnly;
    private bool showOwnedOnly;
    private OutfitSortMode sortMode = OutfitSortMode.Name;
    private OutfitCategoryFilter categoryFilter = OutfitCategoryFilter.All;
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
        tabBar.AddTab(TabRandomize, () => SelectTab(TabRandomize));
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
            NoResultsString = "No outfit sets match.",
            OnItemSelected = OnBrowserRowSelected,
            IsVisible = false,
        };
        browserList.AttachNode(this);

        browserDetail = new ScrollingNode<VerticalListNode>
        {
            Position = new Vector2(bodyOrigin.X + listW + Gap, browserBodyY),
            Size = new Vector2(detailW, browserBodyH),
            AutoHideScrollBar = true,
            ScrollSpeed = 28,
            IsVisible = false,
        };
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
        expandedPieceKeys.Clear();
        tabBar = null;
        formScroll = null;
        browserToolbar = null;
        outfitFilterInput = null;
        missingOnlyCheckbox = null;
        ownedOnlyCheckbox = null;
        sortDropDown = null;
        categoryDropDown = null;
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
            browserList.Size = new Vector2(listW, bodySize.Y - toolbarH);
        }

        if (browserDetail != null)
        {
            browserDetail.IsVisible = browser;
            browserDetail.Position = new Vector2(bodyOrigin.X + listW + Gap, bodyOrigin.Y + toolbarH);
            browserDetail.Size = new Vector2(detailW, bodySize.Y - toolbarH);
        }
    }

    private void RefreshActiveTab(bool force)
    {
        if (IsBrowserTab)
            RefreshBrowserList(force);
        else
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
            case TabRandomize:
                BuildRandomize(list, width);
                break;
            case TabSettings:
                BuildSettings(list, width);
                break;
        }

        list.RecalculateLayout();
        formScroll.RecalculateSizes();
    }

    private string BuildFormSignature()
    {
        var index = plugin.OwnershipIndex;
        var c = plugin.Configuration;
        return selectedTab switch
        {
            TabOverview =>
                $"ov|{index.DresserSlotsUsed}|{index.DresserUniqueCount}|{index.OutfitSetsInDresser}|{index.ArmoireCount}|{index.LastRefresh.Ticks}|{plugin.OutfitSets.CountSetsInArmoire()}",
            TabRandomize =>
                $"rz|{c.RandomizeIncludeDresser}|{c.RandomizeIncludeArmoire}|{(int)c.RandomizeJobFilter}|{c.RandomizeSpecificJobId}|{c.RandomizeLimitRequiredLevel}|{c.RandomizeMinRequiredLevel}|{c.RandomizeMaxRequiredLevel}|{c.RandomizeLimitItemLevel}|{c.RandomizeMinItemLevel}|{c.RandomizeMaxItemLevel}|{LocksSignature()}",
            TabSettings =>
                $"st|{c.Enabled}|{c.ShowTooltipIcons}|{c.ShowGcExpertDeliveryStatus}|{c.ShowOnlyForGlamourItems}|{c.ShowPlateEditorOverlay}|{c.PlateEditorOverlayOnRight}|{c.ShowSlotRerollButtons}",
            _ => selectedTab,
        };
    }

    private string LocksSignature()
    {
        GlamourPlateRandomizer.EnsureLockArray(plugin.Configuration);
        return string.Concat(plugin.Configuration.RandomizeLockedSlots.Select(l => l ? '1' : '0'));
    }

    private void BuildOverview(VerticalListNode list, float width)
    {
        var index = plugin.OwnershipIndex;

        list.AddNode(MakeSection("Storage"));
        list.AddNode(MakeStatLine(
            "Dresser",
            index.DresserSlotsUsed > 0 ? $"{index.DresserSlotsUsed} / 800" : "—",
            width));
        list.AddNode(MakeStatLine("Unique appearances", $"{index.DresserUniqueCount}", width));
        list.AddNode(MakeStatLine("Armoire pieces", $"{index.ArmoireCount}", width));
        list.AddNode(MakeStatLine("Data", index.HasPersistedData ? "Saved" : "Not saved yet", width));

        list.AddNode(MakeSection("Outfit sets"));
        list.AddNode(MakeStatLine("In dresser", $"{index.OutfitSetsInDresser}", width));
        list.AddNode(MakeStatLine("In armoire", $"{plugin.OutfitSets.CountSetsInArmoire()}", width));
        list.AddNode(MakeStatLine("Last refresh", index.LastRefresh.ToLocalTime().ToString("T"), width));

        list.AddNode(new HorizontalLineNode { Size = new Vector2(width, 2f) });

        var buttons = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
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
            TextTooltip = "Clears ownership cache. Open dresser or armoire, then Refresh.",
            OnClick = () =>
            {
                plugin.OwnershipIndex.ClearRuntimeCache();
                plugin.Configuration.CharacterCaches.Clear();
                plugin.Configuration.Save();
                plugin.RefreshAll(true);
                Plugin.ChatGui.Print(
                    "Glamour Tracker+ saved ownership cleared. Open your dresser or armoire, then Refresh.");
                ScheduleRebuildForm();
            },
        });
        list.AddNode(buttons);
    }

    private void BuildRandomize(VerticalListNode list, float width)
    {
        var config = plugin.Configuration;
        GlamourPlateRandomizer.EnsureLockArray(config);

        list.AddNode(MakeSection("Sources"));
        list.AddNode(MakeCheckbox("Use dresser items", config.RandomizeIncludeDresser, v =>
        {
            config.RandomizeIncludeDresser = v;
            config.Save();
            ScheduleRebuildForm();
        }));
        list.AddNode(MakeCheckbox("Use armoire items", config.RandomizeIncludeArmoire, v =>
        {
            config.RandomizeIncludeArmoire = v;
            config.Save();
            ScheduleRebuildForm();
        }));

        list.AddNode(MakeSection("Filters"));
        var jobModes = TrackerNativeHelpers.JobModeLabels.ToList();
        var modeIndex = Math.Clamp((int)config.RandomizeJobFilter, 0, jobModes.Count - 1);
        var jobModeDrop = new StringDropDownNode
        {
            Size = new Vector2(200f, RowH),
            Options = jobModes,
            SelectedOption = jobModes[modeIndex],
            MaxListOptions = 3,
        };
        jobModeDrop.OnOptionSelected = label =>
        {
            var idx = jobModes.IndexOf(label);
            if (idx < 0)
                return;
            config.RandomizeJobFilter = (RandomizeJobFilterMode)idx;
            config.Save();
            ScheduleRebuildForm();
        };
        list.AddNode(jobModeDrop);

        if (config.RandomizeJobFilter == RandomizeJobFilterMode.CurrentJob)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            var jobLine = "Current job unknown";
            if (player != null
                && Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(player.ClassJob.RowId, out var job))
            {
                jobLine = $"{job.Abbreviation.ExtractText()} — {job.Name.ExtractText()}";
            }

            list.AddNode(MakeIndentedText(jobLine, width));
        }
        else if (config.RandomizeJobFilter == RandomizeJobFilterMode.SpecificJob)
        {
            BuildJobPicker(list, config, width);
        }

        list.AddNode(MakeCheckbox("Limit by required level", config.RandomizeLimitRequiredLevel, v =>
        {
            config.RandomizeLimitRequiredLevel = v;
            config.Save();
            ScheduleRebuildForm();
        }));
        if (config.RandomizeLimitRequiredLevel)
        {
            list.AddNode(MakeLabeledSlider("Lowest", config.RandomizeMinRequiredLevel, 1, 100, v =>
            {
                config.RandomizeMinRequiredLevel = v;
                config.Save();
            }, width, indented: true));
            list.AddNode(MakeLabeledSlider("Highest", config.RandomizeMaxRequiredLevel, 1, 100, v =>
            {
                config.RandomizeMaxRequiredLevel = v;
                config.Save();
            }, width, indented: true));
        }

        list.AddNode(MakeCheckbox("Limit by item level", config.RandomizeLimitItemLevel, v =>
        {
            config.RandomizeLimitItemLevel = v;
            config.Save();
            ScheduleRebuildForm();
        }));
        if (config.RandomizeLimitItemLevel)
        {
            list.AddNode(MakeLabeledNumeric("Minimum", config.RandomizeMinItemLevel, 1, 9999, v =>
            {
                config.RandomizeMinItemLevel = v;
                config.Save();
            }, width, indented: true));
            list.AddNode(MakeLabeledNumeric("Maximum", config.RandomizeMaxItemLevel, 1, 9999, v =>
            {
                config.RandomizeMaxItemLevel = v;
                config.Save();
            }, width, indented: true));
        }

        list.AddNode(MakeSection("Slot locks"));
        list.AddNode(MakeMuted("Click a slot to lock or unlock it. Dimmed = locked.", width));
        var locks = config.RandomizeLockedSlots;
        for (var row = 0; row < 2; row++)
        {
            var rowNode = new HorizontalListNode
            {
                Size = new Vector2(width, 52f),
                ItemSpacing = 8f,
            };
            for (var col = 0; col < 6; col++)
            {
                var i = row * 6 + col;
                if (i >= GlamourPlateSlotMap.SlotCount)
                    break;
                var slot = i;
                var locked = locks[i];
                var iconBtn = new IconButtonNode(iconPadding: 1.5f)
                {
                    Size = new Vector2(48f, 48f),
                    IconId = GlamourPlateSlotMap.EmptySlotIcon(i),
                };
                ApplySlotLockVisual(iconBtn, slot, locked);
                // Do not RebuildForm here — disposing this button mid-click crashes the client.
                iconBtn.OnClick = () =>
                {
                    GlamourPlateRandomizer.EnsureLockArray(plugin.Configuration);
                    var nowLocked = !plugin.Configuration.RandomizeLockedSlots[slot];
                    plugin.Configuration.RandomizeLockedSlots[slot] = nowLocked;
                    plugin.Configuration.Save();
                    ApplySlotLockVisual(iconBtn, slot, nowLocked);
                    // Signature includes locks; keep form signature in sync without a rebuild.
                    lastFormSignature = BuildFormSignature();
                };
                rowNode.AddNode(iconBtn);
            }

            list.AddNode(rowNode);
        }

        var lockButtons = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
        };
        lockButtons.AddNode(new TextButtonNode
        {
            Size = new Vector2(100f, RowH),
            String = "Unlock all",
            OnClick = () =>
            {
                GlamourPlateRandomizer.EnsureLockArray(config);
                Array.Fill(config.RandomizeLockedSlots, false);
                config.Save();
                ScheduleRebuildForm();
            },
        });
        lockButtons.AddNode(new TextButtonNode
        {
            Size = new Vector2(100f, RowH),
            String = "Lock all",
            OnClick = () =>
            {
                GlamourPlateRandomizer.EnsureLockArray(config);
                Array.Fill(config.RandomizeLockedSlots, true);
                config.Save();
                ScheduleRebuildForm();
            },
        });
        list.AddNode(lockButtons);

        list.AddNode(MakeMuted(
            "Randomize and slot reroll run from the controls above the plate editor.",
            width));
    }

    private void BuildJobPicker(VerticalListNode list, Configuration config, float width)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var jobs = sheet
            .Where(j => j.RowId != 0)
            .Select(j =>
            {
                var abbr = j.Abbreviation.ExtractText();
                var name = j.Name.ExtractText();
                return (j.RowId, j.UIPriority, Label: string.IsNullOrWhiteSpace(abbr)
                    ? $"#{j.RowId}"
                    : $"{abbr} — {name}");
            })
            .Where(j => !j.Label.StartsWith('#'))
            .OrderBy(j => j.UIPriority)
            .ThenBy(j => j.Label)
            .ToList();

        if (jobs.Count == 0)
            return;

        var labels = jobs.Select(j => j.Label).ToList();
        var selected = jobs.FirstOrDefault(j => j.RowId == config.RandomizeSpecificJobId);
        if (selected.RowId == 0)
        {
            selected = jobs[0];
            config.RandomizeSpecificJobId = selected.RowId;
            config.Save();
        }

        var drop = new StringDropDownNode
        {
            Size = new Vector2(MathF.Min(280f, width - TrackerNativeHelpers.Indent), RowH),
            Options = labels,
            SelectedOption = selected.Label,
            MaxListOptions = 10,
        };
        drop.OnOptionSelected = label =>
        {
            var match = jobs.FirstOrDefault(j => j.Label == label);
            if (match.RowId == 0 || config.RandomizeSpecificJobId == match.RowId)
                return;
            config.RandomizeSpecificJobId = match.RowId;
            config.Save();
        };
        list.AddNode(MakeIndented(drop, width));
    }

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
            ScheduleRebuildForm();
        }));

        list.AddNode(MakeSection("Item tooltips"));
        list.AddNode(MakeCheckbox("Color-code dresser/armoire icons", config.ShowTooltipIcons, v =>
        {
            config.ShowTooltipIcons = v;
            config.Save();
        }));
        list.AddNode(MakeCheckbox("Only annotate glamour gear", config.ShowOnlyForGlamourItems, v =>
        {
            config.ShowOnlyForGlamourItems = v;
            config.Save();
        }));

        list.AddNode(MakeSection("Grand Company delivery"));
        list.AddNode(MakeCheckbox("Show dresser/armoire icons", config.ShowGcExpertDeliveryStatus, v =>
        {
            config.ShowGcExpertDeliveryStatus = v;
            config.Save();
            ScheduleRebuildForm();
        }));

        list.AddNode(MakeSection("Plate editor"));
        list.AddNode(MakeCheckbox("Show controls above plate editor", config.ShowPlateEditorOverlay, v =>
        {
            config.ShowPlateEditorOverlay = v;
            config.Save();
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
        list.AddNode(MakeMuted(
            "Adjust positions from the plate menu or ImGui Settings → Slot button positions.",
            width));
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
            $"{outfitFilter}|{showMissingOnly}|{showOwnedOnly}|{(int)sortMode}|{(int)categoryFilter}|"
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
            ClearBrowserDetail("No outfit sets match your filters.");
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
            var iconPiece = set.Pieces.FirstOrDefault(p => p.Storage == GlamourStorageLocation.None);
            if (iconPiece.ItemId == 0)
                iconPiece = set.Pieces.FirstOrDefault();

            var status = TrackerNativeHelpers.FormatSetCollectionStatus(set);
            rows.Add(new TrackerNativeListRow
            {
                Key = $"set|{set.SetId}",
                Title = set.Name,
                Subtitle = status,
                IconId = TrackerNativeHelpers.ResolveItemIcon(iconPiece.ItemId),
                Badge = set.MissingPieces == 0 ? "Complete" : $"{set.MissingPieces} missing",
                BadgeColor = TrackerNativeHelpers.GetSetStatusColor(set),
                OutfitSet = set,
            });
        }

        return rows;
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
        var detailKey = $"{row.Key}|{set.OwnedPieceCount}|{set.MissingPieces}|{loaded}|{detailRebuildEpoch}";
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

        if (!loaded)
            _ = LoadSetAcquireAsync(set, refreshUi: true);
    }

    private void BuildOutfitDetail(VerticalListNode list, OutfitSetInfo set, float width)
    {
        list.AddNode(MakeText(set.Name, 16, TrackerNativeHelpers.ColorTitle, width, 22f));
        list.AddNode(MakeText(
            TrackerNativeHelpers.FormatSetCollectionStatus(set),
            13,
            TrackerNativeHelpers.GetSetStatusColor(set),
            width,
            18f));

        var progress = set.TotalPieces == 0 ? 0f : set.OwnedPieceCount / (float)set.TotalPieces;
        list.AddNode(new ProgressBarNode
        {
            Size = new Vector2(width, 12f),
            Progress = progress,
        });

        list.AddNode(MakeMuted("Expand a piece for sources. Try on previews it.", width));

        foreach (var piece in set.Pieces)
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

                if (visible && !setAcquireLoaded.ContainsKey(set.SetId))
                    _ = LoadSetAcquireAsync(set, refreshUi: true);
            };

            row.AddNode(header);
            list.AddNode(row);
        }
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

    private static void ApplySlotLockVisual(IconButtonNode button, int slot, bool locked)
    {
        button.MultiplyColor = locked
            ? new Vector3(0.45f, 0.45f, 0.45f)
            : new Vector3(1f, 1f, 1f);
        button.TextTooltip = locked
            ? $"{GlamourPlateSlotMap.Labels[slot]} — locked (click to unlock)"
            : $"{GlamourPlateSlotMap.Labels[slot]} — unlocked (click to lock)";
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

    private async Task LoadSetAcquireAsync(OutfitSetInfo set, bool refreshUi)
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

                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var name = TrackerNativeHelpers.ResolveItemName(piece.ItemId);
                        if (name.StartsWith("Item #", StringComparison.Ordinal))
                            return;

                        var resolved = await plugin.FashionReport
                            .ResolveNamedItemAsync(name, CancellationToken.None)
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
            }

            var wantUi = refreshUi || setAcquirePendingUi.TryRemove(set.SetId, out _);
            if (wantUi)
                await RefreshSelectedSetDetailAsync(set.SetId).ConfigureAwait(false);
            else
                setAcquirePendingUi.TryRemove(set.SetId, out _);
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("outfit.acquire", $"Failed loading sources for set {set.SetId}", ex);
            setAcquireLoaded.TryAdd(set.SetId, 1);
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
        categoryScanCts = new CancellationTokenSource();
        var ct = categoryScanCts.Token;

        try
        {
            var sets = plugin.OutfitSets.GetSets()
                .Where(s => !setAcquireLoaded.ContainsKey(s.SetId))
                .ToList();

            var completed = 0;
            using var gate = new SemaphoreSlim(2);
            var tasks = sets.Select(async set =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await LoadSetAcquireAsync(set, refreshUi: false).ConfigureAwait(false);
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

    private static TextNode MakeIndentedText(string text, float width) =>
        new()
        {
            Size = new Vector2(width - TrackerNativeHelpers.Indent, 16f),
            X = TrackerNativeHelpers.Indent,
            FontSize = 12,
            TextColor = TrackerNativeHelpers.ColorMuted,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

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

    private static ResNode MakeLabeledSlider(
        string label, int value, int min, int max, Action<int> onChanged, float width, bool indented = false)
    {
        var indent = indented ? TrackerNativeHelpers.Indent : 0f;
        var row = new ResNode { Size = new Vector2(width, RowH + 4f) };
        var text = MakeText(label, 12, TrackerNativeHelpers.ColorTitle, width * 0.35f, 18f);
        text.Position = new Vector2(indent, 6f);
        text.AttachNode(row);

        var slider = new SliderNode
        {
            Position = new Vector2(indent + width * 0.35f, 2f),
            Size = new Vector2(width * 0.5f - indent, RowH),
            Min = min,
            Max = max,
            Step = 1,
            Value = Math.Clamp(value, min, max),
        };
        slider.OnValueChanged = onChanged;
        slider.AttachNode(row);
        return row;
    }

    private static ResNode MakeLabeledNumeric(
        string label, int value, int min, int max, Action<int> onChanged, float width, bool indented = false)
    {
        var indent = indented ? TrackerNativeHelpers.Indent : 0f;
        var row = new ResNode { Size = new Vector2(width, RowH + 4f) };
        var text = MakeText(label, 12, TrackerNativeHelpers.ColorTitle, width * 0.35f, 18f);
        text.Position = new Vector2(indent, 6f);
        text.AttachNode(row);

        var input = new NumericInputNode
        {
            Position = new Vector2(indent + width * 0.35f, 0f),
            Size = new Vector2(120f, RowH),
            Min = min,
            Max = max,
            Step = 1,
            Value = Math.Clamp(value, min, max),
        };
        input.OnValueUpdate = onChanged;
        input.AttachNode(row);
        return row;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
