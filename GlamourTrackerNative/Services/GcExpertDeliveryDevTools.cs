#if GLAMOUR_DEV
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

internal sealed unsafe partial class GcExpertDeliveryEnhancer
{
    private const string SheetPickerOverlayId = "glamour-tracker-gc-sheet-picker";
    private int pickerExtraId = (int)EmptyGearSlotAtlas.QolExtraSheetBase;
    private string? pickerStatus;

    public void RecaptureIconTexturePath() => this.iconCache.TryRecaptureTexturePath();

    private void DrawSheetPicker(AtkUnitBasePtr addon)
    {
        var drawPos = ComputeSheetPickerPos(addon);
        if (drawPos == null)
            return;

        ImGui.SetNextWindowPos(drawPos.Value, ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(0.92f);
        if (!ImGui.Begin(
                $"GC icon sheet##{SheetPickerOverlayId}",
                ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoDocking
                | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped(
            "Real sheet is ui/uld/ItemDetailPutIn (not a QoL Extra id). "
            + "Extra picker is only for experiments.");

        if (ImGui.Button("Bake ItemDetailPutIn"))
        {
            var path = this.iconCache.ApplyItemDetailPutInSheet();
            this.lastAtlasSignature = string.Empty;
            this.pickerStatus = $"Baked → {path}";
        }

        ImGui.Separator();
        ImGui.TextUnformatted("QoL Extra sheet id (keeps UV)");
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        var idChanged = ImGui.InputInt("##gcExtraSheetId", ref this.pickerExtraId, 1, 10);
        this.pickerExtraId = Math.Clamp(this.pickerExtraId, (int)EmptyGearSlotAtlas.QolExtraSheetBase, 10_099_999);

        ImGui.SameLine();
        if (ImGui.Button("< Prev"))
        {
            StepKnownExtraSheet(-1);
            idChanged = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Next >"))
        {
            StepKnownExtraSheet(+1);
            idChanged = true;
        }

        var extraId = (uint)this.pickerExtraId;
        var mapped = EmptyGearSlotAtlas.TryGetExtraSheetStem(extraId, out var stem);
        if (mapped)
            ImGui.TextDisabled($"{stem}  (index {extraId - EmptyGearSlotAtlas.QolExtraSheetBase})");
        else
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), "Not in Extra sheet map");

        if (idChanged && mapped)
            ApplyPickerSheet(extraId);

        if (ImGui.Button("Apply Extra id"))
            ApplyPickerSheet(extraId);

        var config = this.getConfiguration();
        ImGui.TextWrapped($"Active: {config.DresserUiIconPath ?? "(none)"}");
        if (!string.IsNullOrWhiteSpace(this.pickerStatus))
            ImGui.TextDisabled(this.pickerStatus);

        ImGui.SetWindowPos(drawPos.Value);
        ImGui.End();
    }

    private void StepKnownExtraSheet(int direction)
    {
        var known = EmptyGearSlotAtlas.KnownExtraSheetIds;
        if (known.Count == 0)
            return;

        var current = (uint)this.pickerExtraId;
        var idx = 0;
        for (var i = 0; i < known.Count; i++)
        {
            if (known[i] >= current)
            {
                idx = i;
                break;
            }

            idx = i;
        }

        if (known[idx] == current)
            idx += direction;
        else if (direction < 0 && known[idx] > current)
            idx--;

        idx = Math.Clamp(idx, 0, known.Count - 1);
        this.pickerExtraId = (int)known[idx];
    }

    private void ApplyPickerSheet(uint extraId)
    {
        if (!this.iconCache.TryApplyExtraSheet(extraId, out var path))
        {
            this.pickerStatus = $"Could not resolve Extra {extraId}";
            return;
        }

        this.lastAtlasSignature = string.Empty; // force marker rebuild with new path
        this.pickerStatus = $"Applied Extra {extraId} → {path}";
    }

    private static Vector2? ComputeSheetPickerPos(AtkUnitBasePtr addon)
    {
        if (addon == null || !addon.IsReady)
            return null;

        var style = ImGui.GetStyle();
        var yOffset = ImGui.CalcTextSize("A").Y + style.FramePadding.Y * 2f + style.WindowPadding.Y * 2f + 8f;
        return ImGuiHelpers.MainViewport.Pos
               + new Vector2(addon.X, addon.Y)
               - new Vector2(0f, yOffset * ImGuiHelpers.GlobalScale);
    }

    public void DebugToChat(IChatGui chat)
    {
        var config = this.getConfiguration();
        if (!config.Enabled)
        {
            chat.Print("[GlamourTracker] Plugin is disabled in settings.");
            return;
        }

        if (!config.ShowGcExpertDeliveryStatus)
        {
            chat.Print("[GlamourTracker] GC expert delivery markers are disabled in settings.");
            return;
        }

        var addonPtr = this.gameGui.GetAddonByName(SupplyAddonName, 1);
        if (addonPtr.Address == nint.Zero)
        {
            chat.Print("[GlamourTracker] GrandCompanySupplyList is not open.");
            return;
        }

        try
        {
            var ptr = (AddonGrandCompanySupplyList*)addonPtr.Address;
            var supplyUnit = (AtkUnitBase*)addonPtr.Address;
            var drawn = SyncNativeMarkers(ptr, supplyUnit);

            var expertItems = GetExpertMatchIndex().Items;
            var list = ptr->ExpertDeliveryList;
            var listCount = list != null ? list->GetItemCount() : 0;
            var firstVisible = list != null ? list->FirstVisibleItemIndex : -1;
            var visibleRows = list != null ? list->NumVisibleRows : (short)0;

            chat.Print(
                $"[GlamourTracker] GC listRows={listCount}, agentExpert={expertItems.Count}, markers={drawn}, firstVisible={firstVisible}, visibleRows={visibleRows}, iconCache={this.iconCache.IsReady}");

            this.iconCache.PrintSliceDebug(chat);

            if (list != null)
                PrintDebugForItemName(chat, list, expertItems, "Voeburtite");

            if (list != null)
            {
                PrintVisibleRowDebug(chat, list, expertItems, 5);
                PrintFirstStoredRowAnchorDebug(chat, list, expertItems);
            }
        }
        catch (Exception ex)
        {
            chat.Print($"[GlamourTracker] GC debug error: {ex.Message}");
        }
    }

    private void PrintDebugForItemName(IChatGui chat, AtkComponentList* list, List<GrandCompanyItem> expertItems, string namePart)
    {
        foreach (var item in expertItems)
        {
            var itemName = item.ItemName.ToString();
            if (!itemName.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                continue;

            var sheetName = GetSheetItemName(item.ItemId);
            var listIndex = FindListIndexForLabel(list, sheetName);
            var visible = listIndex >= 0 && list->IsItemVisible(listIndex);
            chat.Print(
                $"[GlamourTracker] '{sheetName}' id={item.ItemId} icon={item.IconId} listIdx={listIndex} visible={visible} D={IsInDresserForItem(item.ItemId)} A={IsInArmoireForItem(item.ItemId)}");
        }
    }

    private void PrintVisibleRowDebug(IChatGui chat, AtkComponentList* list, List<GrandCompanyItem> expertItems, int maxRows)
    {
        var printed = 0;
        var first = list->FirstVisibleItemIndex;
        var visibleRows = Math.Max(list->NumVisibleRows, (short)1);
        var last = Math.Min(first + visibleRows, list->ListLength);

        for (var itemIndex = first; itemIndex < last && printed < maxRows; itemIndex++)
        {
            if (!list->IsItemVisible(itemIndex))
                continue;

            var renderer = list->GetItemRenderer(itemIndex);
            if (renderer == null)
                continue;

            var rowRoot = GetRowRoot(renderer);
            var gcItem = FindMatchingExpertItem(GetExpertMatchIndex(), list, itemIndex, rowRoot);
            if (gcItem == null)
            {
                var rowLabel = GetRowDisplayLabel(list, itemIndex, rowRoot);
                chat.Print($"[GlamourTracker] listIdx={itemIndex}: no match (rowIcon={GetRowIconId(rowRoot)}, label='{Truncate(rowLabel, 40)}')");
                printed++;
                continue;
            }

            var itemId = gcItem.Value.ItemId;
            chat.Print(
                $"[GlamourTracker] listIdx={itemIndex}: {gcItem.Value.ItemName} id={itemId} D={IsInDresserForItem(itemId)} A={IsInArmoireForItem(itemId)}");
            printed++;
        }
    }

    private static int FindListIndexForLabel(AtkComponentList* list, string sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
            return -1;

        for (var i = 0; i < list->ListLength; i++)
        {
            var label = list->GetItemLabel(i).ToString();
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (label.Contains(sheetName, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private void PrintFirstStoredRowAnchorDebug(IChatGui chat, AtkComponentList* list, List<GrandCompanyItem> expertItems)
    {
        for (var itemIndex = 0; itemIndex < list->ListLength; itemIndex++)
        {
            if (!list->IsItemVisible(itemIndex))
                continue;

            var renderer = list->GetItemRenderer(itemIndex);
            if (renderer == null)
                continue;

            var rowRoot = GetRowRoot(renderer);
            var gcItem = FindMatchingExpertItem(GetExpertMatchIndex(), list, itemIndex, rowRoot);
            if (gcItem == null)
                continue;

            var itemId = gcItem.Value.ItemId;
            if (!IsInDresserForItem(itemId) && !IsInArmoireForItem(itemId))
                continue;

            var iconComponent = AtkUiHelper.FindLeftmostItemGraphicNode(rowRoot);
            var dresserSize = this.iconCache.GetDresserSize();
            var armoireSize = this.iconCache.GetArmoireSize();
            var markerWidth = Math.Max(dresserSize.X, armoireSize.X);
            var markerHeight = Math.Max(dresserSize.Y, armoireSize.Y);
            var uiScale = 1f;
            var supplyPtr = this.gameGui.GetAddonByName("GrandCompanySupplyList", 1);
            if (supplyPtr.Address != nint.Zero)
                uiScale = Math.Max(((AtkUnitBase*)supplyPtr.Address)->Scale, 0.01f);
            var anchor = AtkUiHelper.TryGetListRowMarkerAnchor(
                list, renderer, itemIndex, MarkerGapBeforeIcon, markerHeight, markerWidth, uiScale);

            float rowScreenX = 0;
            float rowScreenY = 0;
            renderer->GetScreenPosition(&rowScreenX, &rowScreenY);

            var listRes = AtkUiHelper.GetComponentOwnerResNode((AtkComponentBase*)list);
            var listScreen = listRes != null ? $"({listRes->ScreenX},{listRes->ScreenY})" : "(null)";
            var rowOwner = renderer->OwnerNode != null;
            chat.Print(
                $"[GlamourTracker] first stored row idx={itemIndex} rowOwner={rowOwner} rowScreen=({rowScreenX},{rowScreenY}) iconNode={iconComponent != null} anchor={anchor} listScreen={listScreen}");
            return;
        }
    }
}
#endif
