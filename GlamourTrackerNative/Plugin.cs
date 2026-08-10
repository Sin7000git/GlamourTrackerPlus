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
    private const int ConfigSaveDebounceMs = 400;

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
    private bool configSaveDirty;
    private DateTime configSaveAfterUtc = DateTime.MinValue;

    public Plugin()
    {
        KamiToolKitLibrary.Initialize(PluginInterface, "Glamour Tracker+");

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.AssignSave(ScheduleConfigSave);
        Configuration.Migrate();
        FlushConfigSave(force: true);

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
        FlushConfigSave(force: true);

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
        FlushConfigSave(force: true);
        outfitSetCatalog.Invalidate();
        fashionReport.RebindOwnership();
        ChatGui.Print(
            "Glamour Tracker+ saved ownership cleared. Open your glamour dresser or armoire to scan again.");
        trackerNativeAddon?.RequestFormRebuild();
    }

    /// <summary>Also persists glamour plates from the client.</summary>
    public void RefreshAll(bool force = false)
    {
        // RefreshOwnership already rebinds Fashion Report when ownership (or force) warrants it.
        RefreshOwnership(force);

        if (ClientState.IsLoggedIn)
            GlamourPlateStore.SyncFromGame(Configuration, GetLocalContentId());

        // Dresser/armoire UI events update ownership without going through OnShow —
        // Overview must rebuild or it stays stuck on stale 0/N after Clear + resync.
        trackerNativeAddon?.RequestFormRebuild();
    }

    private void ScheduleConfigSave()
    {
        this.configSaveDirty = true;
        this.configSaveAfterUtc = DateTime.UtcNow.AddMilliseconds(ConfigSaveDebounceMs);
    }

    private void FlushConfigSave(bool force)
    {
        if (!this.configSaveDirty)
            return;
        if (!force && DateTime.UtcNow < this.configSaveAfterUtc)
            return;

        this.configSaveDirty = false;
        PluginInterface.SavePluginConfig(Configuration);
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
        FlushConfigSave(force: false);
        gcExpertDeliveryEnhancer.Tick();

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
        FlushConfigSave(force: true);
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

    /// <summary>Removes only the logged-in character's persisted cache; other alts keep theirs.</summary>
    public void ForgetCurrentCharacterData()
    {
        var contentId = GetLocalContentId();
        if (contentId == 0)
        {
            ChatGui.PrintError("[Glamour Tracker+] Log in first to forget this character's saved data.");
            return;
        }

        if (!Configuration.ForgetCharacter(contentId))
        {
            ChatGui.Print("[Glamour Tracker+] No saved data for this character.");
            return;
        }

        ownershipIndex.ClearSaved();
        FlushConfigSave(force: true);
        outfitSetCatalog.Invalidate();
        fashionReport.RebindOwnership();
        ChatGui.Print(
            "Glamour Tracker+ forgot this character's saved data. Open your dresser or armoire to scan again.");
        trackerNativeAddon?.RequestFormRebuild();
    }
}
