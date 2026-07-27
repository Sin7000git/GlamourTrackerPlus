using System.Numerics;
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
/// Native Fashion Report window (KamiToolKit / ATK) — full feature shell.
/// </summary>
internal sealed class FashionReportNativeAddon : NativeAddon
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
        RefreshChrome();
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

    private void RefreshChrome()
    {
        var service = plugin.FashionReport;
        var snap = service.Snapshot;

        if (refreshButton != null)
        {
            refreshButton.String = service.IsRefreshing ? "Refreshing…" : "Refresh week";
            refreshButton.IsEnabled = !service.IsRefreshing;
        }

        if (theorycraftButton != null)
            theorycraftButton.IsVisible = !string.IsNullOrWhiteSpace(snap?.TheorycraftUrl);
        if (resultsButton != null)
            resultsButton.IsVisible = !string.IsNullOrWhiteSpace(snap?.ResultsUrl);

        string weekText;
        string statusText;
        Vector4 statusColor;
        byte statusFontSize = 16;
        string metaText;

        if (snap == null)
        {
            weekText = service.IsRefreshing ? "Loading Fashion Report…" : "No Fashion Report loaded yet";
            statusText = string.Empty;
            statusColor = FashionReportNativeHelpers.ColorMuted;
            metaText = string.IsNullOrEmpty(service.LastError)
                ? "Press Refresh week to fetch this week's hints."
                : service.LastError;
        }
        else
        {
            weekText = $"Week {snap.Week} — {snap.Title}";
            var progress = plugin.FashionProgress.GetProgress();
            (statusColor, statusText, statusFontSize) = FashionReportNativeHelpers.FormatProgress(progress);
            if (statusNode != null)
                statusNode.TextTooltip = FashionReportNativeHelpers.ProgressTooltip(progress);

            var parts = new List<string>();
            if (service.LastFetchUtc is { } fetched)
                parts.Add($"Updated {fetched.ToLocalTime():g}");
            if (!string.IsNullOrEmpty(service.LastError))
                parts.Add(service.LastError);
            if (!string.IsNullOrWhiteSpace(selectedTabKey))
            {
                var hint = snap.Hints.FirstOrDefault(h => TabKeyForHint(h) == selectedTabKey);
                if (hint != null)
                {
                    parts.Add(hint.Hint);
                    parts.Add($"{hint.OwnedCount} owned");
                    if (!string.IsNullOrWhiteSpace(hint.RingNote))
                        parts.Add($"Ring: {hint.RingNote}");
                }
            }

            metaText = string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        RefreshVipChrome();

        if (weekNode != null && weekText != lastWeekText)
        {
            weekNode.String = (ReadOnlySeString)weekText;
            lastWeekText = weekText;
        }

        if (statusNode != null
            && (statusText != lastStatusText || statusFontSize != lastStatusFontSize))
        {
            statusNode.String = (ReadOnlySeString)statusText;
            statusNode.TextColor = statusColor;
            statusNode.FontSize = statusFontSize;
            lastStatusText = statusText;
            lastStatusFontSize = statusFontSize;
        }

        if (metaNode != null && metaText != lastMetaText)
        {
            metaNode.String = (ReadOnlySeString)metaText;
            metaNode.TextColor = string.IsNullOrEmpty(service.LastError)
                ? FashionReportNativeHelpers.ColorMuted
                : FashionReportNativeHelpers.ColorError;
            lastMetaText = metaText;
        }
    }

    private void RefreshVipChrome()
    {
        if (vipButton == null && vipIconNode == null)
            return;

        var view = plugin.FashionMgpBuff.GetView();
        var iconId = view.IconId != 0 ? view.IconId : 26173u;

        if (vipIconNode != null && iconId != lastVipIconId)
        {
            vipIconNode.IconId = iconId;
            lastVipIconId = iconId;
        }

        if (vipIconNode != null)
            vipIconNode.Color = view.CanUse
                ? Vector4.One
                : new Vector4(0.55f, 0.55f, 0.55f, 0.85f);

        if (vipButton == null)
            return;

        if (view.ButtonLabel != lastVipLabel)
        {
            vipButton.String = view.ButtonLabel;
            lastVipLabel = view.ButtonLabel;
        }

        vipButton.TextTooltip = view.Tooltip;
        if (view.CanUse != lastVipEnabled || vipButton.IsEnabled != view.CanUse)
        {
            vipButton.IsEnabled = view.CanUse;
            lastVipEnabled = view.CanUse;
        }
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

    private List<FashionReportNativeRow> BuildRows(FashionReportSnapshot? snap)
    {
        if (snap == null)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = "empty",
                    Title = "No Fashion Report loaded",
                    Subtitle = "Press Refresh week",
                },
            ];
        }

        if (selectedTabKey == TabDyes)
            return BuildDyeRows(snap);

        var easy100Key = TabKeyForEasy(snap.Easy100, "Easy 100");
        if (selectedTabKey == easy100Key)
            return BuildEasyRows(snap.Easy100, easy100Key);

        var easy80Key = TabKeyForEasy(snap.Easy80, "Easy 80");
        if (selectedTabKey == easy80Key)
            return BuildEasyRows(snap.Easy80, easy80Key);

        var hint = snap.Hints.FirstOrDefault(h => TabKeyForHint(h) == selectedTabKey);
        if (hint == null)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = "missing-tab",
                    Title = "Slot unavailable",
                    Subtitle = "Refresh week and try again",
                },
            ];
        }

        var rows = new List<FashionReportNativeRow>();
        foreach (var item in hint.Items)
        {
            if (ownedOnly && !item.Owned)
                continue;
            rows.Add(RowForItem(item, hint.SlotKey));
        }

        if (rows.Count == 0)
        {
            rows.Add(new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Info,
                Key = $"empty-{hint.SlotKey}",
                Title = ownedOnly ? "No owned pieces" : "No items listed",
                Subtitle = hint.Hint,
            });
        }

        return rows;
    }

    private List<FashionReportNativeRow> BuildDyeRows(FashionReportSnapshot snap)
    {
        if (snap.Dyes.Count == 0)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = "dye-empty",
                    Title = snap.DyesFresh ? "No dye data" : "Dyes not available yet",
                    Subtitle = snap.DyesFresh ? string.Empty : "Usually posts Friday",
                },
            ];
        }

        return snap.Dyes.Select(dye =>
        {
            var exact = string.IsNullOrWhiteSpace(dye.ExactDye) ? "—" : dye.ExactDye;
            var family = string.IsNullOrWhiteSpace(dye.ColorFamily) ? "—" : dye.ColorFamily;
            return new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Dye,
                Key = $"dye-{dye.SlotKey}",
                Title = $"{dye.SlotLabel}: {exact}",
                Subtitle = $"Family: {family}",
                IconId = ResolveDyeIcon(exact),
            };
        }).ToList();
    }

    private List<FashionReportNativeRow> BuildEasyRows(FashionEasyOutfitView? easy, string tabKey)
    {
        if (easy == null)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = $"{tabKey}-missing",
                    Title = "Not available",
                },
            ];
        }

        if (!easy.Fresh)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = $"{tabKey}-stale",
                    Title = "Not ready yet",
                    Subtitle = "Waiting for dye confirmation",
                },
            ];
        }

        var rows = new List<FashionReportNativeRow>();
        foreach (var item in easy.Items)
        {
            if (ownedOnly && !item.Owned)
                continue;
            rows.Add(RowForItem(item, "easy-" + easy.Title));
        }

        foreach (var (slot, dye) in easy.Dyes)
        {
            rows.Add(new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Dye,
                Key = $"{tabKey}-dye-{slot}",
                Title = $"{slot}: {dye}",
                Subtitle = "Outfit dye",
                IconId = ResolveDyeIcon(dye),
            });
        }

        if (rows.Count == 0)
        {
            rows.Add(new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Info,
                Key = $"{tabKey}-empty",
                Title = ownedOnly ? "No owned pieces" : "No items listed",
            });
        }

        return rows;
    }

    private static FashionReportNativeRow RowForItem(FashionResolvedItem item, string scope) =>
        new()
        {
            Kind = FashionReportNativeRowKind.Item,
            Key = $"{scope}|{item.ItemId}|{item.Name}",
            Title = item.Name,
            Subtitle = item.Summary,
            IconId = item.IconId,
            Badge = FashionReportNativeHelpers.ListBadge(item),
            BadgeColor = FashionReportNativeHelpers.ListBadgeColor(item),
            Item = item,
        };

    private void RebuildDetail(FashionReportNativeRow row)
    {
        if (detailScroll == null)
            return;

        if (row.Key == lastDetailKey && row.Kind != FashionReportNativeRowKind.Item)
            return;

        // Item rows refresh when ownership/materials change even if key matches.
        var mats = row.Item is { } selected
            ? FashionReportNativeHelpers.MaterialsBadge(selected)
            : string.Empty;
        var detailKey = row.Kind == FashionReportNativeRowKind.Item
            ? $"{row.Key}|{row.Badge}|{mats}|{row.Subtitle}"
            : row.Key;
        if (detailKey == lastDetailKey)
            return;
        lastDetailKey = detailKey;

        var list = detailScroll.ContentNode;
        list.Clear();
        autocraftButton = null;
        craftingLogButton = null;
        garlandButton = null;
        lodestoneButton = null;

        var width = detailScroll.Width - 18f;

        list.AddNode(MakeText(row.Title, 16, FashionReportNativeHelpers.ColorSlot, width, 22f));
        if (!string.IsNullOrEmpty(row.Badge))
            list.AddNode(MakeText(row.Badge, 13, row.BadgeColor, width, 18f));
        if (row.Item is { } badgeItem)
        {
            var materials = FashionReportNativeHelpers.MaterialsBadge(badgeItem);
            if (!string.IsNullOrEmpty(materials))
            {
                list.AddNode(MakeText(
                    materials,
                    13,
                    FashionReportNativeHelpers.MaterialsBadgeColor(badgeItem),
                    width,
                    18f));
            }
        }

        if (!string.IsNullOrEmpty(row.Subtitle)
            && (row.Item is null || !FashionReportNativeHelpers.IsRedundantSummary(row.Subtitle, row.Item)))
        {
            list.AddNode(MakeWrappedText(row.Subtitle, 12, FashionReportNativeHelpers.ColorMuted, width));
        }

        if (row.Kind == FashionReportNativeRowKind.Item && row.Item is { } item)
            AddItemDetail(list, item, width);
        else if (row.Kind == FashionReportNativeRowKind.Dye)
            list.AddNode(MakeWrappedText("Exact dye for this slot (plus family).", 12, FashionReportNativeHelpers.ColorMuted, width));

        list.RecalculateLayout();
        detailScroll.RecalculateSizes();
        detailScroll.ScrollToTop();
    }

    private void ClearDetail(string message)
    {
        if (detailScroll == null)
            return;
        lastDetailKey = "clear:" + message;
        var list = detailScroll.ContentNode;
        list.Clear();
        autocraftButton = null;
        craftingLogButton = null;
        garlandButton = null;
        lodestoneButton = null;
        list.AddNode(MakeWrappedText(message, 13, FashionReportNativeHelpers.ColorMuted, detailScroll.Width - 18f));
        list.RecalculateLayout();
        detailScroll.RecalculateSizes();
    }

    private void AddItemDetail(VerticalListNode list, FashionResolvedItem item, float width)
    {
        var canCraft = item.HasCraftRecipe && item.ItemId != 0
                       && plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out _);

        if (canCraft && !item.Owned && plugin.ArtisanIpc.IsAvailable)
        {
            autocraftButton = new TextButtonNode
            {
                Size = new Vector2(Math.Min(220f, width), 28f),
                String = "Autocraft with Artisan",
                TextTooltip = "Starts this recipe in Artisan (×1).\nMaterials must already be in your bags.",
                OnClick = () => TryAutocraft(item),
            };
            list.AddNode(autocraftButton);
        }

        if (canCraft)
        {
            craftingLogButton = new TextButtonNode
            {
                Size = new Vector2(Math.Min(220f, width), 28f),
                String = "Open Crafting Log",
                TextTooltip = "Opens this recipe in the in-game Crafting Log.",
                OnClick = () => TryOpenCraftingLog(item),
            };
            list.AddNode(craftingLogButton);
        }

        foreach (var section in item.Sections)
        {
            list.AddNode(MakeText(section.Label, 13, FashionReportNativeHelpers.TagColor(section.Type), width, 18f));
            if (!string.IsNullOrWhiteSpace(section.Headline))
                list.AddNode(MakeWrappedText(section.Headline, 12, FashionReportNativeHelpers.ColorMuted, width));

            if (section.Type.Equals("craft", StringComparison.OrdinalIgnoreCase) && section.Ingredients.Count > 0)
            {
                foreach (var ing in section.Ingredients)
                {
                    list.AddNode(MakeText(
                        FashionReportNativeHelpers.FormatIngredientLine(ing),
                        12,
                        ing.HasEnough
                            ? FashionReportNativeHelpers.ColorOwned
                            : FashionReportNativeHelpers.ColorMatsMissing,
                        width,
                        16f));
                }

                continue;
            }

            foreach (var line in section.Lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (FashionReportNativeHelpers.LineDuplicatesHeadline(line, section.Headline))
                    continue;

                var preferred = item.PreferredVendor;
                var isPreferred = preferred != null
                    && !string.IsNullOrWhiteSpace(preferred.Location)
                    && (line.Contains(preferred.Location, StringComparison.OrdinalIgnoreCase)
                        || line.Contains(preferred.Name, StringComparison.OrdinalIgnoreCase));
                var label = isPreferred && preferred!.SameArea
                    ? $"• {line}  ← nearby"
                    : $"• {line}";

                // Any NPC/location line with coordinates gets Teleport (vendors, exchange, recompense, etc.).
                if (FashionReportNativeHelpers.HasMapCoordinates(line))
                    list.AddNode(MakeVendorRow(label, line, width));
                else
                    list.AddNode(MakeWrappedText(label, 12, FashionReportNativeHelpers.ColorMuted, width));
            }
        }

        if (item.Sections.Count == 0)
            list.AddNode(MakeText("No acquisition details available.", 12, FashionReportNativeHelpers.ColorMuted, width, 16f));

        if (!string.IsNullOrWhiteSpace(item.GarlandUrl) || !string.IsNullOrWhiteSpace(item.LodestoneUrl))
        {
            var buttons = new HorizontalListNode
            {
                Size = new Vector2(width, 28f),
                ItemSpacing = 8f,
            };

            if (!string.IsNullOrWhiteSpace(item.GarlandUrl))
            {
                garlandButton = new TextButtonNode
                {
                    Size = new Vector2(120f, 28f),
                    String = "Garland Tools",
                    OnClick = () => FashionReportNativeHelpers.OpenUrl(item.GarlandUrl!),
                };
                buttons.AddNode(garlandButton);
            }

            if (!string.IsNullOrWhiteSpace(item.LodestoneUrl))
            {
                lodestoneButton = new TextButtonNode
                {
                    Size = new Vector2(100f, 28f),
                    String = "Lodestone",
                    OnClick = () => FashionReportNativeHelpers.OpenUrl(item.LodestoneUrl!),
                };
                buttons.AddNode(lodestoneButton);
            }

            list.AddNode(buttons);
        }
    }

    private ResNode MakeVendorRow(string label, string teleportTarget, float width)
    {
        const float buttonW = 96f;
        var row = new ResNode
        {
            Size = new Vector2(width, 28f),
        };

        var text = new TextNode
        {
            Position = new Vector2(0f, 5f),
            Size = new Vector2(Math.Max(40f, width - buttonW - 8f), 18f),
            FontSize = 12,
            TextColor = Vector4.One,
            String = (ReadOnlySeString)label,
            TextFlags = TextFlags.Ellipsis,
        };
        text.AttachNode(row);

        var teleport = new TextButtonNode
        {
            Position = new Vector2(width - buttonW, 0f),
            Size = new Vector2(buttonW, 28f),
            String = "Teleport",
            TextTooltip = "Teleport to the nearest aetheryte and flag this vendor on the map.",
            OnClick = () => plugin.VendorTravel.TeleportNearLocation(teleportTarget),
        };
        teleport.AttachNode(row);
        return row;
    }

    private void TryAutocraft(FashionResolvedItem item)
    {
        if (!plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out var recipeId))
        {
            Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {item.Name}.");
            return;
        }

        if (plugin.ArtisanIpc.TryCraftItem(recipeId, 1, out var message))
            Plugin.ChatGui.Print($"[Glamour Tracker+] {message}");
        else
            Plugin.ChatGui.PrintError($"[Glamour Tracker+] {message}");
    }

    private void TryOpenCraftingLog(FashionResolvedItem item)
    {
        if (!plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out var recipeId))
        {
            Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {item.Name}.");
            return;
        }

        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                unsafe
                {
                    var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance();
                    if (agent == null)
                    {
                        Plugin.ChatGui.PrintError("[Glamour Tracker+] Crafting Log is unavailable right now.");
                        return;
                    }

                    agent->OpenRecipeByRecipeId(recipeId);
                }
            }
            catch (Exception ex)
            {
                PluginFileLog.Error("fashion.native", $"Open Crafting Log failed for {item.Name}", ex);
                Plugin.ChatGui.PrintError("[Glamour Tracker+] Could not open the Crafting Log.");
            }
        });
    }

    private static TextNode MakeText(string text, uint fontSize, Vector4 color, float width, float height) =>
        new()
        {
            Size = new Vector2(width, height),
            FontSize = fontSize,
            TextColor = color,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

    private static TextNode MakeWrappedText(string text, uint fontSize, Vector4 color, float width)
    {
        var lines = Math.Clamp(1 + (text.Length / 42), 1, 8);
        return new TextNode
        {
            Size = new Vector2(width, fontSize + 4f + (lines - 1) * (fontSize + 2f)),
            FontSize = fontSize,
            TextColor = color,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.WordWrap | TextFlags.Ellipsis,
        };
    }

    private ushort ResolveDyeIcon(string? dyeName)
    {
        if (string.IsNullOrWhiteSpace(dyeName) || dyeName == "—")
            return 0;

        if (dyeIconCache.TryGetValue(dyeName, out var cached))
            return cached;

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var withDyeSuffix = dyeName.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase)
            ? dyeName
            : dyeName + " Dye";

        ushort icon = 0;
        foreach (var item in sheet)
        {
            if (item.RowId == 0)
                continue;
            var name = item.Name.ExtractText();
            if (string.Equals(name, dyeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, withDyeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                icon = item.Icon;
                break;
            }
        }

        dyeIconCache[dyeName] = icon;
        return icon;
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
