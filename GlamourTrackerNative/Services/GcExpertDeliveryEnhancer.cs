using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AgentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace GlamourTracker.Services;

/// <summary>
/// Expert delivery: green dresser/armoire icons immediately left of each item icon when stored there.
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
    private int lastDrawnMarkerCount;

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
        this.iconCache = new StorageUiIconCache(gameGui, textureProvider, getConfiguration);

        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, SupplyAddonName, OnGcSupplyUiChanged);
    }

    public void OnFirstTooltipForIconAtlas() => this.iconCache.TryEnsureConfigured();

    public void RecaptureIconTexturePath() => this.iconCache.TryRecaptureTexturePath();

    public void ResetCaches() => this.expertMatchIndex = null;

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, SupplyAddonName, OnGcSupplyUiChanged);
    }

    private void OnGcSupplyUiChanged(AddonEvent type, AddonArgs args) => this.expertMatchIndex = null;

    public void DrawOverlays()
    {
        var config = this.getConfiguration();
        if (!config.Enabled || !config.ShowGcExpertDeliveryStatus)
        {
            this.lastDrawnMarkerCount = 0;
            return;
        }

        var addonPtr = this.gameGui.GetAddonByName(SupplyAddonName, 1);
        if (addonPtr.Address == nint.Zero)
        {
            this.lastDrawnMarkerCount = 0;
            return;
        }

        var supplyUnit = (AtkUnitBase*)addonPtr.Address;
        if (!GcMarkerOverlayGuard.ShouldDrawAnyMarkers(supplyUnit))
        {
            this.lastDrawnMarkerCount = 0;
            return;
        }

        try
        {
            this.lastDrawnMarkerCount = DrawExpertDeliveryMarkers((AddonGrandCompanySupplyList*)addonPtr.Address, supplyUnit);
        }
        catch
        {
            this.lastDrawnMarkerCount = 0;
        }
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
            var drawn = DrawExpertDeliveryMarkers(ptr, supplyUnit);

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

    private unsafe int DrawExpertDeliveryMarkers(AddonGrandCompanySupplyList* addon, AtkUnitBase* supplyUnit)
    {
        if (addon->ExpertDeliveryList == null || !IsExpertTabActive(addon))
            return 0;

        var matchIndex = GetExpertMatchIndex();
        if (matchIndex.Items.Count == 0)
            return 0;

        if (!this.iconCache.IsReady)
            this.iconCache.TryEnsureConfigured();

        var config = this.getConfiguration();
        var list = addon->ExpertDeliveryList;
        var drawList = ImGui.GetBackgroundDrawList();
        var clipActive = GcMarkerOverlayGuard.TryGetClipRect(supplyUnit, out var clipMin, out var clipMax);
        if (clipActive)
            drawList.PushClipRect(clipMin, clipMax, true);

        var fontSize = ImGui.GetFontSize();
        var applyGreenTint = config.ShowGcExpertDeliveryColorCoding;
        var dresserSlice = this.iconCache.GetResolvedDresserSlice();
        var armoireSlice = this.iconCache.GetResolvedArmoireSlice();
        var dresserSize = dresserSlice.DisplaySize;
        var armoireSize = armoireSlice.DisplaySize;
        var markerWidth = Math.Max(dresserSize.X, armoireSize.X);
        var drawn = 0;
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
                list, renderer, itemIndex, MarkerGapBeforeIcon, fontSize, markerWidth);
            if (anchor == null)
                continue;

            var markerX = anchor.Value.X;
            var textTopY = anchor.Value.Y;
            var rowDrawn = 0;

            if (inArmoire)
            {
                var pos = new Vector2(markerX, CenterIconY(textTopY, fontSize, armoireSize.Y));
                if (GcMarkerOverlayGuard.ShouldDrawMarkerAt(this.gameGui, supplyUnit, pos, armoireSize)
                    && StorageMarkerDrawer.TryDrawTintedIcon(
                        drawList,
                        this.iconCache.GetArmoireTexture(),
                        armoireSlice,
                        pos,
                        config.FlipArmoireIconV,
                        applyGreenTint))
                {
                    markerX -= armoireSize.X + MarkerIconSpacing;
                    rowDrawn++;
                }
            }

            if (inDresser)
            {
                var pos = new Vector2(markerX, CenterIconY(textTopY, fontSize, dresserSize.Y));
                if (GcMarkerOverlayGuard.ShouldDrawMarkerAt(this.gameGui, supplyUnit, pos, dresserSize)
                    && StorageMarkerDrawer.TryDrawTintedIcon(
                        drawList,
                        this.iconCache.GetDresserTexture(),
                        dresserSlice,
                        pos,
                        config.FlipDresserIconV,
                        applyGreenTint))
                    rowDrawn++;
            }

            drawn += rowDrawn;
        }

        if (clipActive)
            drawList.PopClipRect();

        return drawn;
    }

    private static float CenterIconY(float textTopY, float fontSize, float iconHeight) =>
        textTopY + MathF.Max(0f, (fontSize - iconHeight) * 0.5f);

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
            var anchor = AtkUiHelper.TryGetListRowMarkerAnchor(
                list, renderer, itemIndex, MarkerGapBeforeIcon, ImGui.GetFontSize(), markerWidth);

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
