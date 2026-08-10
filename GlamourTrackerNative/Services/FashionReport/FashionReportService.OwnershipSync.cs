using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using GlamourTracker;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionReportService
{
    private const int OwnershipDebounceMs = 400;

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (events.Count == 0 || Snapshot == null)
            return;

        ScheduleOwnershipRefresh();
    }

    private void ScheduleOwnershipRefresh()
    {
        lock (ownershipRefreshGate)
        {
            ownershipRefreshPending = true;
            ownershipRefreshDueUtc = DateTime.UtcNow.AddMilliseconds(OwnershipDebounceMs);
            if (!frameworkTickSubscribed)
            {
                framework.Update += OnFrameworkTickForOwnership;
                frameworkTickSubscribed = true;
            }
        }
    }

    private void OnFrameworkTickForOwnership(IFramework _)
    {
        lock (ownershipRefreshGate)
        {
            if (!ownershipRefreshPending)
            {
                if (frameworkTickSubscribed)
                {
                    framework.Update -= OnFrameworkTickForOwnership;
                    frameworkTickSubscribed = false;
                }

                return;
            }

            if (DateTime.UtcNow < ownershipRefreshDueUtc)
                return;

            ownershipRefreshPending = false;
            if (frameworkTickSubscribed)
            {
                framework.Update -= OnFrameworkTickForOwnership;
                frameworkTickSubscribed = false;
            }
        }

        try
        {
            RebindOwnership();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Fashion Report ownership rebind after inventory change failed.");
            PluginFileLog.Warn("fashion.ownership", $"Inventory-driven rebind failed: {ex.Message}");
        }
    }
}
