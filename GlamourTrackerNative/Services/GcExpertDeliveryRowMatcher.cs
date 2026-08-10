using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;

namespace GlamourTracker.Services;

internal sealed unsafe partial class GcExpertDeliveryEnhancer
{
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

    private static string GetRowDisplayLabel(AtkComponentList* list, int itemIndex, AtkResNode* rowRoot)
    {
        if (itemIndex >= 0 && itemIndex < list->ListLength)
        {
            var label = list->GetItemLabel(itemIndex).ToString();
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        var textNode = AtkUiHelper.FindPrimaryRowLabelTextNode(rowRoot);
        if (textNode == null)
            return string.Empty;

        var text = textNode->GetAsAtkTextNode();
        return text == null ? string.Empty : text->NodeText.ToString();
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

    private static uint GetListIconId(AtkComponentList* list, int itemIndex)
    {
        if (list->ItemRendererList == null || itemIndex < 0 || itemIndex >= list->ListLength)
            return 0;

        return list->ItemRendererList[itemIndex].IconId;
    }

    private static AtkResNode* GetRowRoot(AtkComponentListItemRenderer* renderer)
    {
        if (renderer->OwnerNode != null)
            return (AtkResNode*)renderer->OwnerNode;

        if (renderer->RowTemplateNodeCountByte == 1)
            return renderer->RowTemplateNode;

        if (renderer->RowTemplateNodeList != null && renderer->RowTemplateNodeCountByte > 0)
            return renderer->RowTemplateNodeList[0];

        return renderer->RowTemplateNode;
    }
}
