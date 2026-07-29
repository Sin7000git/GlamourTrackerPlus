using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes.Simplified;
#if GLAMOUR_DEV
using Dalamud.Bindings.ImGui;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Utility;
#endif
using AgentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace GlamourTracker.Services;

/// <summary>
/// Expert delivery: dresser/armoire icons immediately left of each item icon when stored there.
/// Markers are native ATK image nodes parented to the supply window so they move with it.
/// Texture is baked <c>ui/uld/ItemDetailPutIn</c>; atlas U/V/W/H match tooltip icons.
/// </summary>
internal sealed unsafe class GcExpertDeliveryEnhancer : IDisposable
{
    private const string SupplyAddonName = "GrandCompanySupplyList";
    private const int ExpertDeliveryTab = 2;
    private const int ExpertDeliveryStartPosition = 11;
    private const float MarkerGapBeforeIcon = 4f;
    private const float MarkerIconSpacing = 2f;

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly CabinetCatalog cabinetCatalog;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly StorageUiIconCache iconCache;

    private ExpertDeliveryMatchIndex? expertMatchIndex;

    private readonly List<SimpleImageNode> markerNodes = [];
    private int lastScrollOffset = int.MinValue;
    private int lastFirstVisible = int.MinValue;
    private int lastListLength = -1;
    private float lastAddonScale = float.NaN;
    private nint lastSupplyAddonAddress;
    private string lastAtlasSignature = string.Empty;

#if GLAMOUR_DEV
    private const string SheetPickerOverlayId = "glamour-tracker-gc-sheet-picker";
    private int pickerExtraId = (int)EmptyGearSlotAtlas.QolExtraSheetBase;
    private string? pickerStatus;
#endif

    public GcExpertDeliveryEnhancer(
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

        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, SupplyAddonName, OnGcSupplyFinalize);
    }

#if GLAMOUR_DEV
    public void RecaptureIconTexturePath() => this.iconCache.TryRecaptureTexturePath();
#endif

    public void ResetCaches()
    {
        this.expertMatchIndex = null;
        DisposeNativeMarkers();
    }

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SupplyAddonName, OnGcSupplyFinalize);
        DisposeNativeMarkers();
    }

    private void OnGcSupplyUiChanged(AddonEvent type, AddonArgs args)
    {
        // Invalidate item matching only — do not dispose markers (refresh fires while dragging).
        this.expertMatchIndex = null;
    }

    private void OnGcSupplyFinalize(AddonEvent type, AddonArgs args) => DisposeNativeMarkers();

    private void DisposeNativeMarkers()
    {
        if (this.markerNodes.Count == 0)
        {
            this.lastScrollOffset = int.MinValue;
            this.lastFirstVisible = int.MinValue;
            this.lastListLength = -1;
            this.lastAddonScale = float.NaN;
            this.lastSupplyAddonAddress = 0;
            this.lastAtlasSignature = string.Empty;
            return;
        }

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
        this.lastSupplyAddonAddress = 0;
        this.lastAtlasSignature = string.Empty;
    }

    public void DrawOverlays()
    {
        var config = this.getConfiguration();
        if (!config.Enabled || !config.ShowGcExpertDeliveryStatus)
        {
            if (this.markerNodes.Count > 0)
                DisposeNativeMarkers();
            return;
        }

        var addonPtr = this.gameGui.GetAddonByName(SupplyAddonName, 1);
        if (addonPtr.Address == nint.Zero)
        {
            if (this.markerNodes.Count > 0)
                DisposeNativeMarkers();
            return;
        }

        var supplyUnit = (AtkUnitBase*)addonPtr.Address;
        if (!GcMarkerOverlayGuard.ShouldDrawAnyMarkers(supplyUnit))
        {
            if (this.markerNodes.Count > 0)
                DisposeNativeMarkers();
            return;
        }

#if GLAMOUR_DEV
        DrawSheetPicker(addonPtr);
#endif

        try
        {
            _ = SyncNativeMarkers((AddonGrandCompanySupplyList*)addonPtr.Address, supplyUnit);
        }
        catch
        {
            DisposeNativeMarkers();
        }
    }

#if GLAMOUR_DEV
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
#endif

#if GLAMOUR_DEV
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

    private unsafe void PrintVisibleRowDebug(IChatGui chat, AtkComponentList* list, List<GrandCompanyItem> expertItems, int maxRows)
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
#endif

    private unsafe int SyncNativeMarkers(AddonGrandCompanySupplyList* addon, AtkUnitBase* supplyUnit)
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

        this.iconCache.EnsureBakedTexturePath();

        var config = this.getConfiguration();
        var list = addon->ExpertDeliveryList;
        var dresserSlice = this.iconCache.GetResolvedDresserSlice();
        var armoireSlice = this.iconCache.GetResolvedArmoireSlice();
        var supplyAddress = (nint)supplyUnit;
        var atlasSig = AtlasSignature(config, dresserSlice, armoireSlice);
        var uiScale = Math.Max(supplyUnit->Scale, 0.01f);

        var needsRebuild = list->ScrollOffset != this.lastScrollOffset
            || list->FirstVisibleItemIndex != this.lastFirstVisible
            || list->ListLength != this.lastListLength
            || supplyAddress != this.lastSupplyAddonAddress
            || atlasSig != this.lastAtlasSignature
            || Math.Abs(uiScale - this.lastAddonScale) > 0.001f
            || this.markerNodes.Count == 0;

        if (!needsRebuild)
            return this.markerNodes.Count;

        // Rebuild on scroll / list / atlas / scale change — not while dragging with unchanged list.
        DisposeNativeMarkers();
        this.lastScrollOffset = list->ScrollOffset;
        this.lastFirstVisible = list->FirstVisibleItemIndex;
        this.lastListLength = list->ListLength;
        this.lastSupplyAddonAddress = supplyAddress;
        this.lastAtlasSignature = atlasSig;
        this.lastAddonScale = uiScale;

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

    private bool IsInDresserForItem(uint itemId)
    {
        foreach (var id in ItemIdHelper.GetRelatedItemIds(itemId))
        {
            if (this.ownershipIndex.IsInDresser(id))
                return true;
        }

        return false;
    }

    private bool IsInArmoireForItem(uint itemId)
    {
        foreach (var id in ItemIdHelper.GetRelatedItemIds(itemId))
        {
            if (this.ownershipIndex.IsInArmoire(id))
                return true;
        }

        return false;
    }

    private ExpertDeliveryMatchIndex GetExpertMatchIndex()
    {
        if (this.expertMatchIndex != null)
            return this.expertMatchIndex;

        this.expertMatchIndex = ExpertDeliveryMatchIndex.Build(
            CollectExpertItems(GetAgent()),
            GetSheetItemName);
        return this.expertMatchIndex;
    }

    private static unsafe AgentGrandCompanySupply* GetAgent()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return null;

        return (AgentGrandCompanySupply*)agentModule->GetAgentByInternalId(AgentId.GrandCompanySupply);
    }

    private static unsafe bool IsExpertTabActive(AddonGrandCompanySupplyList* addon)
    {
        if (addon->ExpertDeliveryRadioButton != null && addon->ExpertDeliveryRadioButton->IsSelected)
            return true;

        if (addon->SelectedTab == ExpertDeliveryTab)
            return true;

        var agent = GetAgent();
        return agent != null && agent->SelectedTab == ExpertDeliveryTab;
    }

    private static unsafe List<GrandCompanyItem> CollectExpertItems(AgentGrandCompanySupply* agent)
    {
        var items = new List<GrandCompanyItem>();
        if (agent == null || agent->ItemArray == null)
            return items;

        for (var i = 0; i < agent->NumItems; i++)
        {
            ref var entry = ref agent->ItemArray[i];
            if (entry.ItemId == 0)
                continue;

            if (entry.Position >= ExpertDeliveryStartPosition)
                items.Add(entry);
        }

        return items;
    }

    private static GrandCompanyItem? FindMatchingExpertItem(
        ExpertDeliveryMatchIndex index,
        AtkComponentList* list,
        int itemIndex,
        AtkResNode* rowRoot)
    {
        var rowLabel = GetRowDisplayLabel(list, itemIndex, rowRoot);
        if (!string.IsNullOrWhiteSpace(rowLabel))
        {
            var byLabel = index.MatchByRowLabel(rowLabel);
            if (byLabel != null)
                return byLabel;
        }

        var rowIconId = GetRowIconId(rowRoot);
        if (rowIconId != 0 && index.TryGetByIconId(rowIconId, out var byRowIcon))
            return byRowIcon;

        var listIconId = GetListIconId(list, itemIndex);
        if (listIconId != 0 && index.TryGetByIconId(listIconId, out var byListIcon))
            return byListIcon;

        return null;
    }

    private string GetSheetItemName(uint itemId)
    {
        var baseId = ItemIdHelper.GlamourBaseId(itemId);
        if (!this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(baseId, out var item))
            return string.Empty;

        return item.Name.ToString();
    }

    private static unsafe string GetRowDisplayLabel(AtkComponentList* list, int itemIndex, AtkResNode* rowRoot)
    {
        if (itemIndex >= 0 && itemIndex < list->ListLength)
        {
            var label = list->GetItemLabel(itemIndex).ToString();
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        return GetLongestRowText(rowRoot);
    }

    private static unsafe string GetLongestRowText(AtkResNode* rowRoot)
    {
        if (rowRoot == null)
            return string.Empty;

        var best = string.Empty;

        AtkUiHelper.WalkNodes(rowRoot, node =>
        {
            if (node->Type != NodeType.Text)
                return;

            var text = node->GetAsAtkTextNode();
            if (text == null)
                return;

            var value = text->NodeText.ToString();
            if (value.Length <= best.Length)
                return;

            if (!value.Any(char.IsLetter))
                return;

            best = value;
        });

        return best;
    }

    private static uint GetRowIconId(AtkResNode* rowRoot)
    {
        var iconComponent = AtkUiHelper.FindLeftmostItemGraphicNode(rowRoot);
        if (iconComponent == null)
            return 0;

        if (iconComponent->Type != NodeType.Component)
            return 0;

        var componentNode = iconComponent->GetAsAtkComponentNode();
        if (componentNode == null)
            return 0;

        var icon = componentNode->GetAsAtkComponentIcon();
        return icon == null ? 0 : icon->IconId;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static unsafe uint GetListIconId(AtkComponentList* list, int itemIndex)
    {
        if (list->ItemRendererList == null || itemIndex < 0 || itemIndex >= list->ListLength)
            return 0;

        return list->ItemRendererList[itemIndex].IconId;
    }

#if GLAMOUR_DEV
    private static unsafe int FindListIndexForLabel(AtkComponentList* list, string sheetName)
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

    private unsafe void PrintFirstStoredRowAnchorDebug(IChatGui chat, AtkComponentList* list, List<GrandCompanyItem> expertItems)
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
#endif

    private static unsafe AtkResNode* GetRowRoot(AtkComponentListItemRenderer* renderer)
    {
        if (renderer->OwnerNode != null)
            return (AtkResNode*)renderer->OwnerNode;

        if (renderer->RowTemplateNodeCount == 1)
            return renderer->RowTemplateNode;

        if (renderer->RowTemplateNodeList != null && renderer->RowTemplateNodeCount > 0)
            return renderer->RowTemplateNodeList[0];

        return renderer->RowTemplateNode;
    }
}
