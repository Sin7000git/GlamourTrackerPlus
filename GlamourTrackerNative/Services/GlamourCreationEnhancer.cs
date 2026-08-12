using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using AgentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace GlamourTracker.Services;

/// <summary>
/// Glamour Creation (<c>MiragePrismPrismBoxCrystallize</c>): dresser/armoire ownership icons
/// on each eligible inventory row. Dresser is rightmost; armoire sits to its left when both apply.
/// </summary>
internal sealed unsafe class GlamourCreationEnhancer : IDisposable
{
    private const string AddonName = "MiragePrismPrismBoxCrystallize";
    private const float MinSaneRowHeight = 8f;
    private const float MaxSaneRowHeight = 64f;
    private const float DefaultRowHeight = 36f;

    private static readonly ByteColor OwnedTint = new() { A = 255, R = 115, G = 185, B = 125 };

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly CabinetCatalog cabinetCatalog;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly StorageUiIconCache iconCache;

    private readonly List<SimpleImageNode> markerNodes = [];
    private int lastScrollOffset = int.MinValue;
    private int lastFirstVisible = int.MinValue;
    private int lastListLength = -1;
    private float lastAddonScale = float.NaN;
    private nint lastAddonAddress;
    private string lastLayoutSignature = string.Empty;
    private int lastOwnershipRevision = -1;
    private int lastCrystallizeCount = -1;
    private int lastCrystallizeCategory = int.MinValue;

    public GlamourCreationEnhancer(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        CabinetCatalog cabinetCatalog,
        Func<Configuration> getConfiguration,
        GlamourOwnershipIndex ownershipIndex)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.cabinetCatalog = cabinetCatalog;
        this.getConfiguration = getConfiguration;
        this.ownershipIndex = ownershipIndex;
        this.iconCache = new StorageUiIconCache(gameGui, textureProvider, dataManager, getConfiguration);

        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnFinalize);
    }

    public void ResetCaches() => DisposeNativeMarkers();

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnUiChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnUiChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnFinalize);
        DisposeNativeMarkers();
    }

    private void OnUiChanged(AddonEvent type, AddonArgs args) => this.lastListLength = -1;

    private void OnFinalize(AddonEvent type, AddonArgs args) => DisposeNativeMarkers();

    private void DisposeNativeMarkers()
    {
        foreach (var node in this.markerNodes)
        {
            try
            {
                node.Dispose();
            }
            catch
            {
                // Addon may already be torn down.
            }
        }

        this.markerNodes.Clear();
        this.lastScrollOffset = int.MinValue;
        this.lastFirstVisible = int.MinValue;
        this.lastListLength = -1;
        this.lastAddonScale = float.NaN;
        this.lastAddonAddress = 0;
        this.lastLayoutSignature = string.Empty;
        this.lastOwnershipRevision = -1;
        this.lastCrystallizeCount = -1;
        this.lastCrystallizeCategory = int.MinValue;
    }

    public void Tick()
    {
        var config = this.getConfiguration();
        if (!config.Enabled || !config.ShowGlamourCreationOwnershipIcons)
        {
            if (this.markerNodes.Count > 0)
                DisposeNativeMarkers();
            return;
        }

        var addonPtr = this.gameGui.GetAddonByName(AddonName, 1);
        if (addonPtr.Address == nint.Zero)
        {
            if (this.markerNodes.Count > 0)
                DisposeNativeMarkers();
            return;
        }

        var unit = (AtkUnitBase*)addonPtr.Address;
        if (!GcMarkerOverlayGuard.ShouldDrawAnyMarkers(unit))
        {
            if (this.markerNodes.Count > 0)
                DisposeNativeMarkers();
            return;
        }

        try
        {
            SyncNativeMarkers((AddonMiragePrismPrismBoxCrystallize*)addonPtr.Address, unit);
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("glamour.creation", $"Marker sync failed: {ex.Message}");
            DisposeNativeMarkers();
        }
    }

    private void SyncNativeMarkers(AddonMiragePrismPrismBoxCrystallize* addon, AtkUnitBase* unit)
    {
        if (addon->ItemTreeList == null)
        {
            DisposeNativeMarkers();
            return;
        }

        var data = GetPrismBoxData();
        if (data == null || data->CrystallizeItemCount == 0)
        {
            DisposeNativeMarkers();
            return;
        }

        var list = (AtkComponentList*)addon->ItemTreeList;
        var tree = addon->ItemTreeList;
        var config = this.getConfiguration();
        var addonAddress = (nint)unit;
        var uiScale = Math.Max(unit->Scale, 0.01f);
        var ownershipRevision = this.ownershipIndex.Revision;
        var layoutSig = PlacementSignature(config);

        var cheapDirty = list->ScrollOffset != this.lastScrollOffset
            || list->FirstVisibleItemIndex != this.lastFirstVisible
            || list->ListLength != this.lastListLength
            || addonAddress != this.lastAddonAddress
            || ownershipRevision != this.lastOwnershipRevision
            || data->CrystallizeItemCount != this.lastCrystallizeCount
            || data->CrystallizeCategory != this.lastCrystallizeCategory
            || Math.Abs(uiScale - this.lastAddonScale) > 0.001f
            || layoutSig != this.lastLayoutSignature
            || this.markerNodes.Count == 0;

        this.iconCache.EnsureBakedTexturePath();
        var dresserSlice = this.iconCache.GetResolvedDresserSlice();
        var armoireSlice = this.iconCache.GetResolvedArmoireSlice();

        if (!cheapDirty)
            return;

        DisposeNativeMarkers();
        this.lastScrollOffset = list->ScrollOffset;
        this.lastFirstVisible = list->FirstVisibleItemIndex;
        this.lastListLength = list->ListLength;
        this.lastAddonAddress = addonAddress;
        this.lastLayoutSignature = layoutSig;
        this.lastAddonScale = uiScale;
        this.lastOwnershipRevision = ownershipRevision;
        this.lastCrystallizeCount = data->CrystallizeItemCount;
        this.lastCrystallizeCategory = data->CrystallizeCategory;

        var markerWidth = Math.Max(dresserSlice.DisplaySize.X, armoireSlice.DisplaySize.X);
        var markerHeight = Math.Max(dresserSlice.DisplaySize.Y, armoireSlice.DisplaySize.Y);

        var listNode = AtkUiHelper.GetComponentOwnerResNode((AtkComponentBase*)list);
        if (listNode == null)
            return;

        var rowHeightLocal = EstimateRowHeightLocal(tree, list);
        var onlyOwned = config.StorageIconsOnlyWhenOwned;
        var colorCode = config.ColorCodeStorageIcons;

        var firstVisible = Math.Max(0, list->FirstVisibleItemIndex);
        var listLen = Math.Max(list->ListLength, (int)tree->ItemCount);
        listLen = Math.Max(listLen, (int)data->CrystallizeTreeRowCount);

        // Tree lists often report NumVisibleRows as 1–2. Prefer viewport ÷ sane row height,
        // but never use VisibleItemHeight (that is the whole viewport in px).
        var listHeightLocal = listNode->Height > 0 ? listNode->Height : 0f;
        var fromHeight = listHeightLocal > 0f
            ? (int)MathF.Ceiling(listHeightLocal / Math.Max(MinSaneRowHeight, rowHeightLocal))
            : 0;
        var visibleRows = Math.Max((int)list->NumVisibleRows, 1);
        visibleRows = Math.Max(visibleRows, fromHeight);
        visibleRows = Math.Clamp(visibleRows, 1, 32);

        var endExclusive = Math.Min(listLen, firstVisible + visibleRows);
        var listBottom = listNode->ScreenY + (listNode->Height * uiScale);
        if (endExclusive < listLen)
        {
            var slot = endExclusive - firstVisible;
            var nextTop = listNode->ScreenY + ((slot * rowHeightLocal) * uiScale);
            if (nextTop < listBottom - 2f)
                endExclusive++;
        }

        const float gap = GlamourCreationMarkerDefaults.GapFromRight;
        const float spacing = GlamourCreationMarkerDefaults.IconSpacing;
        const float padRight = GlamourCreationMarkerDefaults.PadRight;
        const float nudgeX = GlamourCreationMarkerDefaults.NudgeX;
        const float nudgeY = GlamourCreationMarkerDefaults.NudgeY;

        // Unique row OwnerNodes → trust their ScreenY for scroll tracking. Always attach to the
        // addon (GC-style); parenting under list rows gets clipped after the first 1–2 items.
        var rowAttachOk = TryBuildUniqueVisibleRowNodes(
            tree, list, firstVisible, endExclusive, out var rowNodesByIndex);

        for (var treeIndex = firstVisible; treeIndex < endExclusive; treeIndex++)
        {
            if (treeIndex < 0 || tree->IsItemSectionHeader(treeIndex))
                continue;

            var rawItemId = ResolveItemIdAtTreeIndex(tree, data, treeIndex);
            var itemId = ItemIdHelper.SheetItemId(rawItemId);
            if (itemId == 0)
                continue;

            if (!this.dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
                continue;

            var canDresser = GlamourOwnershipIndex.IsGlamourGear(item);
            var canArmoire = this.cabinetCatalog.IsArmoireEligible(itemId);
            if (!canDresser && !canArmoire)
                continue;

            // Single dresser glyph: owned if loose list OR outfit piece.
            var inDresser = canDresser && (IsInDresserList(itemId) || IsOutfitPiece(itemId));
            var inArmoire = canArmoire && IsInArmoire(itemId);

            var showDresser = canDresser && (!onlyOwned || inDresser);
            var showArmoire = canArmoire && (!onlyOwned || inArmoire);
            if (!showDresser && !showArmoire)
                continue;

            var renderer = ResolveRenderer(tree, list, treeIndex);
            if (renderer == null)
                continue;

            Vector2? anchor = null;
            if (rowAttachOk
                && rowNodesByIndex.TryGetValue(treeIndex, out var rowRootPtr)
                && rowRootPtr != nint.Zero)
            {
                anchor = TryGetRowScreenAnchorFromNode(
                    (AtkResNode*)rowRootPtr,
                    padRight,
                    gap,
                    markerWidth,
                    markerHeight,
                    uiScale,
                    nudgeX,
                    nudgeY);
            }

            anchor ??= TryGetRowMarkerAnchorMath(
                tree,
                list,
                renderer,
                treeIndex,
                firstVisible,
                listNode,
                rowHeightLocal,
                gap,
                padRight,
                markerWidth,
                markerHeight,
                uiScale,
                nudgeX,
                nudgeY);

            if (anchor == null)
                continue;

            if (anchor.Value.Y < listNode->ScreenY - rowHeightLocal * uiScale
                || anchor.Value.Y > listBottom + rowHeightLocal * uiScale)
                continue;

            var markerX = anchor.Value.X;
            var rowCenterY = anchor.Value.Y;
            // Dresser is always rightmost; armoire sits to its left when both show.
            // Always step X so a failed clip does not shove the other icon sideways.
            if (showDresser)
            {
                PlaceMarkerOnAddon(
                    unit, ref markerX, rowCenterY, dresserSlice, inDresser, colorCode, markerHeight, uiScale,
                    spacing, GetFlipV(config, true), listNode->ScreenY, listBottom);
            }

            if (showArmoire)
            {
                PlaceMarkerOnAddon(
                    unit, ref markerX, rowCenterY, armoireSlice, inArmoire, colorCode, markerHeight, uiScale,
                    spacing, GetFlipV(config, false), listNode->ScreenY, listBottom);
            }
        }

        PluginFileLog.Write(
            "DEBUG",
            "glamour.creation",
            $"sync markers={this.markerNodes.Count} scroll={list->ScrollOffset} first={firstVisible} "
            + $"end={endExclusive} rowH={rowHeightLocal:0.#} onlyOwned={onlyOwned} color={colorCode}");
    }

    /// <summary>
    /// True when each visible leaf row has its own OwnerNode with a distinct screen Y
    /// (pooled renderers all sharing one node → false).
    /// </summary>
    private static bool TryBuildUniqueVisibleRowNodes(
        AtkComponentTreeList* tree,
        AtkComponentList* list,
        int firstVisible,
        int endExclusive,
        out Dictionary<int, nint> rowNodesByIndex)
    {
        rowNodesByIndex = new Dictionary<int, nint>();
        var nodeToIndex = new Dictionary<nint, int>();
        var ys = new HashSet<int>();

        for (var i = firstVisible; i < endExclusive; i++)
        {
            if (tree->IsItemSectionHeader(i))
                continue;

            var renderer = ResolveRenderer(tree, list, i);
            if (renderer == null || renderer->OwnerNode == null)
                continue;

            var node = (AtkResNode*)renderer->OwnerNode;
            if (node->ScreenY <= 1f)
                continue;

            var key = (nint)node;
            if (nodeToIndex.ContainsKey(key))
            {
                rowNodesByIndex.Clear();
                return false;
            }

            nodeToIndex[key] = i;
            rowNodesByIndex[i] = key;
            ys.Add((int)MathF.Round(node->ScreenY));
        }

        if (rowNodesByIndex.Count < 2)
            return rowNodesByIndex.Count >= 1 && ys.Count >= 1;

        return ys.Count >= Math.Min(rowNodesByIndex.Count, 2);
    }

    private static Vector2? TryGetRowScreenAnchorFromNode(
        AtkResNode* rowRoot,
        float padRight,
        float gap,
        float markerWidth,
        float markerHeight,
        float uiScale,
        float nudgeX,
        float nudgeY)
    {
        if (rowRoot == null)
            return null;

        var rowW = rowRoot->Width > 0 ? rowRoot->Width : 0f;
        if (rowW <= 1f || rowRoot->ScreenX <= 1f || rowRoot->ScreenY <= 1f)
            return null;

        var scale = Math.Max(uiScale, 0.01f);
        var rowH = rowRoot->Height > 0 ? rowRoot->Height : markerHeight;
        var x = rowRoot->ScreenX + ((rowW - padRight - gap - markerWidth + nudgeX) * scale);
        var y = rowRoot->ScreenY
            + (MathF.Max(0f, (rowH - markerHeight) * 0.5f) * scale)
            + (nudgeY * scale);
        return new Vector2(x, y);
    }

    private bool PlaceMarkerOnAddon(
        AtkUnitBase* unit,
        ref float markerX,
        float rowCenterY,
        StorageUiIconSlice slice,
        bool owned,
        bool colorCode,
        float markerHeight,
        float uiScale,
        float spacing,
        bool flipV,
        float listClipTop,
        float listClipBottom)
    {
        if (!slice.IsValid || unit == null)
            return false;

        var size = slice.DisplaySize;
        var screenPos = new Vector2(
            markerX,
            rowCenterY + MathF.Max(0f, (markerHeight - size.Y) * 0.5f * uiScale));

        if (!AtkUiHelper.TryClipImageVertically(
                screenPos,
                size,
                uiScale,
                listClipTop,
                listClipBottom,
                new Vector2(slice.U, slice.V),
                new Vector2(slice.Width, slice.Height),
                flipV,
                out var clippedPos,
                out var clippedSize,
                out var clippedUv,
                out var clippedTex))
        {
            markerX -= (size.X + spacing) * uiScale;
            return false;
        }

        if (!GcMarkerOverlayGuard.ShouldDrawMarkerAt(
                this.gameGui, unit, clippedPos, new Vector2(clippedSize.X * uiScale, clippedSize.Y * uiScale)))
        {
            markerX -= (size.X + spacing) * uiScale;
            return false;
        }

        // Color-code: green when owned; missing stays untinted (GC-like).
        ByteColor? tint = colorCode && owned ? OwnedTint : null;

        var ok = AttachAtkMarkerToUnit(
            unit,
            AtkUiHelper.ScreenToAddonLocal(unit, clippedPos),
            clippedSize,
            slice.Path,
            clippedUv,
            clippedTex,
            flipV,
            tint);
        markerX -= (size.X + spacing) * uiScale;
        return ok;
    }

    /// <summary>Leaf order in CrystallizeItems — section headers consume a tree index but no item.</summary>
    private static uint ResolveItemIdAtTreeIndex(
        AtkComponentTreeList* tree,
        MiragePrismPrismBoxData* data,
        int treeIndex)
    {
        if (treeIndex < 0 || data->CrystallizeItemCount == 0)
            return 0;
        if (tree->IsItemSectionHeader(treeIndex))
            return 0;

        var leaf = 0;
        for (var i = 0; i < treeIndex; i++)
        {
            if (!tree->IsItemSectionHeader(i))
                leaf++;
        }

        if (leaf >= data->CrystallizeItemCount)
            return 0;

        return data->CrystallizeItems[leaf].ItemId;
    }

    /// <summary>
    /// Math fallback when row OwnerNodes are pooled. Uses FirstVisible + per-row heights.
    /// Anchors to the list row’s right edge (same side as GC delivery markers).
    /// </summary>
    private static Vector2? TryGetRowMarkerAnchorMath(
        AtkComponentTreeList* tree,
        AtkComponentList* list,
        AtkComponentListItemRenderer* renderer,
        int itemIndex,
        int firstVisible,
        AtkResNode* listNode,
        float rowHeightLocal,
        float gapFromRight,
        float padRight,
        float markerWidth,
        float markerHeight,
        float uiScale,
        float nudgeX,
        float nudgeY)
    {
        if (renderer == null || list == null || listNode == null)
            return null;

        var listX = listNode->ScreenX;
        var listY = listNode->ScreenY;
        if (listX <= 1f || listY <= 1f)
            return null;

        var scale = Math.Max(uiScale, 0.01f);
        var rowH = Math.Clamp(rowHeightLocal, MinSaneRowHeight, MaxSaneRowHeight);

        // Partial-row remainder only (0..rowH). Full rows are already in FirstVisibleItemIndex.
        // Scroll down moves content up → subtract remainder.
        var scrollRemainder = (float)list->ScrollOffset;
        if (scrollRemainder < 0f || scrollRemainder > rowH)
            scrollRemainder = 0f;

        var localY = -scrollRemainder;
        for (var i = firstVisible; i < itemIndex; i++)
            localY += GetRowHeightLocal(tree, i, rowH);

        var rowTop = listY + (localY * scale);
        var rowLeft = listX + (renderer->Left * scale);
        var rowWidthLocal = renderer->OwnerNode != null
            ? ((AtkResNode*)renderer->OwnerNode)->Width
            : listNode->Width;
        if (rowWidthLocal <= 1f)
            rowWidthLocal = listNode->Width;

        // Rightmost marker’s left edge (screen), matching PlaceMarkersOnRow.
        var x = rowLeft
            + ((rowWidthLocal - padRight - gapFromRight - markerWidth + nudgeX) * scale);
        var y = rowTop + MathF.Max(0f, (rowH - markerHeight) * 0.5f * scale) + (nudgeY * scale);
        return new Vector2(x, y);
    }

    private static AtkComponentListItemRenderer* ResolveRenderer(
        AtkComponentTreeList* tree,
        AtkComponentList* list,
        int treeIndex)
    {
        var treeItem = tree->GetItem(treeIndex);
        if (treeItem != null && treeItem->Renderer != null)
            return treeItem->Renderer;

        return list->GetItemRenderer(treeIndex);
    }

    /// <summary>
    /// Per-row height only. <see cref="AtkComponentTreeList.VisibleItemHeight"/> is the viewport
    /// (often hundreds of px) — never use it as a row size.
    /// </summary>
    private static float EstimateRowHeightLocal(AtkComponentTreeList* tree, AtkComponentList* list)
    {
        var sample = Math.Min((int)tree->ItemCount, 24);
        for (var i = 0; i < sample; i++)
        {
            var item = tree->GetItem(i);
            if (item == null)
                continue;
            var h = (float)item->Height;
            if (h is >= MinSaneRowHeight and <= MaxSaneRowHeight)
                return h;
        }

        var itemH = (float)list->ItemHeight;
        if (itemH is >= MinSaneRowHeight and <= MaxSaneRowHeight)
            return itemH;

        var stepY = (float)list->RowStepY;
        if (stepY is >= MinSaneRowHeight and <= MaxSaneRowHeight)
            return stepY;

        return DefaultRowHeight;
    }

    private static float GetRowHeightLocal(AtkComponentTreeList* tree, int index, float fallback)
    {
        var item = tree->GetItem(index);
        if (item == null)
            return fallback;

        var h = (float)item->Height;
        if (h is >= MinSaneRowHeight and <= MaxSaneRowHeight)
            return h;

        return fallback;
    }

    private bool AttachAtkMarkerToUnit(
        AtkUnitBase* unit,
        Vector2 addonLocalPos,
        Vector2 displaySize,
        string? path,
        Vector2 textureUv,
        Vector2 textureSize,
        bool flipV,
        ByteColor? tint)
    {
        if (unit == null || string.IsNullOrWhiteSpace(path) || displaySize.X < 1f || displaySize.Y < 1f)
            return false;

        var node = CreateMarkerNode(addonLocalPos, displaySize, path, textureUv, textureSize, flipV, tint);
        node.AttachNode(unit);
        this.markerNodes.Add(node);
        return true;
    }

    private static SimpleImageNode CreateMarkerNode(
        Vector2 localPos,
        Vector2 displaySize,
        string path,
        Vector2 textureUv,
        Vector2 textureSize,
        bool flipV,
        ByteColor? tint)
    {
        var node = new SimpleImageNode
        {
            Size = displaySize,
            Position = localPos,
            IsVisible = true,
            PartId = 0,
        };

        node.LoadTexture(path);
        node.TextureCoordinates = textureUv;
        node.TextureSize = textureSize;
        node.WrapMode = WrapMode.Stretch;

        if (flipV)
            node.ImageNodeFlags = ImageNodeFlags.FlipV;

        node.AddNodeFlags(NodeFlags.Visible, NodeFlags.Enabled);
        if (tint is { } color)
            AtkUiHelper.TintNode((AtkResNode*)node.Node, color);
        return node;
    }

    private static bool GetFlipV(Configuration config, bool isDresser)
    {
#if GLAMOUR_DEV
        return isDresser ? config.FlipDresserIconV : config.FlipArmoireIconV;
#else
        _ = config;
        _ = isDresser;
        return StorageIconAtlasDefaults.FlipVertically;
#endif
    }

    private static string PlacementSignature(Configuration config) =>
        $"{config.ColorCodeStorageIcons}|{config.StorageIconsOnlyWhenOwned}";

    private static MiragePrismPrismBoxData* GetPrismBoxData()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return null;

        var agent = (AgentMiragePrismPrismBox*)agentModule->GetAgentByInternalId(AgentId.MiragePrismPrismBox);
        if (agent == null || agent->Data == null)
            return null;

        return agent->Data;
    }

    private bool IsInDresserList(uint itemId)
    {
        foreach (var id in ItemIdHelper.GetRelatedItemIds(itemId))
        {
            if (this.ownershipIndex.IsInDresserItemList(id))
                return true;
        }

        return false;
    }

    private bool IsOutfitPiece(uint itemId)
    {
        foreach (var id in ItemIdHelper.GetRelatedItemIds(itemId))
        {
            if (this.ownershipIndex.IsDresserOutfitPiece(id))
                return true;
        }

        return false;
    }

    private bool IsInArmoire(uint itemId)
    {
        foreach (var id in ItemIdHelper.GetRelatedItemIds(itemId))
        {
            if (this.ownershipIndex.IsInArmoire(id))
                return true;
        }

        return false;
    }
}
