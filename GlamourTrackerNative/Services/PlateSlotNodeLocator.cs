using System.Numerics;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

/// <summary>
/// Locates the 12 equipment slots on MiragePrismMiragePlate (paperdoll columns either side of the preview).
/// </summary>
/// <remarks>
/// Live Icon/DragDrop/Collision nodes give slot Y/size. Anchors are cached as fractions of
/// addon size so UI/window scale changes keep pitch correct. Buttons sit on the inner side
/// of each column (toward the character preview).
/// </remarks>
internal static unsafe class PlateSlotNodeLocator
{
    private static readonly int[] LeftColumnSlots = [0, 2, 3, 4, 5, 6]; // MH, Head, Body, Hands, Legs, Feet
    private static readonly int[] RightColumnSlots = [1, 7, 8, 9, 10, 11]; // OH, Ears, Neck, Wrists, RR, LR

    // Paperdoll row grid — fractions of on-screen addon size (scale-safe).
    // Live ATK Y is consistently compressed toward the preview; we always use these for Y.
    private const float PaperdollLeftXFrac = 0.086f;
    private const float PaperdollRightXFrac = 0.914f;
    private const float PaperdollFirstRowYFrac = 0.142f;
    private const float PaperdollLastRowYFrac = 0.662f;
    private const float PaperdollIconHFrac = 0.078f;

    private static DateTime nextDiagUtc = DateTime.MinValue;

    private static bool cacheValid;
    private static bool nodeIdsLocked;
    private static float cachedAddonW;
    private static float cachedAddonH;
    private static readonly Vector2[] CachedRelativePos = new Vector2[GlamourPlateSlotMap.SlotCount];
    private static readonly float[] CachedWidths = new float[GlamourPlateSlotMap.SlotCount];
    private static readonly float[] CachedHeights = new float[GlamourPlateSlotMap.SlotCount];
    private static readonly bool[] CachedButtonOnLeft = new bool[GlamourPlateSlotMap.SlotCount];
    private static readonly uint[] CachedNodeIds = new uint[GlamourPlateSlotMap.SlotCount];
    private static readonly bool[] CachedBottomAnchor = new bool[GlamourPlateSlotMap.SlotCount];

    // Do not lock on the first ATK layout after open — ScreenX/Y are often wrong until refresh/scale.
    private const int StableFramesToLock = 4;
    private static int stableDiscoverFrames;
    private static readonly Vector2[] PendingScreenPos = new Vector2[GlamourPlateSlotMap.SlotCount];
    private static readonly uint[] PendingNodeIds = new uint[GlamourPlateSlotMap.SlotCount];
    private static bool hasPendingDiscover;

    public static bool IsLeftColumnSlot(int slot) => LeftColumnSlots.Contains(slot);

    public static void ClearCache()
    {
        cacheValid = false;
        nodeIdsLocked = false;
        cachedAddonW = 0;
        cachedAddonH = 0;
        stableDiscoverFrames = 0;
        hasPendingDiscover = false;
        Array.Clear(CachedNodeIds);
        Array.Clear(PendingNodeIds);
        Array.Clear(CachedBottomAnchor);
    }

    /// <summary>Drop a premature NodeId lock so the next draw can rediscover (plate/dresser refresh).</summary>
    public static void InvalidateLock()
    {
        nodeIdsLocked = false;
        stableDiscoverFrames = 0;
        hasPendingDiscover = false;
    }

    public static bool TryGetSlotScreenRects(
        AtkUnitBase* unit,
        AtkUnitBasePtr addon,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        IPluginLog? log = null)
    {
        if (unit == null || screenPositions.Length < GlamourPlateSlotMap.SlotCount
            || widths.Length < GlamourPlateSlotMap.SlotCount
            || heights.Length < GlamourPlateSlotMap.SlotCount
            || buttonOnLeft.Length < GlamourPlateSlotMap.SlotCount)
            return false;

        if (!TryGetAddonOrigin(unit, addon, out var origin))
            return false;

        if (!TryGetAddonSize(unit, addon, out var addonW, out var addonH))
            return false;

        var sizeChanged = cacheValid
            && (Math.Abs(addonW - cachedAddonW) > 2f || Math.Abs(addonH - cachedAddonH) > 2f);

        // Large scale jumps: drop NodeId lock so we rediscover instead of reusing stale pixel geometry.
        if (sizeChanged && cachedAddonH > 1f)
        {
            var sy = addonH / cachedAddonH;
            if (sy is < 0.92f or > 1.08f)
                nodeIdsLocked = false;
        }

        // Sticky NodeIds: same frames whether the slot is filled or empty.
        if (nodeIdsLocked)
        {
            if (TryResolveByNodeIds(unit, origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft))
            {
                FinishLayout(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft, persist: true);
                if (LayoutPitchIsSane(screenPositions, origin, addonH))
                    return true;

                // Locked the wrong nodes on first open — unlock and rediscover.
                LogDiag(log, "plate-slots: locked layout failed pitch sanity — unlocking.");
                InvalidateLock();
            }
            else if (cacheValid)
            {
                ApplyCache(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft);
                FinishLayout(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft, persist: false);
                return true;
            }
        }

        Span<Vector2> discoveredPos = stackalloc Vector2[GlamourPlateSlotMap.SlotCount];
        Span<float> discoveredW = stackalloc float[GlamourPlateSlotMap.SlotCount];
        Span<float> discoveredH = stackalloc float[GlamourPlateSlotMap.SlotCount];
        Span<bool> discoveredLeft = stackalloc bool[GlamourPlateSlotMap.SlotCount];
        Span<uint> discoveredIds = stackalloc uint[GlamourPlateSlotMap.SlotCount];
        Span<bool> discoveredBottom = stackalloc bool[GlamourPlateSlotMap.SlotCount];
        discoveredIds.Clear();
        discoveredBottom.Clear();

        // Keep trying until we get a validated live-node lock (geometric is only a stopgap).
        if (TryDiscoverSlotNodes(
                unit, addon, discoveredPos, discoveredW, discoveredH, discoveredLeft, discoveredIds, discoveredBottom, log)
            && ValidateLayout(discoveredPos, discoveredW, discoveredH, discoveredLeft)
            && LayoutPitchIsSane(discoveredPos, origin, addonH))
        {
            FinishLayout(origin, addonW, addonH, discoveredPos, discoveredW, discoveredH, discoveredLeft, persist: false);

            if (TryAdvanceStableLock(
                    origin, addonW, addonH,
                    discoveredPos, discoveredW, discoveredH, discoveredLeft, discoveredIds, discoveredBottom,
                    screenPositions, widths, heights, buttonOnLeft, log))
                return true;

            // Not stable yet — draw this frame's discovery without locking.
            CopySlots(discoveredPos, discoveredW, discoveredH, discoveredLeft, screenPositions, widths, heights, buttonOnLeft);
            return true;
        }

        stableDiscoverFrames = 0;
        hasPendingDiscover = false;

        if (cacheValid)
        {
            ApplyCache(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft);
            FinishLayout(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft, persist: false);
            return true;
        }

        // Last resort before any cache (empty plate on first open) — do NOT set cacheValid sticky
        // from geometric alone; that blocked rediscovery until a scale change.
        FillGeometricFallback(addon, origin, addonW, addonH, discoveredPos, discoveredW, discoveredH, discoveredLeft);
        FinishLayout(origin, addonW, addonH, discoveredPos, discoveredW, discoveredH, discoveredLeft, persist: false);
        CopySlots(discoveredPos, discoveredW, discoveredH, discoveredLeft, screenPositions, widths, heights, buttonOnLeft);
        LogDiag(log, "plate-slots: geometric paperdoll anchors (awaiting stable live lock).");
        return true;
    }

    private static bool TryAdvanceStableLock(
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> discoveredPos,
        Span<float> discoveredW,
        Span<float> discoveredH,
        Span<bool> discoveredLeft,
        Span<uint> discoveredIds,
        Span<bool> discoveredBottom,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        IPluginLog? log)
    {
        if (!hasPendingDiscover || !PendingMatches(discoveredPos, discoveredIds))
        {
            for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
            {
                PendingScreenPos[i] = discoveredPos[i];
                PendingNodeIds[i] = discoveredIds[i];
            }

            hasPendingDiscover = true;
            stableDiscoverFrames = 1;
            return false;
        }

        stableDiscoverFrames++;
        if (stableDiscoverFrames < StableFramesToLock)
            return false;

        StoreCache(
            origin, addonW, addonH,
            discoveredPos, discoveredW, discoveredH, discoveredLeft, discoveredIds, discoveredBottom,
            lockNodeIds: true);
        ApplyCache(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft);
        hasPendingDiscover = false;
        LogDiag(log, $"plate-slots: locked live anchors after {StableFramesToLock} stable frames.");
        return true;
    }

    private static bool PendingMatches(Span<Vector2> positions, Span<uint> nodeIds)
    {
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            if (PendingNodeIds[i] != nodeIds[i])
                return false;
            if (Vector2.DistanceSquared(PendingScreenPos[i], positions[i]) > 9f) // >3px drift
                return false;
        }

        return true;
    }

    private static void CopySlots(
        Span<Vector2> srcPos,
        Span<float> srcW,
        Span<float> srcH,
        Span<bool> srcLeft,
        Span<Vector2> dstPos,
        Span<float> dstW,
        Span<float> dstH,
        Span<bool> dstLeft)
    {
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            dstPos[i] = srcPos[i];
            dstW[i] = srcW[i];
            dstH[i] = srcH[i];
            dstLeft[i] = srcLeft[i];
        }
    }

    /// <summary>
    /// Reject first-open ATK layouts whose row pitch is nonsense vs the plate height
    /// (common before the addon finishes scaling/layout).
    /// </summary>
    private static bool LayoutPitchIsSane(Span<Vector2> screenPositions, Vector2 origin, float addonH)
    {
        if (addonH < 32f)
            return false;

        var expectedPitch = addonH * (PaperdollLastRowYFrac - PaperdollFirstRowYFrac) / 5f;
        if (expectedPitch < 4f)
            return false;

        var sum = 0f;
        for (var i = 1; i < 6; i++)
        {
            var dy = screenPositions[LeftColumnSlots[i]].Y - screenPositions[LeftColumnSlots[i - 1]].Y;
            if (dy < 4f)
                return false;
            sum += dy;
        }

        var avg = sum / 5f;
        if (avg < expectedPitch * 0.50f || avg > expectedPitch * 1.65f)
            return false;

        var firstY = screenPositions[LeftColumnSlots[0]].Y - origin.Y;
        var expectedFirst = addonH * PaperdollFirstRowYFrac;
        if (Math.Abs(firstY - expectedFirst) > addonH * 0.12f)
            return false;

        return true;
    }

    /// <summary>
    /// Keep live X when available; always replace Y (+ icon height) with the calibrated paperdoll
    /// grid. ATK slot ScreenY / Width×Scale stay compressed toward the center at every HUD scale.
    /// </summary>
    private static void FinishLayout(
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        bool persist)
    {
        Span<float> liveX = stackalloc float[GlamourPlateSlotMap.SlotCount];
        Span<float> liveW = stackalloc float[GlamourPlateSlotMap.SlotCount];
        var keepLiveX = false;
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            liveX[i] = screenPositions[i].X;
            liveW[i] = widths[i];
            if (liveX[i] > origin.X + addonW * 0.04f)
                keepLiveX = true;
        }

        ApplyPaperdollLayout(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft);

        if (keepLiveX)
        {
            for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
            {
                if (liveX[i] <= origin.X + 1f)
                    continue;
                screenPositions[i] = new Vector2(liveX[i], screenPositions[i].Y);
                if (liveW[i] > 8f)
                    widths[i] = liveW[i];
            }
        }

        // Inward: left column → button on right of slot; right column → button on left of slot.
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
            buttonOnLeft[i] = !IsLeftColumnSlot(i);

        if (persist)
            StoreRelativeFromScreen(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft);
    }

    private static void ApplyPaperdollLayout(
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        var icon = Math.Max(8f, addonH * PaperdollIconHFrac);
        var firstY = origin.Y + addonH * PaperdollFirstRowYFrac;
        var lastY = origin.Y + addonH * PaperdollLastRowYFrac;
        var pitch = (lastY - firstY) / 5f;
        var leftX = origin.X + addonW * PaperdollLeftXFrac;
        var rightX = origin.X + addonW * PaperdollRightXFrac - icon;

        for (var row = 0; row < 6; row++)
        {
            var y = firstY + row * pitch;
            var leftSlot = LeftColumnSlots[row];
            var rightSlot = RightColumnSlots[row];

            screenPositions[leftSlot] = new Vector2(leftX, y);
            widths[leftSlot] = icon;
            heights[leftSlot] = icon;
            buttonOnLeft[leftSlot] = false; // inward

            screenPositions[rightSlot] = new Vector2(rightX, y);
            widths[rightSlot] = icon;
            heights[rightSlot] = icon;
            buttonOnLeft[rightSlot] = true; // inward
        }
    }

    private static bool TryGetAddonSize(AtkUnitBase* unit, AtkUnitBasePtr addon, out float width, out float height)
    {
        width = height = 0;
        var root = unit->RootNode;
        if (root != null)
        {
            width = root->Width * Math.Max(root->ScaleX, 0.01f);
            height = root->Height * Math.Max(root->ScaleY, 0.01f);
            if (width > 8f && height > 8f)
                return true;
        }

        width = addon.ScaledWidth;
        height = addon.ScaledHeight;
        return width > 8f && height > 8f;
    }

    private static bool TryGetAddonOrigin(AtkUnitBase* unit, AtkUnitBasePtr addon, out Vector2 origin)
    {
        var vp = ImGuiHelpers.MainViewport.Pos;
        var root = unit->RootNode;
        if (root != null && root->ScreenX > 1f && root->ScreenY > 1f)
        {
            origin = vp + new Vector2(root->ScreenX, root->ScreenY);
            return true;
        }

        if (addon.X > 1f || addon.Y > 1f)
        {
            origin = vp + new Vector2(addon.X, addon.Y);
            return true;
        }

        origin = default;
        return false;
    }

    private static void ApplyCache(
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            // Cached* are fractions of addon size (scale-safe).
            screenPositions[i] = origin + new Vector2(
                CachedRelativePos[i].X * addonW,
                CachedRelativePos[i].Y * addonH);
            widths[i] = Math.Max(8f, CachedWidths[i] * addonW);
            heights[i] = Math.Max(8f, CachedHeights[i] * addonH);
            buttonOnLeft[i] = CachedButtonOnLeft[i];
        }
    }

    private static void StoreRelativeFromScreen(
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        if (addonW < 1f || addonH < 1f)
            return;

        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            CachedRelativePos[i] = new Vector2(
                (screenPositions[i].X - origin.X) / addonW,
                (screenPositions[i].Y - origin.Y) / addonH);
            CachedWidths[i] = widths[i] / addonW;
            CachedHeights[i] = heights[i] / addonH;
            CachedButtonOnLeft[i] = buttonOnLeft[i];
        }

        cachedAddonW = addonW;
        cachedAddonH = addonH;
        cacheValid = true;
    }

    private static void StoreCache(
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        Span<uint> nodeIds,
        Span<bool> bottomAnchored,
        bool lockNodeIds)
    {
        StoreRelativeFromScreen(origin, addonW, addonH, screenPositions, widths, heights, buttonOnLeft);
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            CachedNodeIds[i] = nodeIds[i];
            CachedBottomAnchor[i] = bottomAnchored[i];
        }

        var idCount = 0;
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            if (nodeIds[i] != 0)
                idCount++;
        }

        nodeIdsLocked = lockNodeIds && idCount >= GlamourPlateSlotMap.SlotCount;
    }

    private static bool TryResolveByNodeIds(
        AtkUnitBase* unit,
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        var vp = ImGuiHelpers.MainViewport.Pos;
        var resolved = 0;
        Span<bool> bottomAnchored = stackalloc bool[GlamourPlateSlotMap.SlotCount];

        for (var slot = 0; slot < GlamourPlateSlotMap.SlotCount; slot++)
        {
            buttonOnLeft[slot] = CachedButtonOnLeft[slot];
            bottomAnchored[slot] = CachedBottomAnchor[slot];
            widths[slot] = Math.Max(8f, CachedWidths[slot] * addonW);
            heights[slot] = Math.Max(8f, CachedHeights[slot] * addonH);

            var nodeId = CachedNodeIds[slot];
            var expected = origin + new Vector2(
                CachedRelativePos[slot].X * addonW,
                CachedRelativePos[slot].Y * addonH);
            if (nodeId != 0 && TryFindBestNode(unit, nodeId, expected, out var node))
            {
                if (TryGetRawSlotPoint(node, out var gx, out var gy, out var bottom))
                {
                    var x = vp.X + gx;
                    var y = vp.Y + gy;
                    if (x > 1f && y > 1f)
                    {
                        screenPositions[slot] = new Vector2(x, y);
                        bottomAnchored[slot] = bottom;
                        resolved++;
                        continue;
                    }
                }
            }

            screenPositions[slot] = expected;
        }

        if (resolved < 10)
            return false;

        // Rebuild tops/sizes from row pitch — never trust Width*Scale at odd HUD scales.
        NormalizeSlotsFromRowPitch(screenPositions, widths, heights, bottomAnchored);
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
            CachedBottomAnchor[i] = bottomAnchored[i];
        return true;
    }

    private static bool TryFindBestNode(AtkUnitBase* unit, uint nodeId, Vector2 expectedImGui, out AtkResNode* best)
    {
        best = null;
        AtkResNode* found = null;
        var bestDist = float.MaxValue;
        var vp = ImGuiHelpers.MainViewport.Pos;

        void Consider(AtkResNode* node)
        {
            if (node == null || node->NodeId != nodeId)
                return;
            if (node->Type != NodeType.Component
                && node->Type != NodeType.Collision)
                return;
            if (node->Type == NodeType.Component
                && node->GetAsAtkComponentDragDrop() == null
                && node->GetAsAtkComponentIcon() == null)
                return;

            if (!TryGetRawSlotPoint(node, out var gx, out var gy, out _))
                return;

            var pos = vp + new Vector2(gx, gy);
            var dist = Vector2.DistanceSquared(pos, expectedImGui);
            if (dist < bestDist)
            {
                bestDist = dist;
                found = node;
            }
        }

        if (unit->UldManager.NodeList != null)
        {
            for (var i = 0; i < unit->UldManager.NodeListCount; i++)
                Consider(unit->UldManager.NodeList[i]);
        }

        if (unit->RootNode != null)
            WalkDeep(unit->RootNode, Consider);

        best = found;
        return best != null;
    }

    private static bool ValidateLayout(
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            if (screenPositions[i].X <= 1f || screenPositions[i].Y <= 1f)
                return false;
            if (widths[i] < 20f || heights[i] < 20f)
                return false;
        }

        var leftX = AverageColumnX(screenPositions, LeftColumnSlots);
        var rightX = AverageColumnX(screenPositions, RightColumnSlots);
        if (rightX - leftX < 80f)
            return false;

        for (var i = 0; i < 6; i++)
        {
            if (!buttonOnLeft[LeftColumnSlots[i]] || buttonOnLeft[RightColumnSlots[i]])
                return false;
        }

        // Column X must be tight — rejects piled/mis-assigned empty-slot clusters.
        if (!ColumnXIsTight(screenPositions, LeftColumnSlots, widths) ||
            !ColumnXIsTight(screenPositions, RightColumnSlots, widths))
            return false;

        // Rows monotonically down with consistent pitch.
        if (!ColumnPitchIsEven(screenPositions, LeftColumnSlots, heights) ||
            !ColumnPitchIsEven(screenPositions, RightColumnSlots, heights))
            return false;

        return true;
    }

    private static bool ColumnXIsTight(Span<Vector2> screenPositions, int[] slots, Span<float> widths)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        var avgW = 0f;
        foreach (var slot in slots)
        {
            min = Math.Min(min, screenPositions[slot].X);
            max = Math.Max(max, screenPositions[slot].X);
            avgW += widths[slot];
        }

        avgW /= slots.Length;
        return max - min <= avgW * 0.45f;
    }

    private static bool ColumnPitchIsEven(Span<Vector2> screenPositions, int[] slots, Span<float> heights)
    {
        var pitches = new float[5];
        for (var i = 1; i < 6; i++)
        {
            var dy = screenPositions[slots[i]].Y - screenPositions[slots[i - 1]].Y;
            if (dy <= 8f)
                return false;
            pitches[i - 1] = dy;
        }

        var avg = pitches.Average();
        var heightSum = 0f;
        foreach (var slot in slots)
            heightSum += heights[slot];
        var avgH = heightSum / slots.Length;
        if (avg < avgH * 0.7f || avg > avgH * 2.4f)
            return false;

        foreach (var p in pitches)
        {
            if (Math.Abs(p - avg) > avg * 0.28f)
                return false;
        }

        return true;
    }

    private static float AverageColumnX(Span<Vector2> screenPositions, int[] slots)
    {
        var sum = 0f;
        foreach (var slot in slots)
            sum += screenPositions[slot].X;
        return sum / slots.Length;
    }

    private static bool TryDiscoverSlotNodes(
        AtkUnitBase* unit,
        AtkUnitBasePtr addon,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        Span<uint> nodeIds,
        Span<bool> bottomAnchored,
        IPluginLog? log)
    {
        var candidates = new List<Candidate>(32);
        CollectSlotCandidates(unit, addon, candidates);

        var unique = Dedupe(candidates.OrderBy(c => c.ScreenY).ThenBy(c => c.ScreenX).ToList());
        if (unique.Count < GlamourPlateSlotMap.SlotCount)
        {
            LogDiag(log, $"plate-slots: only {unique.Count} unique nodes (need 12).");
            return false;
        }

        // Size filter uses rough local sizes only for clustering, not for final placement.
        var medianSize = unique
            .Select(c => (c.RoughW + c.RoughH) * 0.5f)
            .OrderBy(s => s)
            .ElementAt(unique.Count / 2);
        var sized = unique
            .Where(c => Math.Abs(((c.RoughW + c.RoughH) * 0.5f) - medianSize) <= medianSize * 0.5f)
            .ToList();
        if (sized.Count < GlamourPlateSlotMap.SlotCount)
            sized = unique;

        if (!TryAssignTwoColumns(sized, medianSize, screenPositions, widths, heights, buttonOnLeft, nodeIds, bottomAnchored))
            return false;

        // Final tops + icon size from row pitch (correct at every HUD scale).
        NormalizeSlotsFromRowPitch(screenPositions, widths, heights, bottomAnchored);
        return true;
    }

    private static void CollectSlotCandidates(AtkUnitBase* unit, AtkUnitBasePtr addon, List<Candidate> candidates)
    {
        var addonTop = addon.Y;
        var addonBottom = addon.Y + addon.ScaledHeight;
        var addonLeft = addon.X;
        var addonRight = addon.X + addon.ScaledWidth;
        var minY = addonTop + addon.ScaledHeight * 0.04f;
        var maxY = addonBottom - addon.ScaledHeight * 0.10f;
        var minIcon = Math.Max(16f, addon.ScaledHeight * 0.025f);
        var maxIcon = Math.Max(minIcon + 8f, addon.ScaledHeight * 0.14f);
        var vp = ImGuiHelpers.MainViewport.Pos;

        void Consider(AtkResNode* node)
        {
            if (node == null)
                return;

            if (!TryGetRawSlotPoint(node, out var x, out var y, out var bottom))
                return;

            // Rough size for filtering/clustering only (not used for final icon height).
            var roughW = node->Width * Math.Max(node->ScaleX, 0.01f);
            var roughH = node->Height * Math.Max(node->ScaleY, 0.01f);
            if (roughW < minIcon * 0.5f || roughH < minIcon * 0.5f)
                return;
            if (roughW > maxIcon * 2f || roughH > maxIcon * 2f)
                return;

            if (x < addonLeft - 8f || x > addonRight + 8f)
                return;
            if (y < minY || y > maxY)
                return;

            candidates.Add(new Candidate(vp.X + x, vp.Y + y, roughW, roughH, node->NodeId, bottom));
        }

        if (unit->UldManager.NodeList != null)
        {
            for (var i = 0; i < unit->UldManager.NodeListCount; i++)
                Consider(unit->UldManager.NodeList[i]);
        }

        if (unit->CollisionNodeList != null)
        {
            for (var i = 0; i < unit->CollisionNodeListCount; i++)
                Consider(unit->CollisionNodeList[i]);
        }

        if (unit->RootNode != null)
            WalkDeep(unit->RootNode, Consider);
    }

    /// <summary>Raw ATK ScreenX/Y — no height correction (Height*Scale is wrong at 120–180% HUD).</summary>
    private static bool TryGetRawSlotPoint(AtkResNode* node, out float x, out float y, out bool bottomAnchor)
    {
        x = y = 0;
        bottomAnchor = false;
        if (node == null)
            return false;

        var accept = false;
        if (node->Type == NodeType.Component)
        {
            if (node->GetAsAtkComponentDragDrop() != null)
                accept = true;
            else if (node->GetAsAtkComponentIcon() != null)
            {
                accept = true;
                bottomAnchor = true;
            }
        }
        else if (node->Type == NodeType.Collision)
        {
            accept = true;
        }

        if (!accept)
            return false;

        x = node->ScreenX;
        y = node->ScreenY;
        return x > 1f && y > 1f;
    }

    /// <summary>
    /// Icon size + top Y from median row pitch. ScreenY deltas are reliable at every HUD scale;
    /// Width×Scale (and parent-scale products) are not.
    /// </summary>
    private static void NormalizeSlotsFromRowPitch(
        Span<Vector2> positions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> bottomAnchored)
    {
        NormalizeColumnFromPitch(positions, widths, heights, bottomAnchored, LeftColumnSlots);
        NormalizeColumnFromPitch(positions, widths, heights, bottomAnchored, RightColumnSlots);
    }

    private static void NormalizeColumnFromPitch(
        Span<Vector2> positions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> bottomAnchored,
        int[] slots)
    {
        var pitches = new float[5];
        for (var i = 0; i < 5; i++)
            pitches[i] = positions[slots[i + 1]].Y - positions[slots[i]].Y;

        Array.Sort(pitches);
        var pitch = pitches[2];
        if (pitch < 8f)
            return;

        // Gear icons fill most of the row pitch; remainder is the gap between frames.
        var icon = pitch * 0.82f;
        for (var i = 0; i < 6; i++)
        {
            var slot = slots[i];
            var rawY = positions[slot].Y;
            var topY = bottomAnchored[slot] ? rawY - icon : rawY;
            positions[slot] = new Vector2(positions[slot].X, topY);
            widths[slot] = icon;
            heights[slot] = icon;
        }
    }

    private delegate void NodeHandler(AtkResNode* node);

    private static void WalkDeep(AtkResNode* root, NodeHandler visit)
    {
        for (var node = root; node != null; node = node->NextSiblingNode)
        {
            visit(node);

            if (node->ChildNode != null)
                WalkDeep(node->ChildNode, visit);

            if (node->Type == NodeType.Component)
            {
                var componentNode = (AtkComponentNode*)node;
                if (componentNode->Component != null
                    && componentNode->Component->UldManager.RootNode != null)
                {
                    WalkDeep(componentNode->Component->UldManager.RootNode, visit);
                }
            }
        }
    }

    private static bool TryAssignTwoColumns(
        List<Candidate> sized,
        float medianSize,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        Span<uint> nodeIds,
        Span<bool> bottomAnchored)
    {
        var byX = sized.OrderBy(c => c.ScreenX).ToList();
        var bestGap = 0f;
        var bestSplit = -1;
        for (var i = 0; i < byX.Count - 1; i++)
        {
            var edgeGap = byX[i + 1].ScreenX - byX[i].ScreenX;
            var gap = byX[i + 1].ScreenX - (byX[i].ScreenX + byX[i].RoughW);
            var score = Math.Max(gap, edgeGap - medianSize);
            if (score > bestGap)
            {
                bestGap = score;
                bestSplit = i + 1;
            }
        }

        if (bestSplit < 0 || bestGap < medianSize * 1.2f)
            return false;

        var left = Dedupe(byX.Take(bestSplit).OrderBy(c => c.ScreenY).ToList());
        var right = Dedupe(byX.Skip(bestSplit).OrderBy(c => c.ScreenY).ToList());
        if (left.Count < 6 || right.Count < 6)
            return false;

        left = PickSixRows(left, medianSize);
        right = PickSixRows(right, medianSize);
        if (left.Count != 6 || right.Count != 6)
            return false;

        var leftSpan = left[^1].ScreenY - left[0].ScreenY;
        var rightSpan = right[^1].ScreenY - right[0].ScreenY;
        if (leftSpan < medianSize * 3.0f || rightSpan < medianSize * 3.0f)
            return false;

        AssignColumn(left, LeftColumnSlots, buttonOnLeftSide: true, screenPositions, widths, heights, buttonOnLeft, nodeIds, bottomAnchored);
        AssignColumn(right, RightColumnSlots, buttonOnLeftSide: false, screenPositions, widths, heights, buttonOnLeft, nodeIds, bottomAnchored);
        return true;
    }

    private static List<Candidate> PickSixRows(List<Candidate> columnSortedByY, float medianSize)
    {
        if (columnSortedByY.Count == 6)
            return columnSortedByY;
        if (columnSortedByY.Count < 6)
            return columnSortedByY;

        var best = columnSortedByY.Take(6).ToList();
        var bestScore = float.MaxValue;
        for (var start = 0; start <= columnSortedByY.Count - 6; start++)
        {
            var window = columnSortedByY.Skip(start).Take(6).ToList();
            var pitches = new float[5];
            for (var i = 0; i < 5; i++)
                pitches[i] = window[i + 1].ScreenY - window[i].ScreenY;

            var avg = pitches.Average();
            if (avg < medianSize * 0.65f || avg > medianSize * 2.6f)
                continue;

            var variance = pitches.Sum(p => (p - avg) * (p - avg));
            if (variance < bestScore)
            {
                bestScore = variance;
                best = window;
            }
        }

        return best;
    }

    private static void AssignColumn(
        List<Candidate> column,
        int[] slotOrder,
        bool buttonOnLeftSide,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        Span<uint> nodeIds,
        Span<bool> bottomAnchored)
    {
        for (var i = 0; i < 6; i++)
        {
            var slot = slotOrder[i];
            var node = column[i];
            // Store RAW ScreenY here; NormalizeSlotsFromRowPitch converts to top + size.
            screenPositions[slot] = new Vector2(node.ScreenX, node.ScreenY);
            widths[slot] = node.RoughW;
            heights[slot] = node.RoughH;
            buttonOnLeft[slot] = buttonOnLeftSide;
            nodeIds[slot] = node.NodeId;
            bottomAnchored[slot] = node.BottomAnchor;
        }
    }

    private static List<Candidate> Dedupe(List<Candidate> ordered)
    {
        var unique = new List<Candidate>(ordered.Count);
        foreach (var c in ordered)
        {
            if (unique.Any(u => Math.Abs(u.ScreenX - c.ScreenX) < 12f && Math.Abs(u.ScreenY - c.ScreenY) < 12f))
                continue;
            unique.Add(c);
        }

        return unique;
    }

    /// <summary>
    /// Character-centered paperdoll estimate — independent of filled/empty slot node trees.
    /// </summary>
    private static void FillGeometricFallback(
        AtkUnitBasePtr addon,
        Vector2 origin,
        float addonW,
        float addonH,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        ApplyPaperdollLayout(
            origin,
            addonW,
            addonH,
            screenPositions,
            widths,
            heights,
            buttonOnLeft);
    }

    private static void LogDiag(IPluginLog? log, string message)
    {
        var now = DateTime.UtcNow;
        if (now < nextDiagUtc)
            return;

        nextDiagUtc = now.AddSeconds(8);
        log?.Information(message);

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlamourTrackerPlus",
                "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "app.log"),
                $"{now:yyyy-MM-ddTHH:mm:ssZ} [INFO] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private readonly record struct Candidate(
        float ScreenX,
        float ScreenY,
        float RoughW,
        float RoughH,
        uint NodeId,
        bool BottomAnchor);
}
