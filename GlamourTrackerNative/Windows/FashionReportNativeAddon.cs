using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

/// <summary>
/// Native Fashion Report window (KamiToolKit / ATK) — full feature shell.
/// </summary>
internal sealed partial class FashionReportNativeAddon : NativeAddon
{
    private const string TabDyes = "Dyes";
    private const float ToolbarH = 28f;
    private const float HeaderH = 32f;
    private const float MetaH = 20f;
    private const float TabH = 28f;
    private const float Gap = 6f;

    private readonly Plugin plugin;
    private readonly Dictionary<string, ushort> dyeIconCache = new(StringComparer.OrdinalIgnoreCase);

    private TextButtonNode? refreshButton;
    private TextButtonNode? ownershipButton;
    private TextButtonNode? theorycraftButton;
    private TextButtonNode? resultsButton;
    private TextButtonNode? siteButton;
    private CheckboxNode? ownedOnlyCheckbox;
    private TextNode? weekNode;
    private IconImageNode? vipIconNode;
    private TextButtonNode? vipButton;
    private TextNode? statusNode;
    private TextNode? metaNode;
    private TabBarNode? tabBar;
    private ListNode<FashionReportNativeRow, FashionReportNativeItemNode>? itemList;
    private ScrollingNode<VerticalListNode>? detailScroll;
    private TextButtonNode? autocraftButton;
    private TextButtonNode? craftingLogButton;
    private TextButtonNode? garlandButton;
    private TextButtonNode? lodestoneButton;

    private bool ownedOnly;
    private string selectedTabKey = string.Empty;
    private string selectedRowKey = string.Empty;
    private string lastTabsSignature = string.Empty;
    private string lastListSignature = string.Empty;
    private string lastDetailKey = string.Empty;
    private string lastWeekText = string.Empty;
    private string lastStatusText = string.Empty;
    private string lastMetaText = string.Empty;
    private string lastVipLabel = string.Empty;
    private uint lastVipIconId;
    private bool lastVipEnabled = true;
    private byte lastStatusFontSize;
    private FashionResolvedItem? selectedItem;
    private DateTime nextChromeRefreshUtc = DateTime.MinValue;
    private bool lastChromeRefreshing;

    public FashionReportNativeAddon(Plugin plugin)
    {
        this.plugin = plugin;
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        var origin = ContentStartPosition;
        var content = ContentSize;
        var x = origin.X;
        var y = origin.Y;

        refreshButton = new TextButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(120f, ToolbarH),
            String = "Refresh week",
            OnClick = OnRefreshClicked,
        };
        refreshButton.AttachNode(this);

        ownershipButton = new TextButtonNode
        {
            Position = new Vector2(x + 126f, y),
            Size = new Vector2(140f, ToolbarH),
            String = "Update ownership",
            OnClick = () => plugin.FashionReport.RebindOwnership(),
            TextTooltip = "Re-check bags, armoury, saddlebag, dresser/armoire, and nearby vendors.",
        };
        ownershipButton.AttachNode(this);

        ownedOnlyCheckbox = new CheckboxNode
        {
            Position = new Vector2(x + 276f, y + 2f),
            Size = new Vector2(24f, 24f),
            String = "Owned pieces only",
        };
        ownedOnlyCheckbox.AttachNode(this);
        ownedOnlyCheckbox.OnClick = isChecked =>
        {
            ownedOnly = isChecked;
            RefreshList(force: true);
        };

        siteButton = new TextButtonNode
        {
            Position = new Vector2(x + content.X - 110f, y),
            Size = new Vector2(110f, ToolbarH),
            String = "FRXIV.com",
            OnClick = () => FashionReportNativeHelpers.OpenUrl("https://fashionreportxiv.com/"),
        };
        siteButton.AttachNode(this);

        resultsButton = new TextButtonNode
        {
            Position = new Vector2(x + content.X - 224f, y),
            Size = new Vector2(108f, ToolbarH),
            String = "Open results",
            OnClick = () =>
            {
                var url = plugin.FashionReport.Snapshot?.ResultsUrl;
                if (!string.IsNullOrWhiteSpace(url))
                    FashionReportNativeHelpers.OpenUrl(url);
            },
        };
        resultsButton.AttachNode(this);

        theorycraftButton = new TextButtonNode
        {
            Position = new Vector2(x + content.X - 348f, y),
            Size = new Vector2(118f, ToolbarH),
            String = "Theorycraft",
            OnClick = () =>
            {
                var url = plugin.FashionReport.Snapshot?.TheorycraftUrl;
                if (!string.IsNullOrWhiteSpace(url))
                    FashionReportNativeHelpers.OpenUrl(url);
            },
        };
        theorycraftButton.AttachNode(this);

        y += ToolbarH + Gap;

        weekNode = new TextNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(content.X * 0.38f, HeaderH),
            FontSize = 18,
            String = (ReadOnlySeString)"Loading Fashion Report…",
        };
        weekNode.AttachNode(this);

        var vipX = x + content.X * 0.38f;
        var vipW = content.X * 0.34f;
        vipIconNode = new IconImageNode
        {
            Position = new Vector2(vipX, y + 2f),
            Size = new Vector2(28f, 28f),
            TextureSize = new Vector2(28f, 28f),
            ImageNodeFlags = ImageNodeFlags.AutoFit,
            IconId = 26173,
        };
        vipIconNode.AttachNode(this);

        vipButton = new TextButtonNode
        {
            Position = new Vector2(vipX + 32f, y + 2f),
            Size = new Vector2(MathF.Max(120f, vipW - 36f), 28f),
            String = "Use Gold Saucer VIP Card",
            OnClick = () => plugin.FashionMgpBuff.TryUseVipCard(),
            TextTooltip = "Use a Gold Saucer VIP Card for +15% MGP for 120 minutes.",
        };
        vipButton.AttachNode(this);

        statusNode = new TextNode
        {
            Position = new Vector2(x + content.X * 0.72f, y),
            Size = new Vector2(content.X * 0.28f, HeaderH),
            FontSize = 16,
            AlignmentType = AlignmentType.Right,
            String = (ReadOnlySeString)string.Empty,
        };
        statusNode.AttachNode(this);

        y += HeaderH + 2f;

        metaNode = new TextNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(content.X, MetaH),
            FontSize = 12,
            TextColor = FashionReportNativeHelpers.ColorMuted,
            String = (ReadOnlySeString)string.Empty,
            TextFlags = TextFlags.Ellipsis,
        };
        metaNode.AttachNode(this);

        y += MetaH + Gap;

        tabBar = new TabBarNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(content.X, TabH),
        };
        tabBar.AttachNode(this);

        y += TabH + Gap;

        var bodyH = content.Y - (y - origin.Y);
        var listW = MathF.Floor(content.X * 0.48f);
        var detailW = content.X - listW - Gap;

        itemList = new ListNode<FashionReportNativeRow, FashionReportNativeItemNode>
        {
            Position = new Vector2(x, y),
            Size = new Vector2(listW, bodyH),
            OptionsList = [],
            AutoResetScroll = false,
            NoResultsString = "No items to show.",
            OnItemSelected = OnRowSelected,
        };
        itemList.AttachNode(this);

        detailScroll = new ScrollingNode<VerticalListNode>
        {
            Position = new Vector2(x + listW + Gap, y),
            Size = new Vector2(detailW, bodyH),
            AutoHideScrollBar = true,
            ScrollSpeed = 28,
        };
        detailScroll.ContentNode.FitContents = true;
        detailScroll.ContentNode.FitWidth = true;
        detailScroll.ContentNode.ItemSpacing = 4f;
        detailScroll.AttachNode(this);

        RebuildTabs();
        RefreshChrome();
        RefreshList(force: true);
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // Progress + MGP buff scan inventory/status — throttle unless refresh state flips.
        var refreshing = plugin.FashionReport.IsRefreshing;
        var now = DateTime.UtcNow;
        if (refreshing != lastChromeRefreshing || now >= nextChromeRefreshUtc)
        {
            lastChromeRefreshing = refreshing;
            nextChromeRefreshUtc = now.AddSeconds(1);
            RefreshChrome();
        }

        RebuildTabsIfNeeded();
        RefreshList(force: false);
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        refreshButton = null;
        ownershipButton = null;
        theorycraftButton = null;
        resultsButton = null;
        siteButton = null;
        ownedOnlyCheckbox = null;
        weekNode = null;
        vipIconNode = null;
        vipButton = null;
        statusNode = null;
        metaNode = null;
        tabBar = null;
        itemList = null;
        detailScroll = null;
        autocraftButton = null;
        craftingLogButton = null;
        garlandButton = null;
        lodestoneButton = null;
        selectedItem = null;
        // Nodes are recreated on next Open(); cached strings must reset or week stays on the
        // initializer text ("Loading Fashion Report…") forever.
        lastWeekText = string.Empty;
        lastStatusText = string.Empty;
        lastMetaText = string.Empty;
        lastVipLabel = string.Empty;
        lastVipIconId = 0;
        lastVipEnabled = true;
        lastStatusFontSize = 0;
        lastTabsSignature = string.Empty;
        lastListSignature = string.Empty;
        lastDetailKey = string.Empty;
    }

    private void OnRefreshClicked()
    {
        plugin.RefreshAll(false);
        _ = plugin.FashionReport.RefreshAsync(force: true);
    }

    private void OnRowSelected(FashionReportNativeRow? row)
    {
        if (row == null)
            return;

        selectedRowKey = row.Key;
        selectedItem = row.Item;
        RebuildDetail(row);
    }

    private void RebuildTabsIfNeeded()
    {
        var signature = BuildTabsSignature(plugin.FashionReport.Snapshot);
        if (signature == lastTabsSignature)
            return;
        RebuildTabs();
    }

    private void RebuildTabs()
    {
        if (tabBar == null)
            return;

        var snap = plugin.FashionReport.Snapshot;
        lastTabsSignature = BuildTabsSignature(snap);
        tabBar.Clear();

        if (snap == null)
        {
            selectedTabKey = string.Empty;
            return;
        }

        var keys = new List<string>();
        foreach (var hint in snap.Hints)
        {
            var key = TabKeyForHint(hint);
            keys.Add(key);
            var label = hint.SlotLabel;
            tabBar.AddTab((ReadOnlySeString)label, () => SelectTab(key));
        }

        keys.Add(TabDyes);
        tabBar.AddTab((ReadOnlySeString)TabDyes, () => SelectTab(TabDyes));

        var easy100Key = TabKeyForEasy(snap.Easy100, "Easy 100");
        keys.Add(easy100Key);
        tabBar.AddTab((ReadOnlySeString)easy100Key, () => SelectTab(easy100Key));

        var easy80Key = TabKeyForEasy(snap.Easy80, "Easy 80");
        keys.Add(easy80Key);
        tabBar.AddTab((ReadOnlySeString)easy80Key, () => SelectTab(easy80Key));

        if (string.IsNullOrEmpty(selectedTabKey) || !keys.Contains(selectedTabKey))
            selectedTabKey = keys[0];

        tabBar.SelectTab((ReadOnlySeString)DisplayLabelForTab(snap, selectedTabKey));
        RefreshList(force: true);
    }

    private void SelectTab(string key)
    {
        if (selectedTabKey == key)
            return;
        selectedTabKey = key;
        selectedRowKey = string.Empty;
        selectedItem = null;
        lastDetailKey = string.Empty;
        RefreshList(force: true);
    }

    private void RefreshList(bool force)
    {
        if (itemList == null)
            return;

        var service = plugin.FashionReport;
        var snap = service.Snapshot;
        var signature = BuildListSignature(snap, selectedTabKey, ownedOnly, service.LastFetchUtc);
        if (!force && signature == lastListSignature)
            return;

        lastListSignature = signature;
        var rows = BuildRows(snap);
        itemList.OptionsList = rows;
        itemList.Update();

        FashionReportNativeRow? keep = null;
        if (!string.IsNullOrEmpty(selectedRowKey))
            keep = rows.FirstOrDefault(r => r.Key == selectedRowKey);
        keep ??= rows.FirstOrDefault(r => r.Kind != FashionReportNativeRowKind.Info);

        if (keep != null)
        {
            selectedRowKey = keep.Key;
            selectedItem = keep.Item;
            RebuildDetail(keep);
        }
        else
        {
            selectedRowKey = string.Empty;
            selectedItem = null;
            ClearDetail("Select an item for acquisition details.");
        }
    }

    private static string TabKeyForHint(FashionHintSlotView hint) => hint.SlotLabel;

    private static string TabKeyForEasy(FashionEasyOutfitView? easy, string fallback) =>
        string.IsNullOrWhiteSpace(easy?.Title) ? fallback : easy!.Title;

    private static string DisplayLabelForTab(FashionReportSnapshot snap, string key)
    {
        if (key == TabDyes)
            return TabDyes;
        foreach (var hint in snap.Hints)
        {
            if (TabKeyForHint(hint) == key)
                return hint.SlotLabel;
        }

        return key;
    }

    private static string BuildTabsSignature(FashionReportSnapshot? snap)
    {
        if (snap == null)
            return "null";
        var hints = string.Join('|', snap.Hints.Select(h => h.SlotKey + ":" + h.SlotLabel));
        return $"{snap.Week}|{hints}|{snap.Easy100?.Title}|{snap.Easy80?.Title}";
    }

    private static string BuildListSignature(
        FashionReportSnapshot? snap,
        string tab,
        bool ownedOnly,
        DateTime? fetched)
    {
        if (snap == null)
            return $"null|{ownedOnly}";

        var ownedSum = snap.Hints.Sum(h => h.OwnedCount);
        var mats = snap.Hints.SelectMany(h => h.Items)
            .Sum(i => i.CraftMatsReady * 17 + i.CraftMatsTotal);
        return $"{snap.Week}|{fetched?.Ticks}|{tab}|{ownedOnly}|{ownedSum}|{mats}|{snap.Dyes.Count}|{snap.Easy100?.Fresh}|{snap.Easy80?.Fresh}";
    }
}
