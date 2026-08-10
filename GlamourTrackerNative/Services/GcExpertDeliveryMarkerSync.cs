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

        for (var itemIndex = 0; itemIndex < list->ListLength; itemIndex++)
        {
            if (!list->IsItemVisible(itemIndex))
                continue;

            var renderer = list->GetItemRenderer(itemIndex);
            if (renderer == null)
                continue;

            var rowRoot = GetRowRoot(renderer);
            var gcItem = FindMatchingExpertItem(matchIndex, list, itemIndex, rowRoot);
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
                list, renderer, itemIndex, MarkerGapBeforeIcon, markerHeight, markerWidth, uiScale);
            if (anchor == null)
                continue;

            var markerX = anchor.Value.X;
            var rowCenterY = anchor.Value.Y;

            if (inArmoire)
            {
                var screenPos = new Vector2(
                    markerX,
                    rowCenterY + MathF.Max(0f, (markerHeight - armoireSize.Y) * 0.5f * uiScale));
                if (GcMarkerOverlayGuard.ShouldDrawMarkerAt(this.gameGui, supplyUnit, screenPos, armoireSize * uiScale))
                {
                    AttachAtkMarker(
                        supplyUnit,
                        AtkUiHelper.ScreenToAddonLocal(supplyUnit, screenPos),
                        armoireSlice,
                        config.FlipArmoireIconV);
                    markerX -= (armoireSize.X + MarkerIconSpacing) * uiScale;
                }
            }

            if (inDresser)
            {
                var screenPos = new Vector2(
                    markerX,
                    rowCenterY + MathF.Max(0f, (markerHeight - dresserSize.Y) * 0.5f * uiScale));
                if (GcMarkerOverlayGuard.ShouldDrawMarkerAt(this.gameGui, supplyUnit, screenPos, dresserSize * uiScale))
                {
                    AttachAtkMarker(
                        supplyUnit,
                        AtkUiHelper.ScreenToAddonLocal(supplyUnit, screenPos),
                        dresserSlice,
                        config.FlipDresserIconV);
                }
            }
        }

        return this.markerNodes.Count;
    }

    private void AttachAtkMarker(
        AtkUnitBase* supplyUnit,
        Vector2 rootRelativePos,
        StorageUiIconSlice slice,
        bool flipV)
    {
        if (!slice.IsValid || string.IsNullOrWhiteSpace(slice.Path))
            return;

        var node = new SimpleImageNode
        {
            Size = slice.DisplaySize,
            Position = rootRelativePos,
            IsVisible = true,
            PartId = 0,
        };

        node.LoadTexture(slice.Path);
        node.TextureCoordinates = new Vector2(slice.U, slice.V);
        node.TextureSize = new Vector2(slice.Width, slice.Height);
        node.WrapMode = WrapMode.Stretch;

        if (flipV)
            node.ImageNodeFlags = ImageNodeFlags.FlipV;

        node.AddNodeFlags(NodeFlags.Visible, NodeFlags.Enabled);
        node.AttachNode(supplyUnit);
        this.markerNodes.Add(node);
    }

    private static string AtlasSignature(Configuration config, StorageUiIconSlice dresser, StorageUiIconSlice armoire) =>
        $"{dresser.Path}|{dresser.U},{dresser.V},{dresser.Width},{dresser.Height},{dresser.DisplayWidth:F1}x{dresser.DisplayHeight:F1}|{config.FlipDresserIconV}"
        + $"||{armoire.Path}|{armoire.U},{armoire.V},{armoire.Width},{armoire.Height},{armoire.DisplayWidth:F1}x{armoire.DisplayHeight:F1}|{config.FlipArmoireIconV}";
}
