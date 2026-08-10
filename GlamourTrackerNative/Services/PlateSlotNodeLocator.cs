using System.Numerics;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

/// <summary>
/// Places the 12 plate-editor slot anchors from manual config (fractions of the plate window).
/// Automatic ATK node discovery was unreliable across HUD scales.
/// </summary>
internal static unsafe class PlateSlotNodeLocator
{
    private static readonly int[] LeftColumnSlots = [0, 2, 3, 4, 5, 6]; // MH, Head, Body, Hands, Legs, Feet
    private static readonly int[] RightColumnSlots = [1, 7, 8, 9, 10, 11]; // OH, Ears, Neck, Wrists, RR, LR
    private static readonly bool[] LeftColumnLookup = BuildLeftColumnLookup();

    public static bool IsLeftColumnSlot(int slot) =>
        (uint)slot < LeftColumnLookup.Length && LeftColumnLookup[slot];

    private static bool[] BuildLeftColumnLookup()
    {
        var lookup = new bool[GlamourPlateSlotMap.SlotCount];
        foreach (var slot in LeftColumnSlots)
            lookup[slot] = true;
        return lookup;
    }

    public static void ClearCache()
    {
        // Layout is fully driven by Configuration each frame.
    }

    public static void InvalidateLock()
    {
        // Kept for Plugin refresh hooks; no-op with manual layout.
    }

    public static void ResetSlotRerollDefaults(Configuration config)
    {
        config.SlotRerollFirstRowY = 0.17f;
        config.SlotRerollLastRowY = 0.591f;
        config.SlotRerollLeftColumnX = 0.137f;
        config.SlotRerollRightColumnX = 0.867f;
        config.SlotRerollIconSize = 0.02f;
        config.SlotRerollTowardCenter = true;
        config.SlotRerollNudgeX = 0f;
        config.SlotRerollNudgeY = 0f;
        config.SlotRerollGap = 0f;
    }

    public static bool TryGetSlotScreenRects(
        AtkUnitBase* unit,
        AtkUnitBasePtr addon,
        Configuration config,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft,
        IPluginLog? log = null)
    {
        if (unit == null || config == null
            || screenPositions.Length < GlamourPlateSlotMap.SlotCount
            || widths.Length < GlamourPlateSlotMap.SlotCount
            || heights.Length < GlamourPlateSlotMap.SlotCount
            || buttonOnLeft.Length < GlamourPlateSlotMap.SlotCount)
            return false;

        if (!TryGetAddonOrigin(unit, addon, out var origin))
            return false;

        if (!TryGetAddonSize(unit, addon, out var addonW, out var addonH))
            return false;

        ApplyPaperdollLayout(origin, addonW, addonH, config, screenPositions, widths, heights, buttonOnLeft);
        _ = log;
        return true;
    }

    private static void ApplyPaperdollLayout(
        Vector2 origin,
        float addonW,
        float addonH,
        Configuration config,
        Span<Vector2> screenPositions,
        Span<float> widths,
        Span<float> heights,
        Span<bool> buttonOnLeft)
    {
        var firstFrac = Math.Clamp(config.SlotRerollFirstRowY, 0.02f, 0.90f);
        var lastFrac = Math.Clamp(config.SlotRerollLastRowY, firstFrac + 0.05f, 0.98f);
        var leftFrac = Math.Clamp(config.SlotRerollLeftColumnX, 0.01f, 0.45f);
        var rightFrac = Math.Clamp(config.SlotRerollRightColumnX, 0.55f, 0.99f);
        var iconFrac = Math.Clamp(config.SlotRerollIconSize, 0.02f, 0.15f);

        var icon = Math.Max(8f, addonH * iconFrac);
        var firstY = origin.Y + addonH * firstFrac;
        var lastY = origin.Y + addonH * lastFrac;
        var pitch = (lastY - firstY) / 5f;
        var leftX = origin.X + addonW * leftFrac;
        var rightX = origin.X + addonW * rightFrac - icon;

        // buttonOnLeft true = draw to the left of the slot.
        var leftButtonsOnLeft = !config.SlotRerollTowardCenter;
        var rightButtonsOnLeft = config.SlotRerollTowardCenter;

        for (var row = 0; row < 6; row++)
        {
            var y = firstY + row * pitch;
            var leftSlot = LeftColumnSlots[row];
            var rightSlot = RightColumnSlots[row];

            screenPositions[leftSlot] = new Vector2(leftX, y);
            widths[leftSlot] = icon;
            heights[leftSlot] = icon;
            buttonOnLeft[leftSlot] = leftButtonsOnLeft;

            screenPositions[rightSlot] = new Vector2(rightX, y);
            widths[rightSlot] = icon;
            heights[rightSlot] = icon;
            buttonOnLeft[rightSlot] = rightButtonsOnLeft;
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
        // Prefer addon.X/Y (same as Glamaholic) — RootNode ScreenX can trail while dragging.
        if (addon.X > 1f || addon.Y > 1f)
        {
            origin = vp + new Vector2(addon.X, addon.Y);
            return true;
        }

        var root = unit->RootNode;
        if (root != null && root->ScreenX > 1f && root->ScreenY > 1f)
        {
            origin = vp + new Vector2(root->ScreenX, root->ScreenY);
            return true;
        }

        origin = default;
        return false;
    }
}
