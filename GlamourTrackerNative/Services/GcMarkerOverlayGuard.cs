using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

/// <summary>
/// Hides GC markers only where another window covers them. Uses safe APIs only (no walking foreign addon nodes).
/// </summary>
internal static unsafe class GcMarkerOverlayGuard
{
    private const string ItemDetailAddonName = "ItemDetail";

    private static readonly string[] IgnoredCollisionNames =
    [
        "Cursor",
        "DragDrop",
        "OperationGuide",
        "Filter",
        "FilterSystem",
        "ScreenFrame",
        "ManagedScreenFrame",
        "GrandCompanySupplyList",
    ];

    private static readonly Vector2[] MarkerProbeFractions =
    [
        new(0.5f, 0.5f),
        new(0.2f, 0.2f),
        new(0.8f, 0.2f),
        new(0.2f, 0.8f),
        new(0.8f, 0.8f),
        new(0.5f, 0.15f),
        new(0.5f, 0.85f),
    ];

    public static bool ShouldDrawAnyMarkers(AtkUnitBase* supplyUnit) =>
        supplyUnit != null && supplyUnit->IsVisible && TryGetRootScreenRect(supplyUnit, out _, out _);

    public static bool TryGetClipRect(AtkUnitBase* supplyUnit, out Vector2 min, out Vector2 max)
    {
        min = default;
        max = default;
        return supplyUnit != null && TryGetRootScreenRect(supplyUnit, out min, out max);
    }

    public static bool ShouldDrawMarkerAt(
        IGameGui gameGui,
        AtkUnitBase* supplyUnit,
        Vector2 topLeft,
        Vector2 markerSize)
    {
        if (supplyUnit == null || !supplyUnit->IsVisible)
            return false;

        if (!TryGetRootScreenRect(supplyUnit, out var supplyMin, out var supplyMax))
            return false;

        var markerMin = topLeft;
        var markerMax = topLeft + markerSize;
        // Small pad so the last partially-visible list row is not rejected for a 1–2px overhang.
        const float edgePad = 6f;
        if (!RectsOverlap(
                markerMin,
                markerMax,
                supplyMin - new Vector2(edgePad, edgePad),
                supplyMax + new Vector2(edgePad, edgePad)))
            return false;

        if (IsBlockedByItemDetail(gameGui, markerMin, markerMax))
            return false;

        return !IsBlockedByCollision(supplyUnit, markerMin, markerSize);
    }

    private static bool IsBlockedByItemDetail(IGameGui gameGui, Vector2 markerMin, Vector2 markerMax)
    {
        var ptr = gameGui.GetAddonByName(ItemDetailAddonName, 1);
        if (ptr.Address == nint.Zero)
            return false;

        var unit = (AtkUnitBase*)ptr.Address;
        if (!unit->IsReady || !unit->IsVisible)
            return false;

        if (!TryGetRootScreenRect(unit, out var occluderMin, out var occluderMax))
            return false;

        return RectsOverlap(markerMin, markerMax, occluderMin, occluderMax);
    }

    private static bool IsBlockedByCollision(AtkUnitBase* supplyUnit, Vector2 markerMin, Vector2 markerSize)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
            return false;

        var dragDrop = manager->AddonDragDrop;

        foreach (var fraction in MarkerProbeFractions)
        {
            var point = markerMin + markerSize * fraction;
            AddonCollision collision = default;
            manager->GetAddonCollision(
                &collision,
                (short)Math.Clamp(point.X, short.MinValue, short.MaxValue),
                (short)Math.Clamp(point.Y, short.MinValue, short.MaxValue));

            if (IsOccludingCollisionTarget(collision.UnitBase, supplyUnit, dragDrop))
                return true;
        }

        return false;
    }

    private static bool IsOccludingCollisionTarget(
        AtkUnitBase* hit,
        AtkUnitBase* supplyUnit,
        AddonDragDrop* dragDrop)
    {
        if (hit == null)
            return false;

        if (hit == supplyUnit || BelongsToSupplyTree(hit, supplyUnit))
            return false;

        if (dragDrop != null && hit == (AtkUnitBase*)dragDrop)
            return false;

        var name = hit->NameString;
        if (string.IsNullOrEmpty(name))
            return true;

        if (name.Equals(ItemDetailAddonName, StringComparison.Ordinal))
            return false;

        foreach (var ignored in IgnoredCollisionNames)
        {
            if (name.Equals(ignored, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool BelongsToSupplyTree(AtkUnitBase* candidate, AtkUnitBase* supplyUnit)
    {
        if (candidate == null || supplyUnit == null)
            return false;

        var supplyId = supplyUnit->Id;
        for (var depth = 0; depth < 8 && candidate != null; depth++)
        {
            if (candidate->Id == supplyId)
                return true;

            if (candidate->ParentId == 0)
                return false;

            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null)
                return false;

            candidate = manager->GetAddonById(candidate->ParentId);
            if (candidate == null || !candidate->IsReady)
                return false;
        }

        return false;
    }

    private static bool TryGetRootScreenRect(AtkUnitBase* unit, out Vector2 min, out Vector2 max)
    {
        min = default;
        max = default;

        if (unit == null || !unit->IsReady || !unit->IsVisible)
            return false;

        var root = unit->RootNode;
        if (root == null)
            return false;

        var scale = unit->Scale > 0f ? unit->Scale : 1f;
        // Prefer unit X/Y so clip/occlusion track the visible window while dragging.
        if (unit->X > 1f || unit->Y > 1f)
            min = new Vector2(unit->X, unit->Y);
        else
            min = new Vector2(root->ScreenX, root->ScreenY);

        var size = new Vector2(root->Width * scale, root->Height * scale);
        if (size.X <= 0f || size.Y <= 0f)
            return false;

        max = min + size;
        return min.X > 1f && min.Y > 1f;
    }

    private static bool RectsOverlap(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax) =>
        aMin.X < bMax.X && aMax.X > bMin.X && aMin.Y < bMax.Y && aMax.Y > bMin.Y;
}
