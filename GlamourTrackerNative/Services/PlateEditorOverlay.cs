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

    // Reroll button side length as a fraction of the plate window height (tracks UI scale).
    private const float SlotRerollButtonHFrac = 0.036f;

    private static readonly ImGuiWindowFlags HelperWindowFlags =
        ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoDecoration
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoNavFocus
        | ImGuiWindowFlags.NoNavInputs
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoBringToFrontOnFocus
        | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoDocking;

    /// <summary>Fixed-size slot buttons — AutoResize + font tricks clipped the bottom/right edges.</summary>
    private static readonly ImGuiWindowFlags SlotHelperWindowFlags =
        ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoDecoration
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoNavFocus
        | ImGuiWindowFlags.NoNavInputs
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoBringToFrontOnFocus
        | ImGuiWindowFlags.NoDocking;

    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourPlateRandomizer plateRandomizer;
    private readonly Action openTracker;
    private readonly Action refreshAll;
    private readonly IFramework framework;
    private bool wasPlateEditorVisible;
    private bool? plateVisibleCached;
    private bool? dresserVisibleCached;
    private bool? armoireVisibleCached;

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
        // Fresh visibility lookups once per draw — IsPlateEditorVisible used to hit GetAddonByName ×3.
        this.plateVisibleCached = null;
        this.dresserVisibleCached = null;
        this.armoireVisibleCached = null;

        var config = this.getConfiguration();
        if (!config.Enabled)
            return;

        var visible = IsPlateEditorVisible();
        if (visible && !this.wasPlateEditorVisible)
            PlateSlotNodeLocator.ClearCache(); // fresh open — don't keep a premature lock
        this.wasPlateEditorVisible = visible;

        if (!visible)
        {
            PlateSlotNodeLocator.ClearCache();
            return;
        }

        var addon = this.gameGui.GetAddonByName(PlateAddonName, 1);
        if (addon == null || !addon.IsReady || !addon.IsVisible)
        {
            PlateSlotNodeLocator.ClearCache();
            this.wasPlateEditorVisible = false;
            return;
        }

        var styleVars = 0;
        var styleColors = 0;
        if (config.UseLocalUiStyle)
        {
            config.LocalUiTheme ??= PluginLocalUiTheme.CreateDefault();
            (styleVars, styleColors) = config.LocalUiTheme.Push();
        }

        try
        {
            if (config.ShowPlateEditorOverlay)
                DrawTopBar(addon, config);

            if (config.ShowSlotRerollButtons)
                DrawSlotRerollButtons(addon, config);
        }
        finally
        {
            PluginLocalUiTheme.Pop(styleVars, styleColors);
        }
    }

    private void DrawTopBar(AtkUnitBasePtr addon, Configuration config)
    {
        var drawPos = ComputeDrawPos(addon, config.PlateEditorOverlayOnRight);
        if (drawPos == null)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);

        // Glamaholic lock: Appearing for first frame, SetWindowPos every frame after draw.
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
                config,
                slots,
                widths,
                heights,
                buttonOnLeft,
                Plugin.Log))
            return;

        var busy = this.plateRandomizer.IsBusy;
        var sourcesOk = config.RandomizeIncludeDresser || config.RandomizeIncludeArmoire;
        var buttonLabel = FontAwesomeIcon.Sync.ToIconString();

        // Size with the plate window, not Dalamud GlobalScale — so 80%/200% HUD stay proportional.
        var plateH = Math.Max(addon.ScaledHeight, 1f);
        unsafe
        {
            var root = unit->RootNode;
            if (root != null)
            {
                var rootH = root->Height * Math.Max(root->ScaleY, 0.01f);
                if (rootH > 8f)
                    plateH = rootH;
            }
        }

        // Button + icon both track plate on-screen height (dresser/plate window scale).
        var buttonSide = Math.Max(12f, plateH * SlotRerollButtonHFrac);
        var buttonSize = new Vector2(buttonSide, buttonSide);
        // FA Sync ink fills most of the em-box; ~55% of the button leaves visible padding.
        var iconPx = buttonSide * 0.55f;

        var gap = Math.Clamp(config.SlotRerollGap, 0f, 40f) * (plateH / 700f);
        var nudgeX = config.SlotRerollNudgeX * (plateH / 700f);
        var nudgeY = config.SlotRerollNudgeY * (plateH / 700f);

        Span<Vector2> buttonPositions = stackalloc Vector2[GlamourPlateSlotMap.SlotCount];
        Span<bool> buttonActive = stackalloc bool[GlamourPlateSlotMap.SlotCount];
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var any = false;

        for (var slot = 0; slot < GlamourPlateSlotMap.SlotCount; slot++)
        {
            buttonActive[slot] = false;
            var slotPos = slots[slot];
            if (slotPos.X <= 1f || slotPos.Y <= 1f)
                continue;

            var iconW = widths[slot] > 1f ? widths[slot] : heights[slot];
            var iconH = heights[slot] > 1f ? heights[slot] : iconW;
            if (iconW < 8f || iconH < 8f)
                continue;

            var slotCenter = slotPos + new Vector2(iconW, iconH) * 0.5f;
            var sideSign = buttonOnLeft[slot] ? -1f : 1f;
            var towardCenter = PlateSlotNodeLocator.IsLeftColumnSlot(slot) ? 1f : -1f;
            var buttonCenter = new Vector2(
                slotCenter.X + sideSign * (iconW * 0.5f + gap + buttonSize.X * 0.5f) + towardCenter * nudgeX,
                slotCenter.Y + nudgeY);
            var pos = buttonCenter - buttonSize * 0.5f;
            buttonPositions[slot] = pos;
            buttonActive[slot] = true;
            any = true;
            minX = MathF.Min(minX, pos.X);
            minY = MathF.Min(minY, pos.Y);
            maxX = MathF.Max(maxX, pos.X + buttonSize.X);
            maxY = MathF.Max(maxY, pos.Y + buttonSize.Y);
        }

        if (!any)
            return;

        var windowPos = new Vector2(minX, minY);
        var windowSize = new Vector2(MathF.Max(1f, maxX - minX), MathF.Max(1f, maxY - minY));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);

        ImGui.SetNextWindowPos(windowPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
        var began = ImGui.Begin($"##{SlotOverlayId}", SlotHelperWindowFlags);
        ImGui.PopStyleVar(5);

        if (!began)
        {
            ImGui.End();
            return;
        }

        for (var slot = 0; slot < GlamourPlateSlotMap.SlotCount; slot++)
        {
            if (!buttonActive[slot])
                continue;

            var pos = buttonPositions[slot];
            ImGui.SetCursorScreenPos(pos);

            // Invisible hit-target + manual icon draw — Button() leaves FA glyphs optically left of center.
            ImGui.BeginDisabled(!sourcesOk);
            var pressed = ImGui.InvisibleButton($"##{SlotOverlayId}-hit-{slot}", buttonSize);
            if (pressed && !busy)
                StartRandomizeSlot(slot);

            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var rounding = ImGui.GetStyle().FrameRounding;
            var bgCol = active ? ImGuiCol.ButtonActive : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button;
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, max, ImGui.GetColorU32(bgCol), rounding);
            draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), rounding, ImDrawFlags.None, 2f);

            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                var font = ImGui.GetFont();
                var clip = new Vector4(min.X, min.Y, max.X, max.Y);
                var textPos = new Vector2(
                    min.X + (buttonSize.X - iconPx) * 0.5f + iconPx * 0.12f,
                    min.Y + (buttonSize.Y - iconPx) * 0.5f);
                font.RenderText(
                    draw,
                    iconPx,
                    textPos,
                    ImGui.GetColorU32(ImGuiCol.Text),
                    clip,
                    buttonLabel,
                    wrapWidth: 0f,
                    cpuFineClip: false);
            }

            ImGui.EndDisabled();
            if (hovered)
                ImGui.SetTooltip($"Randomize {GlamourPlateSlotMap.Labels[slot]}");
        }

        ImGui.SetWindowPos(windowPos);
        ImGui.End();
    }

    private void DrawControls(Configuration config)
    {
        var busy = this.plateRandomizer.IsBusy;
        var sourcesOk = config.RandomizeIncludeDresser || config.RandomizeIncludeArmoire;
        ImGui.BeginDisabled(!sourcesOk);
        if (ImGui.Button("Randomize plate") && !busy)
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

#if GLAMOUR_DEV
        if (ImGui.BeginMenu("Adjust slot button positions"))
        {
            if (DrawSlotRerollPlacementControls(config, "overlay"))
                config.Save();
            ImGui.EndMenu();
        }
#endif

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

    /// <summary>Shared sliders for manual slot-button placement. Returns true if anything changed.</summary>
    internal static bool DrawSlotRerollPlacementControls(Configuration config, string idSuffix)
    {
        var changed = false;
        ImGui.TextDisabled("Tune while the plate editor is open. Values save automatically.");
        ImGui.PushItemWidth(220f * ImGuiHelpers.GlobalScale);

        var firstY = config.SlotRerollFirstRowY * 100f;
        if (ImGui.SliderFloat($"First row (top %)##{idSuffix}", ref firstY, 5f, 70f, "%.1f%%"))
        {
            config.SlotRerollFirstRowY = firstY / 100f;
            changed = true;
        }

        var lastY = config.SlotRerollLastRowY * 100f;
        if (ImGui.SliderFloat($"Last row (top %)##{idSuffix}", ref lastY, 20f, 95f, "%.1f%%"))
        {
            config.SlotRerollLastRowY = lastY / 100f;
            changed = true;
        }

        if (config.SlotRerollLastRowY < config.SlotRerollFirstRowY + 0.05f)
            config.SlotRerollLastRowY = config.SlotRerollFirstRowY + 0.05f;

        var leftX = config.SlotRerollLeftColumnX * 100f;
        if (ImGui.SliderFloat($"Left column (%)##{idSuffix}", ref leftX, 1f, 40f, "%.1f%%"))
        {
            config.SlotRerollLeftColumnX = leftX / 100f;
            changed = true;
        }

        var rightX = config.SlotRerollRightColumnX * 100f;
        if (ImGui.SliderFloat($"Right column (%)##{idSuffix}", ref rightX, 60f, 99f, "%.1f%%"))
        {
            config.SlotRerollRightColumnX = rightX / 100f;
            changed = true;
        }

        var icon = config.SlotRerollIconSize * 100f;
        if (ImGui.SliderFloat($"Slot size (%)##{idSuffix}", ref icon, 2f, 12f, "%.1f%%"))
        {
            config.SlotRerollIconSize = icon / 100f;
            changed = true;
        }

        var gap = config.SlotRerollGap;
        if (ImGui.SliderFloat($"Gap from slot##{idSuffix}", ref gap, 0f, 24f, "%.0f"))
        {
            config.SlotRerollGap = gap;
            changed = true;
        }

        var towardCenter = config.SlotRerollTowardCenter;
        if (ImGui.Checkbox($"Place toward character preview##{idSuffix}", ref towardCenter))
        {
            config.SlotRerollTowardCenter = towardCenter;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off = outer edges of the plate. On = beside the preview.");

        var nudgeX = config.SlotRerollNudgeX;
        if (ImGui.SliderFloat($"Nudge sideways##{idSuffix}", ref nudgeX, -40f, 40f, "%.0f"))
        {
            config.SlotRerollNudgeX = nudgeX;
            changed = true;
        }

        var nudgeY = config.SlotRerollNudgeY;
        if (ImGui.SliderFloat($"Nudge up/down##{idSuffix}", ref nudgeY, -40f, 40f, "%.0f"))
        {
            config.SlotRerollNudgeY = nudgeY;
            changed = true;
        }

        ImGui.PopItemWidth();

        if (ImGui.Button($"Reset placement defaults##{idSuffix}"))
        {
            PlateSlotNodeLocator.ResetSlotRerollDefaults(config);
            changed = true;
        }

        return changed;
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
        this.plateVisibleCached ??= IsAddonVisible(PlateAddonName);
        if (!this.plateVisibleCached.Value)
            return false;

        this.dresserVisibleCached ??= IsAddonVisible(DresserAddonName);
        if (this.dresserVisibleCached.Value)
            return true;

        this.armoireVisibleCached ??= IsAddonVisible(ArmoireAddonName);
        return this.armoireVisibleCached.Value;
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
