using GlamourTracker.Windows.Native;
using KamiToolKit.Nodes;

using static GlamourTracker.Windows.TrackerNativeNodeFactory;

namespace GlamourTracker.Windows;

internal sealed partial class TrackerNativeAddon
{
    private void BuildSettings(VerticalListNode list, float width)
    {
        var config = plugin.Configuration;

        list.AddNode(MakeSection("General"));
        list.AddNode(MakeCheckbox("Enable plugin", config.Enabled, v =>
        {
            config.Enabled = v;
            config.Save();
            if (!v)
                plugin.RestoreTooltipEnhancements();
        }));

        list.AddNode(MakeSection("Item tooltips"));
        list.AddNode(MakeCheckbox("Color-code dresser/armoire icons", config.ShowTooltipIcons, v =>
        {
            config.ShowTooltipIcons = v;
            config.Save();
        }));

        list.AddNode(MakeSection("Grand Company delivery"));
        list.AddNode(MakeCheckbox("Show dresser/armoire icons", config.ShowGcExpertDeliveryStatus, v =>
        {
            config.ShowGcExpertDeliveryStatus = v;
            config.Save();
        }));

        list.AddNode(MakeSection("Plate editor"));
        list.AddNode(MakeCheckbox("Show controls above plate editor", config.ShowPlateEditorOverlay, v =>
        {
            config.ShowPlateEditorOverlay = v;
            config.Save();
            // Nested "Place on the right" appears/disappears — rebuild next tick only.
            ScheduleRebuildForm();
        }));
        if (config.ShowPlateEditorOverlay)
        {
            list.AddNode(MakeIndentedCheckbox(
                "Place on the right",
                config.PlateEditorOverlayOnRight,
                v =>
                {
                    config.PlateEditorOverlayOnRight = v;
                    config.Save();
                },
                width));
        }

        list.AddNode(MakeCheckbox("Show reroll next to each slot", config.ShowSlotRerollButtons, v =>
        {
            config.ShowSlotRerollButtons = v;
            config.Save();
        }));
#if GLAMOUR_DEV
        list.AddNode(MakeMuted(
            "Fine-tune positions via /glamplus imgui → Settings → Slot button positions.",
            width));
#else
        list.AddNode(MakeMuted(
            "Slot button positions use built-in defaults.",
            width));
#endif
    }

}
