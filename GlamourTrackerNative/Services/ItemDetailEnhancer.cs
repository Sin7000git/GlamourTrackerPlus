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

    private bool isEnhancing;

    public ItemDetailEnhancer(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
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

        this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, ItemDetailAddonName, OnItemDetailEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ItemDetailAddonName, OnItemDetailEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, ItemDetailAddonName, OnItemDetailFinalize);
    }

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, ItemDetailAddonName, OnItemDetailEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, ItemDetailAddonName, OnItemDetailEvent);
        this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, ItemDetailAddonName, OnItemDetailFinalize);
        RestoreVisibleTooltip();
    }

    private void OnItemDetailEvent(AddonEvent type, AddonArgs args) => TryEnhanceTooltip(args);

    /// <summary>The game reuses tooltip addon memory, so leave the icons as we found them.</summary>
    private unsafe void OnItemDetailFinalize(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.Address == nint.Zero)
            return;

        RestoreTooltip((AddonItemDetail*)args.Addon.Address);
    }

    public unsafe void RestoreVisibleTooltip()
    {
        var addon = this.gameGui.GetAddonByName(ItemDetailAddonName, 1);
        if (addon.Address == nint.Zero)
            return;

        RestoreTooltip((AddonItemDetail*)addon.Address);
    }

    private unsafe void TryEnhanceTooltip(AddonArgs args)
    {
        // Tinting nodes makes the addon fire another update — ignore our own re-entry.
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
            {
                RestoreTooltip(addon);
                return;
            }

            var agent = (AgentItemDetail*)agentModule->GetAgentByInternalId(AgentId.ItemDetail);
            if (agent == null || agent->ItemId == 0)
            {
                RestoreTooltip(addon);
                return;
            }

            var itemId = agent->ItemId;
            if (!this.dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            {
                RestoreTooltip(addon);
                return;
            }

            if (!IsRelevantItem(item, itemId))
            {
                RestoreTooltip(addon);
                return;
            }

            var canDresser = GlamourOwnershipIndex.IsGlamourGear(item);
            var canArmoire = this.cabinetCatalog.IsArmoireEligible(itemId);
            var dresserOwned = canDresser && this.ownershipIndex.IsInDresser(itemId);
            var armoireOwned = canArmoire && this.ownershipIndex.IsInArmoire(itemId);

            ApplyStorageIcons(addon, canDresser, canArmoire, dresserOwned, armoireOwned);
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
        if (addon == null)
            return;

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
