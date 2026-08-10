using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes.Simplified;

namespace GlamourTracker.Services;

/// <summary>
/// Expert delivery: dresser/armoire icons immediately left of each item icon when stored there.
/// Markers are native ATK image nodes parented to the supply window so they move with it.
/// Texture is baked <c>ui/uld/ItemDetailPutIn</c>; atlas U/V/W/H match tooltip icons.
/// </summary>
internal sealed unsafe partial class GcExpertDeliveryEnhancer : IDisposable
{
    private const string SupplyAddonName = "GrandCompanySupplyList";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly StorageUiIconCache iconCache;

    private readonly List<SimpleImageNode> markerNodes = [];
    private int lastScrollOffset = int.MinValue;
    private int lastFirstVisible = int.MinValue;
    private int lastListLength = -1;
    private float lastAddonScale = float.NaN;
    private nint lastSupplyAddonAddress;
    private string lastAtlasSignature = string.Empty;
    private int lastOwnershipRevision = -1;

    public GcExpertDeliveryEnhancer(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        Func<Configuration> getConfiguration,
        GlamourOwnershipIndex ownershipIndex)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.getConfiguration = getConfiguration;
        this.ownershipIndex = ownershipIndex;
        this.iconCache = new StorageUiIconCache(gameGui, textureProvider, dataManager, getConfiguration);

        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, SupplyAddonName, OnGcSupplyUiChanged);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, SupplyAddonName, OnGcSupplyFinalize);
    }

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
            this.lastOwnershipRevision = -1;
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
        this.lastOwnershipRevision = -1;
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
}
