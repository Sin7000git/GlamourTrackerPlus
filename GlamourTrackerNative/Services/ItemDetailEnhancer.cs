using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using AgentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace GlamourTracker.Services;

internal sealed class ItemDetailEnhancer : IDisposable
{
    private const string ItemDetailAddonName = "ItemDetail";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly CabinetCatalog cabinetCatalog;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly System.Action? onTooltipIconsApplied;

    private bool isEnhancing;

    public ItemDetailEnhancer(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
        CabinetCatalog cabinetCatalog,
        Func<Configuration> getConfiguration,
        GlamourOwnershipIndex ownershipIndex,
        System.Action? onTooltipIconsApplied = null)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.cabinetCatalog = cabinetCatalog;
        this.getConfiguration = getConfiguration;
        this.ownershipIndex = ownershipIndex;
        this.onTooltipIconsApplied = onTooltipIconsApplied;

        this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, ItemDetailAddonName, OnItemDetailEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ItemDetailAddonName, OnItemDetailEvent);
    }

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, ItemDetailAddonName, OnItemDetailEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, ItemDetailAddonName, OnItemDetailEvent);
        RestoreVisibleTooltip();
    }

    private void OnItemDetailEvent(AddonEvent type, AddonArgs args) => TryEnhanceTooltip(args);

    public unsafe void RestoreVisibleTooltip()
    {
        var addon = this.gameGui.GetAddonByName(ItemDetailAddonName, 1);
        if (addon.Address == nint.Zero)
            return;

        RestoreTooltip((AddonItemDetail*)addon.Address);
    }

    private unsafe void TryEnhanceTooltip(AddonArgs args)
    {
        if (this.isEnhancing || args.Addon.Address == nint.Zero)
            return;

        var config = this.getConfiguration();
        var addon = (AddonItemDetail*)args.Addon.Address;

        if (!config.Enabled || !config.ShowTooltipIcons)
        {
            RestoreTooltip(addon);
            return;
        }

        this.isEnhancing = true;
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return;

            var agent = (AgentItemDetail*)agentModule->GetAgentByInternalId(AgentId.ItemDetail);
            if (agent == null || agent->ItemId == 0)
            {
                RestoreTooltip(addon);
                return;
            }

            var itemId = agent->ItemId;
            if (!this.dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
                return;

            if (config.ShowOnlyForGlamourItems && !IsRelevantItem(item, itemId))
            {
                RestoreTooltip(addon);
                return;
            }

            var canDresser = GlamourOwnershipIndex.IsGlamourGear(item);
            var canArmoire = this.cabinetCatalog.IsArmoireEligible(itemId);
            var dresserOwned = canDresser && this.ownershipIndex.IsInDresser(itemId);
            var armoireOwned = canArmoire && this.ownershipIndex.IsInArmoire(itemId);

            ApplyStorageIcons(addon, canDresser, canArmoire, dresserOwned, armoireOwned);

            if (canDresser || canArmoire)
                this.onTooltipIconsApplied?.Invoke();
        }
        finally
        {
            this.isEnhancing = false;
        }
    }

    private bool IsRelevantItem(Item item, uint itemId)
    {
        if (GlamourOwnershipIndex.IsGlamourGear(item))
            return true;

        return this.ownershipIndex.IsStored(itemId)
            || this.cabinetCatalog.IsArmoireEligible(itemId);
    }

    private static unsafe void RestoreTooltip(AddonItemDetail* addon)
    {
        AtkUiHelper.RestoreIconGroup(addon->GlamourDresserIconGroup);
        AtkUiHelper.RestoreIconGroup(addon->ArmoireIconGroup);
    }

    private static unsafe void ApplyStorageIcons(
        AddonItemDetail* addon,
        bool canDresser,
        bool canArmoire,
        bool dresserOwned,
        bool armoireOwned)
    {
        if (canDresser)
        {
            AtkUiHelper.SetGroupVisible(addon->GlamourDresserIconGroup, true);
            AtkUiHelper.TintIconGroup(addon->GlamourDresserIconGroup, dresserOwned);
        }
        else
        {
            AtkUiHelper.RestoreIconGroup(addon->GlamourDresserIconGroup);
        }

        if (canArmoire)
        {
            AtkUiHelper.SetGroupVisible(addon->ArmoireIconGroup, true);
            AtkUiHelper.TintIconGroup(addon->ArmoireIconGroup, armoireOwned);
        }
        else
        {
            AtkUiHelper.RestoreIconGroup(addon->ArmoireIconGroup);
        }

        if ((canDresser || canArmoire) && addon->HeaderIconsGroup != null)
            AtkUiHelper.SetGroupVisible(addon->HeaderIconsGroup, true);
    }
}
