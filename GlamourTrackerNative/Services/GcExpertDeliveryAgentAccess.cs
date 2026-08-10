using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using AgentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace GlamourTracker.Services;

internal sealed unsafe partial class GcExpertDeliveryEnhancer
{
    private const int ExpertDeliveryTab = 2;
    private const int ExpertDeliveryStartPosition = 11;

    private ExpertDeliveryMatchIndex? expertMatchIndex;

    private ExpertDeliveryMatchIndex GetExpertMatchIndex()
    {
        if (this.expertMatchIndex != null)
            return this.expertMatchIndex;

        this.expertMatchIndex = ExpertDeliveryMatchIndex.Build(
            CollectExpertItems(GetAgent()),
            GetSheetItemName);
        return this.expertMatchIndex;
    }

    private static AgentGrandCompanySupply* GetAgent()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return null;

        return (AgentGrandCompanySupply*)agentModule->GetAgentByInternalId(AgentId.GrandCompanySupply);
    }

    private static bool IsExpertTabActive(AddonGrandCompanySupplyList* addon)
    {
        if (addon->ExpertDeliveryRadioButton != null && addon->ExpertDeliveryRadioButton->IsSelected)
            return true;

        if (addon->SelectedTab == ExpertDeliveryTab)
            return true;

        var agent = GetAgent();
        return agent != null && agent->SelectedTab == ExpertDeliveryTab;
    }

    private static List<GrandCompanyItem> CollectExpertItems(AgentGrandCompanySupply* agent)
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
}
