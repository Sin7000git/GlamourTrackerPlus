using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace GlamourTracker.Services.FashionReport;

/// <summary>Minimal Artisan IPC for starting a single recipe craft.</summary>
internal sealed class ArtisanIpcClient : IDisposable
{
    private static readonly TimeSpan AvailabilityCacheTtl = TimeSpan.FromSeconds(2);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool>? isBusy;
    private readonly ICallGateSubscriber<bool>? isListRunning;
    private readonly ICallGateSubscriber<bool>? getEndurance;
    private readonly ICallGateSubscriber<ushort, int, object>? craftItem;

    private bool cachedAvailable;
    private DateTime cacheUtc = DateTime.MinValue;

    public ArtisanIpcClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        isBusy = TryFunc<bool>(pluginInterface, "Artisan.IsBusy");
        isListRunning = TryFunc<bool>(pluginInterface, "Artisan.IsListRunning");
        getEndurance = TryFunc<bool>(pluginInterface, "Artisan.GetEnduranceStatus");
        craftItem = TryAction<ushort, int>(pluginInterface, "Artisan.CraftItem");
    }

    /// <summary>
    /// True when Artisan is installed, loaded, and exposes CraftItem IPC.
    /// Cached briefly so Draw does not scan InstalledPlugins every frame.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            var now = DateTime.UtcNow;
            if ((now - cacheUtc) < AvailabilityCacheTtl)
                return cachedAvailable;

            cachedAvailable = craftItem != null
                              && pluginInterface.InstalledPlugins.Any(p =>
                                  string.Equals(p.InternalName, "Artisan", StringComparison.OrdinalIgnoreCase)
                                  && p.IsLoaded);
            cacheUtc = now;
            return cachedAvailable;
        }
    }

    public bool IsBusy()
    {
        try
        {
            return isBusy?.InvokeFunc() == true
                   || isListRunning?.InvokeFunc() == true
                   || getEndurance?.InvokeFunc() == true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Artisan busy check failed.");
            return false;
        }
    }

    public bool TryCraftItem(ushort recipeId, int quantity, out string message)
    {
        if (!IsAvailable || craftItem == null)
        {
            message = "Install and enable Artisan to autocraft.";
            return false;
        }

        if (IsBusy())
        {
            message = "Artisan is already crafting. Stop it first, then try again.";
            return false;
        }

        try
        {
            craftItem.InvokeAction(recipeId, Math.Max(1, quantity));
            message = $"Started Artisan craft for recipe #{recipeId} ×{Math.Max(1, quantity)}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Could not start Artisan: {ex.Message}";
            log.Warning(ex, "Artisan.CraftItem failed.");
            PluginFileLog.Error("fashion.artisan", $"CraftItem failed recipe={recipeId}", ex);
            return false;
        }
    }

    public void Dispose()
    {
    }

    private static ICallGateSubscriber<TRet>? TryFunc<TRet>(IDalamudPluginInterface pi, string name)
    {
        try
        {
            return pi.GetIpcSubscriber<TRet>(name);
        }
        catch
        {
            return null;
        }
    }

    private static ICallGateSubscriber<T1, T2, object>? TryAction<T1, T2>(IDalamudPluginInterface pi, string name)
    {
        try
        {
            return pi.GetIpcSubscriber<T1, T2, object>(name);
        }
        catch
        {
            return null;
        }
    }
}
