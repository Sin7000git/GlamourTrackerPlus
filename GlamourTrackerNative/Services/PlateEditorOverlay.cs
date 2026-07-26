using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Windows;

namespace GlamourTracker.Services;

/// <summary>
/// Floating controls above the glamour plate editor, plus per-slot reroll buttons.
/// Defaults to the top-right of the plate window so it does not cover Glamaholic's top-left menu.
/// </summary>
internal sealed class PlateEditorOverlay
{
    private const string PlateAddonName = "MiragePrismMiragePlate";
    private const string DresserAddonName = "MiragePrismPrismBox";
    private const string ArmoireAddonName = "CabinetWithdraw";
    private const string OverlayId = "glamour-tracker-plate-overlay";
    private const string SlotOverlayId = "glamour-tracker-slot-reroll";

    // Tuned against live plate-editor slot nodes (UI-scaled pixels).
    private const float SlotRerollLeftOffsetX = -19.5f;
    private const float SlotRerollLeftOffsetY = 12.5f;
    private const float SlotRerollRightOffsetX = 47f;
    private const float SlotRerollRightOffsetY = 12.5f;

    private static readonly ImGuiWindowFlags HelperWindowFlags =
        ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoDecoration
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoNavFocus
        | ImGuiWindowFlags.NoNavInputs
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoDocking;

    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourPlateRandomizer plateRandomizer;
    private readonly Action openTracker;
    private readonly Action refreshAll;
    private readonly IFramework framework;

    public PlateEditorOverlay(
        IGameGui gameGui,
        IChatGui chatGui,
        IFramework framework,
        Func<Configuration> getConfiguration,
        GlamourPlateRandomizer plateRandomizer,
        Action openTracker,
        Action refreshAll)
    {
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.framework = framework;
        this.getConfiguration = getConfiguration;
        this.plateRandomizer = plateRandomizer;
        this.openTracker = openTracker;
        this.refreshAll = refreshAll;
    }

    public void Draw()
    {
        var config = this.getConfiguration();
        if (!config.Enabled)
            return;

        if (!IsPlateEditorVisible())
            return;

        var addon = this.gameGui.GetAddonByName(PlateAddonName, 1);
        if (addon == null || !addon.IsReady || !addon.IsVisible)
            return;

        if (config.ShowPlateEditorOverlay)
            DrawTopBar(addon, config);

        if (config.ShowSlotRerollButtons)
            DrawSlotRerollButtons(addon, config);
    }

    private void DrawTopBar(AtkUnitBasePtr addon, Configuration config)
    {
        var drawPos = ComputeDrawPos(addon, config.PlateEditorOverlayOnRight);
        if (drawPos == null)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);

        ImGui.SetNextWindowPos(drawPos.Value, ImGuiCond.Appearing);
        var began = ImGui.Begin($"##{OverlayId}", HelperWindowFlags);
        ImGui.PopStyleVar(3);

        if (!began)
        {
            ImGui.End();
            return;
        }

        DrawControls(config);
        ImGui.SetWindowPos(drawPos.Value);
        ImGui.End();
    }

    private unsafe void DrawSlotRerollButtons(AtkUnitBasePtr addon, Configuration config)
    {
        if (addon.Address == nint.Zero)
            return;

        var unit = (AtkUnitBase*)addon.Address;
        Span<Vector2> slots = stackalloc Vector2[GlamourPlateSlotMap.SlotCount];
        Span<float> widths = stackalloc float[GlamourPlateSlotMap.SlotCount];
        Span<float> heights = stackalloc float[GlamourPlateSlotMap.SlotCount];
        Span<bool> buttonOnLeft = stackalloc bool[GlamourPlateSlotMap.SlotCount];
        if (!PlateSlotNodeLocator.TryGetSlotScreenRects(
                unit,
                addon,
                slots,
                widths,
                heights,
                buttonOnLeft,
                Plugin.Log))
            return;

        var busy = this.plateRandomizer.IsBusy;
        var canRandomize = (config.RandomizeIncludeDresser || config.RandomizeIncludeArmoire) && !busy;
        // Size relative to the gear icon so vertical centering is visible (FrameHeight ≈ icon on hiDPI).
        var buttonLabel = FontAwesomeIcon.Sync.ToIconString();
        Vector2 iconTextSize;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            iconTextSize = ImGui.CalcTextSize(buttonLabel);

        for (var slot = 0; slot < GlamourPlateSlotMap.SlotCount; slot++)
        {
            var slotPos = slots[slot];
            if (slotPos.X <= 1f || slotPos.Y <= 1f)
                continue;

            var iconWidth = widths[slot] > 1f ? widths[slot] : 44f * ImGuiHelpers.GlobalScale;
            var iconHeight = heights[slot] > 1f ? heights[slot] : iconWidth;
            var side = Math.Clamp(Math.Min(iconWidth, iconHeight) * 0.42f, 16f * ImGuiHelpers.GlobalScale, 28f * ImGuiHelpers.GlobalScale);
            var buttonSize = new Vector2(
                Math.Max(side, iconTextSize.X + ImGui.GetStyle().FramePadding.X * 2),
                Math.Max(side, iconTextSize.Y + ImGui.GetStyle().FramePadding.Y * 2));
            // Keep buttons outside the icon: left of left-column, right of right-column.
            var gap = 2f * ImGuiHelpers.GlobalScale;
            var y = slotPos.Y + (iconHeight - buttonSize.Y) * 0.5f;
            var pos = buttonOnLeft[slot]
                ? new Vector2(slotPos.X - buttonSize.X - gap, y)
                : new Vector2(slotPos.X + iconWidth + gap, y);

            var g = ImGuiHelpers.GlobalScale;
            pos += buttonOnLeft[slot]
                ? new Vector2(SlotRerollLeftOffsetX, SlotRerollLeftOffsetY) * g
                : new Vector2(SlotRerollRightOffsetX, SlotRerollRightOffsetY) * g;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            var began = ImGui.Begin($"##{SlotOverlayId}-{slot}", HelperWindowFlags);
            ImGui.PopStyleVar(3);

            if (!began)
            {
                ImGui.End();
                continue;
            }

            ImGui.BeginDisabled(!canRandomize);
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                if (ImGui.Button($"{buttonLabel}##slot{slot}", buttonSize))
                    StartRandomizeSlot(slot);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Reroll {GlamourPlateSlotMap.Labels[slot]}");

            ImGui.EndDisabled();
            ImGui.SetWindowPos(pos);
            ImGui.End();
        }
    }

    private void DrawControls(Configuration config)
    {
        var busy = this.plateRandomizer.IsBusy;
        var canRandomize = (config.RandomizeIncludeDresser || config.RandomizeIncludeArmoire) && !busy;
        ImGui.BeginDisabled(!canRandomize);
        if (ImGui.Button("Randomize plate"))
            StartRandomize();
        ImGui.EndDisabled();

        ImGui.SameLine(0, 6);

        var menuLabel = "Glamour Tracker";
        ImGui.SetNextItemWidth(MenuWidth());
        if (!ImGui.BeginCombo($"##{OverlayId}-menu", menuLabel))
            return;

        if (ImGui.Selectable("Open tracker window"))
            this.openTracker();

        ImGui.Separator();

        var includeDresser = config.RandomizeIncludeDresser;
        if (ImGui.Checkbox("Use dresser items", ref includeDresser))
        {
            config.RandomizeIncludeDresser = includeDresser;
            config.Save();
        }

        var includeArmoire = config.RandomizeIncludeArmoire;
        if (ImGui.Checkbox("Use armoire items", ref includeArmoire))
        {
            config.RandomizeIncludeArmoire = includeArmoire;
            config.Save();
        }

        var showSlotButtons = config.ShowSlotRerollButtons;
        if (ImGui.Checkbox("Show reroll on each slot", ref showSlotButtons))
        {
            config.ShowSlotRerollButtons = showSlotButtons;
            config.Save();
        }

        ImGui.Separator();
        if (RandomizeFilterUi.Draw(config, Plugin.DataManager, Plugin.ObjectTable, "overlay"))
            config.Save();

        ImGui.Separator();
        GlamourPlateRandomizer.EnsureLockArray(config);
        if (ImGui.BeginMenu("Slot locks"))
        {
            var locks = config.RandomizeLockedSlots;
            var locksChanged = false;
            for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
            {
                var locked = locks[i];
                if (ImGui.Checkbox(GlamourPlateSlotMap.Labels[i], ref locked))
                {
                    locks[i] = locked;
                    locksChanged = true;
                }
            }

            if (ImGui.Button("Unlock all"))
            {
                Array.Fill(locks, false);
                locksChanged = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("Lock all"))
            {
                Array.Fill(locks, true);
                locksChanged = true;
            }

            if (locksChanged)
                config.Save();

            ImGui.EndMenu();
        }

        ImGui.EndCombo();
    }

    private void StartRandomize()
    {
        _ = this.framework.RunOnFrameworkThread(() =>
        {
            var result = this.plateRandomizer.BeginRandomize(r =>
            {
                if (!r.InProgress)
                    this.chatGui.Print($"[Glamour Tracker] {r.Message}");
                if (r is { Success: true, InProgress: false })
                    this.refreshAll();
            });
            this.chatGui.Print($"[Glamour Tracker] {result.Message}");
        });
    }

    private void StartRandomizeSlot(int slot)
    {
        _ = this.framework.RunOnFrameworkThread(() =>
        {
            var result = this.plateRandomizer.BeginRandomizeSlot(slot, r =>
            {
                if (!r.InProgress)
                    this.chatGui.Print($"[Glamour Tracker] {r.Message}");
                if (r is { Success: true, InProgress: false })
                    this.refreshAll();
            });
            this.chatGui.Print($"[Glamour Tracker] {result.Message}");
        });
    }

    private bool IsPlateEditorVisible()
    {
        if (!IsAddonVisible(PlateAddonName))
            return false;

        return IsAddonVisible(DresserAddonName) || IsAddonVisible(ArmoireAddonName);
    }

    private bool IsAddonVisible(string name)
    {
        var addon = this.gameGui.GetAddonByName(name, 1);
        return addon != null && addon.IsVisible;
    }

    private static Vector2? ComputeDrawPos(AtkUnitBasePtr addon, bool onRight)
    {
        if (addon == null)
            return null;

        var style = ImGui.GetStyle();
        var yOffset = ImGui.CalcTextSize("A").Y + style.FramePadding.Y + style.FrameBorderSize;
        var xModifier = onRight
            ? Math.Max(0f, addon.ScaledWidth - BarWidth())
            : 0f;

        return ImGuiHelpers.MainViewport.Pos
               + new Vector2(addon.X, addon.Y)
               + new Vector2(xModifier, 0)
               - new Vector2(0, yOffset);
    }

    private static float BarWidth()
    {
        var style = ImGui.GetStyle();
        var button = ImGui.CalcTextSize("Randomize plate").X + style.FramePadding.X * 2;
        return (button + 6f) * ImGuiHelpers.GlobalScale + MenuWidth();
    }

    private static float MenuWidth()
    {
        var style = ImGui.GetStyle();
        return (ImGui.CalcTextSize("Glamour Tracker").X
                + style.ItemInnerSpacing.X * 2
                + ImGui.GetFrameHeight()) * ImGuiHelpers.GlobalScale;
    }
}
