using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.IoC;
#if GLAMOUR_DEV
using Dalamud.Interface.Windowing;
#endif
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows;
using KamiToolKit;

namespace GlamourTracker;

public sealed class Plugin : IDalamudPlugin
{
    /// <summary>Primary UI command: native main window. Subcommands: report, …</summary>
    public const string CommandName = "/glamplus";
    private const double BackgroundRefreshSeconds = 30;
    private const double UiEventRefreshDebounceSeconds = 1.5;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    public Configuration Configuration { get; }

#if GLAMOUR_DEV
    public readonly WindowSystem WindowSystem = new("GlamourTrackerNative");
    private TrackerWindow? TrackerWindow { get; set; }
#endif
    private FashionReportNativeAddon? fashionNativeAddon;
    private TrackerNativeAddon? trackerNativeAddon;

    private readonly CabinetCatalog cabinetCatalog;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly OutfitSetCatalog outfitSetCatalog;
    private readonly GlamourCandidatePool candidatePool;
    private readonly GlamourPlateRandomizer plateRandomizer;
    private readonly PlateEditorOverlay plateEditorOverlay;
    private readonly ItemDetailEnhancer itemDetailEnhancer;
    private readonly GcExpertDeliveryEnhancer gcExpertDeliveryEnhancer;
    private readonly FashionReportService fashionReport;
    private readonly FashionMgpBuffService fashionMgpBuff;
    private readonly FashionVendorTravel vendorTravel;
    private readonly FashionReportProgressTracker fashionProgress;
    private readonly ArtisanIpcClient artisanIpc;
    private readonly FashionRecipeLookup recipeLookup;
    private readonly PluginCommands pluginCommands;

    private DateTime lastBackgroundRefresh = DateTime.MinValue;
    private DateTime lastUiEventRefresh = DateTime.MinValue;
    private bool wasEnabled = true;

    public Plugin()
    {
        KamiToolKitLibrary.Initialize(PluginInterface, "Glamour Tracker+");

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.AssignSave(() => PluginInterface.SavePluginConfig(Configuration));
        MigrateIconSliceConfig(Configuration);

        cabinetCatalog = new CabinetCatalog();
        cabinetCatalog.Build(DataManager);

        ownershipIndex = new GlamourOwnershipIndex(
            DataManager,
            cabinetCatalog,
            () => Configuration,
            ClientState,
            GetLocalContentIdStatic);
        outfitSetCatalog = new OutfitSetCatalog(DataManager, ownershipIndex, cabinetCatalog);
        candidatePool = new GlamourCandidatePool(DataManager, cabinetCatalog);
        plateRandomizer = new GlamourPlateRandomizer(candidatePool, () => Configuration, ObjectTable, Log);
        plateEditorOverlay = new PlateEditorOverlay(
            GameGui,
            ChatGui,
            Framework,
            () => Configuration,
            plateRandomizer,
            ToggleMainUi,
            () => RefreshAll(true));
        gcExpertDeliveryEnhancer = new GcExpertDeliveryEnhancer(
            AddonLifecycle,
            GameGui,
            DataManager,
            TextureProvider,
            cabinetCatalog,
            () => Configuration,
            ownershipIndex);
        itemDetailEnhancer = new ItemDetailEnhancer(
            AddonLifecycle,
            GameGui,
            DataManager,
            cabinetCatalog,
            () => Configuration,
            ownershipIndex);
        fashionReport = new FashionReportService(
            DataManager,
            ownershipIndex,
            ClientState,
            ObjectTable,
            GameInventory,
            Framework,
            Log);
        fashionMgpBuff = new FashionMgpBuffService(
            DataManager,
            ObjectTable,
            Framework,
            ChatGui,
            Log);
        vendorTravel = new FashionVendorTravel(
            DataManager,
            AetheryteList,
            GameGui,
            ChatGui,
            CommandManager,
            PluginInterface,
            Framework,
            Log);
        artisanIpc = new ArtisanIpcClient(PluginInterface, Log);
        recipeLookup = new FashionRecipeLookup(DataManager);
        fashionProgress = new FashionReportProgressTracker(
            GameInterop,
            () => Configuration,
            GetLocalContentIdStatic,
            Framework,
            Log);
        pluginCommands = new PluginCommands(CommandManager, ChatGui, this);

#if GLAMOUR_DEV
        TrackerWindow = new TrackerWindow(this);
        WindowSystem.AddWindow(TrackerWindow);
#endif
        trackerNativeAddon = new TrackerNativeAddon(this)
        {
            InternalName = "GlamNativeMain",
            Title = "Glamour Tracker+",
            Size = new System.Numerics.Vector2(920f, 640f),
        };
        fashionNativeAddon = new FashionReportNativeAddon(this)
        {
            InternalName = "GlamNativeFR",
            Title = "Fashion Report",
            Size = new System.Numerics.Vector2(960f, 640f),
        };

        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "MiragePrismPrismBox", OnDresserUiRefresh);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "MiragePrismPrismBox", OnDresserUiRefresh);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "MiragePrismMiragePlate", OnPlateUiRefresh);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "MiragePrismMiragePlate", OnPlateUiRefresh);
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettingsUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        if (ClientState.IsLoggedIn)
            RefreshAll(true);

        _ = fashionReport.RefreshAsync(force: false);
        Log.Information(
#if GLAMOUR_DEV
            "Glamour Tracker+ loaded (Dev). /glamplus · /glamplus report · /glamplus imgui.");
#else
            "Glamour Tracker+ loaded. /glamplus = main UI, /glamplus report = Fashion Report.");
#endif
    }

    public void Dispose()
    {
        ownershipIndex.OnCharacterLogout();

        trackerNativeAddon?.Dispose();
        trackerNativeAddon = null;
        fashionNativeAddon?.Dispose();
        fashionNativeAddon = null;

        pluginCommands.Dispose();
        fashionReport.Dispose();
        fashionProgress.Dispose();
        artisanIpc.Dispose();
        itemDetailEnhancer.Dispose();
        gcExpertDeliveryEnhancer.Dispose();

        KamiToolKitLibrary.Dispose();

        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "MiragePrismPrismBox", OnDresserUiRefresh);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "MiragePrismPrismBox", OnDresserUiRefresh);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "MiragePrismMiragePlate", OnPlateUiRefresh);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "MiragePrismMiragePlate", OnPlateUiRefresh);
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettingsUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

#if GLAMOUR_DEV
        WindowSystem.RemoveAllWindows();
        TrackerWindow = null;
#endif
    }

    internal GlamourOwnershipIndex OwnershipIndex => ownershipIndex;
    internal OutfitSetCatalog OutfitSets => outfitSetCatalog;
    internal CabinetCatalog CabinetCatalog => cabinetCatalog;
    internal GlamourPlateRandomizer PlateRandomizer => plateRandomizer;
    internal FashionReportService FashionReport => fashionReport;
    internal FashionMgpBuffService FashionMgpBuff => fashionMgpBuff;
    internal FashionVendorTravel VendorTravel => vendorTravel;
    internal FashionReportProgressTracker FashionProgress => fashionProgress;
    internal ArtisanIpcClient ArtisanIpc => artisanIpc;
    internal FashionRecipeLookup RecipeLookup => recipeLookup;

    /// <summary>Must run on the framework thread (unsafe agent writes).</summary>
    internal PlateRandomizeResult BeginRandomizeOpenPlate(Action<PlateRandomizeResult>? onComplete = null) =>
        plateRandomizer.BeginRandomize(onComplete);

    /// <summary>Reloads dresser/armoire ownership used by tooltips and GC markers.</summary>
    public void RefreshOwnership(bool force = false)
    {
        var wasSuspended = ownershipIndex.IsLiveOwnershipSuspended;
        var revisionBefore = ownershipIndex.Revision;
        ownershipIndex.Refresh(force);

        // After Clear, keep Overview at zeros until the dresser/armoire is opened — do not rebuild
        // the catalog from the game's leftover in-session ItemFinder cache every background tick.
        if (wasSuspended && ownershipIndex.IsLiveOwnershipSuspended)
            return;

        if (ownershipIndex.Revision != revisionBefore
            || wasSuspended
            || force)
        {
            outfitSetCatalog.Invalidate();
            fashionReport.RebindOwnership();
        }
    }

    /// <summary>
    /// Deletes saved and runtime ownership for every character. Stays empty until the dresser or
    /// armoire is opened again; does not discard the normal between-session save once data is re-read.
    /// </summary>
    public void ClearSavedOwnership()
    {
        ownershipIndex.ClearSaved();
        Configuration.CharacterCaches.Clear();
        Configuration.Save();
        outfitSetCatalog.Invalidate();
        fashionReport.RebindOwnership();
        ChatGui.Print(
            "Glamour Tracker+ saved ownership cleared. Open your glamour dresser or armoire to scan again.");
        trackerNativeAddon?.RequestFormRebuild();
    }

    /// <summary>Also persists glamour plates from the client.</summary>
    public void RefreshAll(bool force = false)
    {
        RefreshOwnership(force);

        if (ClientState.IsLoggedIn)
            GlamourPlateStore.SyncFromGame(Configuration, GetLocalContentId());

        fashionReport.RebindOwnership();

        // Dresser UI events / Refresh now update ownership without going through OnShow —
        // Overview must rebuild or it stays stuck on stale 0/N after Clear + resync.
        trackerNativeAddon?.RequestFormRebuild();
    }

    internal ulong GetLocalContentId() => GetLocalContentIdStatic();

    public void ToggleMainUi()
    {
        _ = Framework.RunOnFrameworkThread(() => trackerNativeAddon?.Toggle());
    }

#if GLAMOUR_DEV
    public void ToggleImGuiMainUi() => TrackerWindow?.Toggle();
#endif

    public void OpenFashionReportTab()
    {
        ToggleNativeFashionReport();
        _ = fashionReport.RefreshAsync(force: false);
    }

    /// <summary>Opens the KamiToolKit native Fashion Report shell (main-thread).</summary>
    public void ToggleNativeFashionReport()
    {
        _ = Framework.RunOnFrameworkThread(() => fashionNativeAddon?.Toggle());
    }

    public void RestoreTooltipEnhancements() => itemDetailEnhancer.RestoreVisibleTooltip();

#if GLAMOUR_DEV
    internal void DebugGcExpertDelivery() => gcExpertDeliveryEnhancer.DebugToChat(ChatGui);

    internal void RefreshGcIconPath() => gcExpertDeliveryEnhancer.RecaptureIconTexturePath();
#endif

    private void OpenSettingsUi() => trackerNativeAddon?.OpenSettingsTab();

    private void DrawUi()
    {
        plateEditorOverlay.Draw();
        gcExpertDeliveryEnhancer.DrawOverlays();
#if GLAMOUR_DEV
        WindowSystem.Draw();
#endif
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
            return;

        var enabled = Configuration.Enabled;
        if (!enabled)
        {
            if (this.wasEnabled)
                itemDetailEnhancer.RestoreVisibleTooltip();

            this.wasEnabled = false;
            return;
        }

        this.wasEnabled = true;
        plateRandomizer.Tick();

        // ContentId can lag behind IsLoggedIn — finish deferred cache load before any wipe-prone refresh.
        if (ownershipIndex.TryFinishPendingLoginLoad())
        {
            RefreshAll(true);
            this.lastBackgroundRefresh = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - this.lastBackgroundRefresh).TotalSeconds < BackgroundRefreshSeconds)
            return;

        this.lastBackgroundRefresh = DateTime.UtcNow;
        RefreshOwnership();
    }

    private void OnLogin()
    {
        var contentId = GetLocalContentId();
        ownershipIndex.OnCharacterLogin(contentId);
        // Skip immediate RefreshAll when ContentId is still 0 — framework tick will load + refresh.
        if (contentId != 0)
        {
            RefreshAll(true);
            this.lastBackgroundRefresh = DateTime.UtcNow;
        }
    }

    private void OnLogout(int type, int code)
    {
        if (ClientState.IsLoggedIn)
            GlamourPlateStore.SyncFromGame(Configuration, GetLocalContentId());

        ownershipIndex.OnCharacterLogout();
        itemDetailEnhancer.RestoreVisibleTooltip();
        gcExpertDeliveryEnhancer.ResetCaches();
    }

    private void OnDresserUiRefresh(AddonEvent type, AddonArgs args)
    {
        PlateSlotNodeLocator.InvalidateLock();
        TryRefreshAllFromUiEvent();
    }

    private void OnPlateUiRefresh(AddonEvent type, AddonArgs args)
    {
        PlateSlotNodeLocator.InvalidateLock();
        TryRefreshAllFromUiEvent();
    }

    /// <summary>Debounce dresser/plate ATK churn so ownership sync + config Save are not per-tick.</summary>
    private void TryRefreshAllFromUiEvent()
    {
        var now = DateTime.UtcNow;
        if ((now - this.lastUiEventRefresh).TotalSeconds < UiEventRefreshDebounceSeconds)
            return;

        this.lastUiEventRefresh = now;
        RefreshAll(true);
        this.lastBackgroundRefresh = now;
    }

    private static unsafe ulong GetLocalContentIdStatic()
    {
        var uiState = UIState.Instance();
        return uiState == null ? 0 : uiState->PlayerState.ContentId;
    }

    /// <summary>Clears pre-0.4.1 icon paths that lacked atlas UV data (showed garbled atlas text).</summary>
    private static void MigrateIconSliceConfig(Configuration config)
    {
        var dirty = false;

        if (!string.IsNullOrWhiteSpace(config.DresserUiIconPath) && config.DresserUiIconW == 0)
        {
            config.DresserUiIconPath = null;
            dirty = true;
        }

        if (!string.IsNullOrWhiteSpace(config.ArmoireUiIconPath) && config.ArmoireUiIconW == 0)
        {
            config.ArmoireUiIconPath = null;
            dirty = true;
        }

        if (config.Version < 5)
        {
            StorageIconAtlasDefaults.ApplyUvDefaults(config);
            if (IsReadyPath(config))
                config.StorageIconAtlasConfigured = true;
            config.Version = 5;
            dirty = true;
        }

        if (config.LocalUiTheme == null)
        {
            config.LocalUiTheme = PluginLocalUiTheme.CreateDefault();
            dirty = true;
        }
        else
        {
            config.LocalUiTheme.EnsureInitialized();
        }

        if (config.Version < 6)
        {
            config.UseLocalUiStyle = true;
            config.Version = 6;
            dirty = true;
        }

        if (config.Version < 8)
        {
            PlateSlotNodeLocator.ResetSlotRerollDefaults(config);
            config.Version = 8;
            dirty = true;
        }

        if (config.Version < 9)
        {
            StorageIconAtlasDefaults.ApplyUvDefaults(config);
            config.Version = 9;
            dirty = true;
        }

        if (config.Version < 10)
        {
            config.DresserIconDisplayScale = StorageIconAtlasDefaults.DisplayScale;
            config.ArmoireIconDisplayScale = StorageIconAtlasDefaults.DisplayScale;
            config.Version = 10;
            dirty = true;
        }

        // Bake ItemDetailPutIn — Extra-sheet hunting / areamap mis-applies are not the real atlas.
        if (config.Version < 11
            || !StorageIconAtlasDefaults.IsItemDetailPutInPath(config.DresserUiIconPath)
            || !StorageIconAtlasDefaults.IsItemDetailPutInPath(config.ArmoireUiIconPath))
        {
            // Path resolved at runtime via IDataManager once services exist; stem is enough for migration.
            var baked = StorageIconAtlasDefaults.TextureStem + "_hr1.tex";
            config.DresserUiIconPath = baked;
            config.ArmoireUiIconPath = baked;
            config.StorageIconAtlasConfigured = true;
            if (config.Version < 11)
                config.Version = 11;
            dirty = true;
        }

        // v12: one-shot clear Fashion Report progress (stale Complete survived week roll via Math.Max / bad saves).
        if (config.Version < 12)
        {
            foreach (var cache in config.CharacterCaches.Values)
            {
                cache.FashionReportHighestScore = 0;
                cache.FashionReportAllowancesRemaining = 4;
                cache.FashionReportSynced = false;
                cache.FashionReportFromDailyDuty = false;
                cache.FashionReportNextResetUtc = default;
            }

            config.Version = 12;
            dirty = true;
        }

        if (dirty)
            config.Save();
    }

    private static bool IsReadyPath(Configuration config) =>
        !string.IsNullOrWhiteSpace(config.DresserUiIconPath)
        || !string.IsNullOrWhiteSpace(config.ArmoireUiIconPath);
}
