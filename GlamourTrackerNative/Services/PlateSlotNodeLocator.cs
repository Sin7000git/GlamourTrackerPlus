using System.Numerics;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

/// <summary>
/// Locates the 12 equipment slots on MiragePrismMiragePlate (paperdoll columns either side of the preview).
/// </summary>
internal static unsafe class PlateSlotNodeLocator
{
    private static readonly int[] LeftColumnSlots = [0, 2, 3, 4, 5, 6]; // MH, Head, Body, Hands, Legs, Feet
    private static readonly int[] RightColumnSlots = [1, 7, 8, 9, 10, 11]; // OH, Ears, Neck, Wrists, RR, LR

    private static DateTime nextDiagUtc = DateTime.MinValue;

    public static bool IsLeftColumnSlot(int slot) => LeftColumnSlots.Contains(slot);

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

        if (TryDiscoverSlots(unit, addon, screenPositions, widths, heights, buttonOnLeft, log))
            return true;

        FillGeometricFallback(addon, screenPositions, widths, heights, buttonOnLeft);
        LogDiag(log, "plate-slots: discovery failed — geometric paperdoll fallback.");
        return true;
    }

    private static bool TryDiscoverSlots(
        AtkUnitBase* unit,
        AtkUnitBasePtr addon,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        IPluginLog? log)
    {
        var candidates = new List<Candidate>(32);
        CollectCandidates(unit, addon, candidates);

        var unique = Dedupe(candidates.OrderBy(c => c.ScreenY).ThenBy(c => c.ScreenX).ToList());
        if (unique.Count < GlamourPlateSlotMap.SlotCount)
        {
            LogDiag(log, $"plate-slots: only {unique.Count} unique slot-like nodes (need 12).");
            return false;
        }

        var medianSize = unique
            .Select(c => (c.Width + c.Height) * 0.5f)
            .OrderBy(s => s)
            .ElementAt(unique.Count / 2);
        var sized = unique
            .Where(c => Math.Abs(((c.Width + c.Height) * 0.5f) - medianSize) <= medianSize * 0.4f)
            .ToList();
        if (sized.Count < GlamourPlateSlotMap.SlotCount)
            sized = unique;

        if (!TryAssignTwoColumns(sized, medianSize, screenPositions, widths, heights, buttonOnLeft))
        {
            LogDiag(
                log,
                $"plate-slots: could not split {sized.Count} nodes into two 6-row columns (median={medianSize:0}).");
            return false;
        }

        LogDiag(log, $"plate-slots: anchored to live nodes ({sized.Count} candidates, median={medianSize:0}).");
        return true;
    }

    private static void CollectCandidates(AtkUnitBase* unit, AtkUnitBasePtr addon, List<Candidate> candidates)
    {
        var addonTop = addon.Y;
        var addonBottom = addon.Y + addon.ScaledHeight;
        var addonLeft = addon.X;
        var addonRight = addon.X + addon.ScaledWidth;
        var minY = addonTop + addon.ScaledHeight * 0.06f;
        var maxY = addonBottom - addon.ScaledHeight * 0.14f;

        void Consider(AtkResNode* node)
        {
            if (node == null || !node->IsVisible())
                return;

            if (!TryGetSlotLikeBounds(node, out var x, out var y, out var width, out var height))
                return;

            if (x < addonLeft - 8f || x > addonRight + 8f)
                return;
            if (y < minY || y > maxY)
                return;

            // Equipment icons are roughly square; reject wide chrome / plate-number strips.
            if (width is < 28f or > 110f || height is < 28f or > 110f)
                return;
            if (Math.Abs(width - height) > 28f)
                return;

            candidates.Add(new Candidate(x, y, width, height, node->NodeId));
        }

        // Flat UldManager list (most reliable for this addon).
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

        // Deep walk including component UldManager trees (icons live inside components).
        if (unit->RootNode != null)
            WalkDeep(unit->RootNode, Consider);
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

    private static bool TryGetSlotLikeBounds(
        AtkResNode* node,
        out float x,
        out float y,
        out float width,
        out float height)
    {
        x = y = width = height = 0;
        if (node == null)
            return false;

        var accept = false;
        if (node->Type == NodeType.Component)
        {
            // Plate gear uses Icon and/or DragDrop depending on client build.
            if (node->GetAsAtkComponentDragDrop() != null || node->GetAsAtkComponentIcon() != null)
                accept = true;
        }
        else if (node->Type == NodeType.Image)
        {
            // Inner item artwork — useful when outer component bounds are oversized.
            accept = node->GetAsAtkImageNode() != null;
        }
        else if (node->Type == NodeType.Collision)
        {
            accept = true;
        }

        if (!accept)
            return false;

        x = node->ScreenX;
        y = node->ScreenY;
        width = node->Width * Math.Max(node->ScaleX, 0.01f);
        height = node->Height * Math.Max(node->ScaleY, 0.01f);

        // Image / Icon components report ScreenY at the bottom edge (same as AtkUiHelper).
        if (UsesBottomScreenAnchor(node))
            y -= height;

        if (x <= 1f || y <= 1f || width < 1f || height < 1f)
            return false;

        return true;
    }

    private static bool UsesBottomScreenAnchor(AtkResNode* node)
    {
        if (node->Type == NodeType.Image)
            return true;

        if (node->Type != NodeType.Component)
            return false;

        return node->GetAsAtkComponentIcon() != null;
    }

    private static bool TryAssignTwoColumns(
        List<Candidate> sized,
        float medianSize,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        var byX = sized.OrderBy(c => c.ScreenX).ToList();
        var bestGap = 0f;
        var bestSplit = -1;
        for (var i = 0; i < byX.Count - 1; i++)
        {
            var edgeGap = byX[i + 1].ScreenX - byX[i].ScreenX;
            var gap = byX[i + 1].ScreenX - (byX[i].ScreenX + byX[i].Width);
            var score = Math.Max(gap, edgeGap - medianSize);
            if (score > bestGap)
            {
                bestGap = score;
                bestSplit = i + 1;
            }
        }

        // Columns sit on opposite sides of the character preview.
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

        var vp = ImGuiHelpers.MainViewport.Pos;
        AssignColumn(left, LeftColumnSlots, buttonOnLeftSide: true, vp, screenPositions, widths, heights, buttonOnLeft);
        AssignColumn(right, RightColumnSlots, buttonOnLeftSide: false, vp, screenPositions, widths, heights, buttonOnLeft);
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
        Vector2 viewportPos,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        for (var i = 0; i < 6; i++)
        {
            var slot = slotOrder[i];
            var node = column[i];
            // Match top-bar overlay space: game screen coords + main viewport origin.
            screenPositions[slot] = viewportPos + new Vector2(node.ScreenX, node.ScreenY);
            // Keep true width for right-side placement; height for vertical centering.
            widths[slot] = node.Width;
            heights[slot] = node.Height;
            buttonOnLeft[slot] = buttonOnLeftSide;
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
    /// Character-centered paperdoll estimate used only when live nodes cannot be resolved.
    /// </summary>
    private static void FillGeometricFallback(
        AtkUnitBasePtr addon,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        var origin = ImGuiHelpers.MainViewport.Pos + new Vector2(addon.X, addon.Y);
        var w = addon.ScaledWidth;
        var h = addon.ScaledHeight;

        // Derive sizes from the addon so UI scale matches the plate window, not only ImGui scale.
        var icon = Math.Clamp(h * 0.052f, 36f, 58f);
        var rowPitch = icon + Math.Clamp(h * 0.014f, 6f, 14f);
        var startY = h * 0.205f;
        var midX = w * 0.5f;
        // Columns sit beside the character preview (not against the window chrome).
        var colOffset = w * 0.205f;

        var leftX = origin.X + midX - colOffset - icon;
        var rightX = origin.X + midX + colOffset;

        for (var row = 0; row < 6; row++)
        {
            var y = origin.Y + startY + row * rowPitch;
            var leftSlot = LeftColumnSlots[row];
            var rightSlot = RightColumnSlots[row];

            screenPositions[leftSlot] = new Vector2(leftX, y);
            widths[leftSlot] = icon;
            heights[leftSlot] = icon;
            buttonOnLeft[leftSlot] = true;

            screenPositions[rightSlot] = new Vector2(rightX, y);
            widths[rightSlot] = icon;
            heights[rightSlot] = icon;
            buttonOnLeft[rightSlot] = false;
        }
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
        float Width,
        float Height,
        uint NodeId);
}
