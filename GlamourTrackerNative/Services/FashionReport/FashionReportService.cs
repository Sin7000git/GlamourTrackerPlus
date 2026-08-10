using System.Collections.Concurrent;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using GlamourTracker;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionReportService : IDisposable
{
    private static readonly string[] DyeSlotOrder = ["weapon", "head", "body", "hands", "legs", "feet"];

    private static readonly Dictionary<string, string> SlotLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = "Weapon",
        ["head"] = "Head",
        ["body"] = "Body",
        ["hands"] = "Hands",
        ["legs"] = "Legs",
        ["feet"] = "Feet",
        ["ear"] = "Earrings",
        ["neck"] = "Necklace",
        ["wrist"] = "Bracelets",
        ["ring"] = "Ring",
        ["left_ring"] = "Left ring",
        ["right_ring"] = "Right ring",
    };

    private readonly IDataManager dataManager;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly IGameInventory gameInventory;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly FashionReportClient client;
    private readonly FashionVendorLocator vendorLocator;
    private readonly FashionInventoryIndex inventoryIndex;

    private readonly ConcurrentDictionary<string, FashionReportItemDetailDto> itemDetailCache =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, uint>? itemNameToId;
    private CancellationTokenSource? refreshCts;
    private readonly object stateGate = new();
    private readonly object ownershipRefreshGate = new();
    private bool ownershipRefreshPending;
    private DateTime ownershipRefreshDueUtc = DateTime.MinValue;
    private bool frameworkTickSubscribed;

    public FashionReportService(
        IDataManager dataManager,
        GlamourOwnershipIndex ownershipIndex,
        IClientState clientState,
        IObjectTable objectTable,
        IGameInventory gameInventory,
        IFramework framework,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.ownershipIndex = ownershipIndex;
        this.gameInventory = gameInventory;
        this.framework = framework;
        this.log = log;
        client = new FashionReportClient(log);
        vendorLocator = new FashionVendorLocator(dataManager, clientState, objectTable);
        inventoryIndex = new FashionInventoryIndex(gameInventory);

        // Buy / craft / move / split — keep Fashion Report owned + mats counts fresh.
        this.gameInventory.InventoryChanged += OnInventoryChanged;
    }

    public FashionReportSnapshot? Snapshot { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRefreshing { get; private set; }
    public DateTime? LastFetchUtc { get; private set; }

    public void RebindOwnership()
    {
        var current = Snapshot;
        if (current == null)
            return;

        // Prefer framework thread so LocalPlayer / inventories are available.
        if (framework.IsInFrameworkUpdateThread)
        {
            Snapshot = RebuildWithOwnership(
                current,
                vendorLocator.CapturePlayerContext(),
                inventoryIndex.Scan());
            return;
        }

        _ = framework.RunOnFrameworkThread(() =>
        {
            var snap = Snapshot;
            if (snap != null)
            {
                Snapshot = RebuildWithOwnership(
                    snap,
                    vendorLocator.CapturePlayerContext(),
                    inventoryIndex.Scan());
            }
        });
    }

    /// <summary>Resolve acquisition for an arbitrary item name (Outfit sets, etc.).</summary>
    public async Task<FashionResolvedItem> ResolveNamedItemAsync(string name, CancellationToken ct = default)
    {
        var (playerContext, inventory) = await framework
            .RunOnFrameworkThread(() => (vendorLocator.CapturePlayerContext(), inventoryIndex.Scan()))
            .ConfigureAwait(false);
        var detail = await GetCachedItemDetailAsync(name, ct).ConfigureAwait(false);
        return ResolveItem(name, detail?.GarlandUrl, detail, null, null, playerContext, inventory);
    }

    public void Dispose()
    {
        gameInventory.InventoryChanged -= OnInventoryChanged;
        lock (ownershipRefreshGate)
        {
            if (frameworkTickSubscribed)
            {
                framework.Update -= OnFrameworkTickForOwnership;
                frameworkTickSubscribed = false;
            }

            ownershipRefreshPending = false;
        }

        refreshCts?.Cancel();
        refreshCts?.Dispose();
        client.Dispose();
    }
}
