using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes.Simplified;

namespace GlamourTracker.Services;

internal sealed unsafe partial class GcExpertDeliveryEnhancer
{
    private const float MarkerGapBeforeIcon = 4f;
    private const float MarkerIconSpacing = 2f;

    private int SyncNativeMarkers(AddonGrandCompanySupplyList* addon, AtkUnitBase* supplyUnit)
    {
        if (addon->ExpertDeliveryList == null || !IsExpertTabActive(addon))
        {
            DisposeNativeMarkers();
            return 0;
        }

        var matchIndex = GetExpertMatchIndex();
        if (matchIndex.Items.Count == 0)
        {
            DisposeNativeMarkers();
            return 0;
        }

        var config = this.getConfiguration();
        var list = addon->ExpertDeliveryList;
        var supplyAddress = (nint)supplyUnit;
        var uiScale = Math.Max(supplyUnit->Scale, 0.01f);
        var ownershipRevision = this.ownershipIndex.Revision;

        // Cheap dirty check first — atlas slice resolution is relatively expensive.
        var cheapDirty = list->ScrollOffset != this.lastScrollOffset
            || list->FirstVisibleItemIndex != this.lastFirstVisible
            || list->ListLength != this.lastListLength
            || supplyAddress != this.lastSupplyAddonAddress
            || ownershipRevision != this.lastOwnershipRevision
            || Math.Abs(uiScale - this.lastAddonScale) > 0.001f
            || this.markerNodes.Count == 0;

        this.iconCache.EnsureBakedTexturePath();
        var dresserSlice = this.iconCache.GetResolvedDresserSlice();
        var armoireSlice = this.iconCache.GetResolvedArmoireSlice();
        var atlasSig = AtlasSignature(config, dresserSlice, armoireSlice);

        if (!cheapDirty && atlasSig == this.lastAtlasSignature)
            return this.markerNodes.Count;

        // Rebuild on scroll / list / atlas / scale / ownership change — not while dragging with unchanged list.
        DisposeNativeMarkers();
        this.lastScrollOffset = list->ScrollOffset;
        this.lastFirstVisible = list->FirstVisibleItemIndex;
        this.lastListLength = list->ListLength;
        this.lastSupplyAddonAddress = supplyAddress;
        this.lastAtlasSignature = atlasSig;
        this.lastAddonScale = uiScale;
        this.lastOwnershipRevision = ownershipRevision;

        var dresserSize = dresserSlice.DisplaySize;
        var armoireSize = armoireSlice.DisplaySize;
        var markerWidth = Math.Max(dresserSize.X, armoireSize.X);
        var markerHeight = Math.Max(dresserSize.Y, armoireSize.Y);
        var ownershipCache = new Dictionary<uint, (bool Dresser, bool Armoire)>();

        // Visible window from FirstVisible + NumVisibleRows. Optionally one more row when its
        // formula Y is still inside the list (partial bottom). Never include a fully off-screen
        // next row — GetItemRenderer reuses the top pooled node and the icon jumped to row 0.
        var listNode = AtkUiHelper.GetComponentOwnerResNode((AtkComponentBase*)list);
        if (listNode == null)
            return 0;

        var firstVisible = Math.Max(0, list->FirstVisibleItemIndex);
        var itemHeight = list->ItemHeight > 0 ? list->ItemHeight : (short)40;
        var listHeightLocal = listNode->Height > 0 ? listNode->Height : 0f;
        var fromHeight = listHeightLocal > 0f
            ? (int)MathF.Ceiling(listHeightLocal / itemHeight)
            : 0;
        var visibleRows = Math.Max((int)list->NumVisibleRows, 1);
        visibleRows = Math.Max(visibleRows, fromHeight);
        var endExclusive = Math.Min(list->ListLength, firstVisible + visibleRows);
        var listBottom = listNode->ScreenY + (listNode->Height * uiScale);
        var scroll = (float)list->ScrollOffset;
        if (scroll < 0f || scroll > itemHeight)
            scroll = 0f;
        if (endExclusive < list->ListLength)
        {
            var slot = endExclusive - firstVisible;
            var nextTop = listNode->ScreenY + ((slot * itemHeight) - scroll) * uiScale;
            if (nextTop < listBottom - 2f)
                endExclusive++;
        }

        var listClipTop = listNode->ScreenY;
        var listClipBottom = listBottom;

        for (var itemIndex = firstVisible; itemIndex < endExclusive; itemIndex++)
        {
            var renderer = list->GetItemRenderer(itemIndex);
            var isVisible = list->IsItemVisible(itemIndex);
            var rowRoot = renderer != null ? GetRowRoot(renderer) : null;
            // Partial bottom rows may report !IsItemVisible or a null/pooled renderer.
            var gcItem = FindMatchingExpertItem(
                matchIndex, list, itemIndex, rowRoot, allowIconFallback: isVisible && renderer != null);
            if (gcItem == null)
                continue;

            var itemId = gcItem.Value.ItemId;
            if (!ownershipCache.TryGetValue(itemId, out var stored))
            {
                stored = (IsInDresserForItem(itemId), IsInArmoireForItem(itemId));
                ownershipCache[itemId] = stored;
            }

            var inDresser = stored.Dresser;
            var inArmoire = stored.Armoire;
            if (!inDresser && !inArmoire)
                continue;

            var anchor = AtkUiHelper.TryGetListRowMarkerAnchor(
                list,
                renderer,
                itemIndex,
                MarkerGapBeforeIcon,
                markerHeight,
                markerWidth,
                uiScale,
                preferOwnerNode: true);
            if (anchor == null)
            {
                PluginFileLog.Write(
                    "DEBUG",
                    "gc.markers",
                    $"skip listIdx={itemIndex} id={itemId}: no anchor");
                continue;
            }

            // Drop stale pool rows; keep partial top/bottom rows so UV clip can crop them.
            var rowPad = itemHeight * uiScale;
            if (anchor.Value.Y < listClipTop - rowPad || anchor.Value.Y > listClipBottom + rowPad)
            {
                PluginFileLog.Write(
                    "DEBUG",
                    "gc.markers",
                    $"skip listIdx={itemIndex} id={itemId}: anchor Y outside list");
                continue;
            }

            var markerX = anchor.Value.X;
            var rowCenterY = anchor.Value.Y;

            if (inArmoire)
            {
                TryPlaceMarkerOnAddon(
                    supplyUnit,
                    ref markerX,
                    rowCenterY,
                    armoireSlice,
                    armoireSize,
                    markerHeight,
                    uiScale,
                    GetFlipV(config, isDresser: false),
                    listClipTop,
                    listClipBottom);
            }

            if (inDresser)
            {
                TryPlaceMarkerOnAddon(
                    supplyUnit,
                    ref markerX,
                    rowCenterY,
                    dresserSlice,
                    dresserSize,
                    markerHeight,
                    uiScale,
                    GetFlipV(config, isDresser: true),
                    listClipTop,
                    listClipBottom);
            }
        }

        return this.markerNodes.Count;
    }

    private void TryPlaceMarkerOnAddon(
        AtkUnitBase* supplyUnit,
        ref float markerX,
        float rowCenterY,
        StorageUiIconSlice slice,
        Vector2 sliceSize,
        float markerHeight,
        float uiScale,
        bool flipV,
        float listClipTop,
        float listClipBottom)
    {
        // Always step X so a failed draw does not shove the sibling icon sideways.
        var step = (sliceSize.X + MarkerIconSpacing) * uiScale;
        if (!slice.IsValid || string.IsNullOrWhiteSpace(slice.Path))
        {
            markerX -= step;
            return;
        }

        var screenPos = new Vector2(
            markerX,
            rowCenterY + MathF.Max(0f, (markerHeight - sliceSize.Y) * 0.5f * uiScale));

        if (!AtkUiHelper.TryClipImageVertically(
                screenPos,
                sliceSize,
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
            markerX -= step;
            return;
        }

        if (!GcMarkerOverlayGuard.ShouldDrawMarkerAt(
                this.gameGui, supplyUnit, clippedPos, new Vector2(clippedSize.X * uiScale, clippedSize.Y * uiScale)))
        {
            markerX -= step;
            return;
        }

        AttachAtkMarker(
            supplyUnit,
            AtkUiHelper.ScreenToAddonLocal(supplyUnit, clippedPos),
            clippedSize,
            slice.Path,
            clippedUv,
            clippedTex,
            flipV);
        markerX -= step;
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

    private void AttachAtkMarker(
        AtkUnitBase* unit,
        Vector2 addonLocalPos,
        Vector2 displaySize,
        string path,
        Vector2 textureUv,
        Vector2 textureSize,
        bool flipV)
    {
        if (unit == null || displaySize.X < 1f || displaySize.Y < 1f)
            return;

        var node = new SimpleImageNode
        {
            Size = displaySize,
            Position = addonLocalPos,
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
        node.AttachNode(unit);
        this.markerNodes.Add(node);
    }

    private static string AtlasSignature(Configuration config, StorageUiIconSlice dresser, StorageUiIconSlice armoire) =>
        $"{dresser.Path}|{dresser.U},{dresser.V},{dresser.Width},{dresser.Height},{dresser.DisplayWidth:F1}x{dresser.DisplayHeight:F1}|{GetFlipV(config, true)}"
        + $"||{armoire.Path}|{armoire.U},{armoire.V},{armoire.Width},{armoire.Height},{armoire.DisplayWidth:F1}x{armoire.DisplayHeight:F1}|{GetFlipV(config, false)}";
}
