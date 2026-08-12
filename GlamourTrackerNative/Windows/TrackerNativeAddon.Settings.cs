using System.Numerics;
using GlamourTracker.Services;
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
        list.AddNode(MakeMuted(
            "On item tooltips: green when stored, red when not (for storage that item can use).",
            width));

        list.AddNode(MakeSection("Grand Company delivery"));
        list.AddNode(MakeCheckbox("Show dresser/armoire icons", config.ShowGcExpertDeliveryStatus, v =>
        {
            config.ShowGcExpertDeliveryStatus = v;
            config.Save();
        }));

        list.AddNode(MakeSection("Glamour Creation"));
        list.AddNode(MakeCheckbox("Show dresser/armoire ownership icons", config.ShowGlamourCreationOwnershipIcons, v =>
        {
            config.ShowGlamourCreationOwnershipIcons = v;
            config.Save();
        }));
        list.AddNode(MakeMuted(
            "On the crystallize list. Dresser sits on the right; armoire on the left when both apply.",
            width));
        list.AddNode(MakeCheckbox("Color-code owned icons green", config.ColorCodeStorageIcons, v =>
        {
            config.ColorCodeStorageIcons = v;
            config.Save();
        }));
        list.AddNode(MakeMuted(
            "Owned icons turn green. Missing icons stay normal (untinted), like Grand Company delivery.",
            width));
        list.AddNode(MakeCheckbox("Only show icons where owned", config.StorageIconsOnlyWhenOwned, v =>
        {
            config.StorageIconsOnlyWhenOwned = v;
            config.Save();
        }));
        list.AddNode(MakeMuted(
            "Hides dresser or armoire icons for storage you do not already have. Crystallize list only.",
            width));

        list.AddNode(MakeSection("Outfit wishlist"));
        list.AddNode(MakeCheckbox(
            "Remove from wishlist when owned",
            config.AutoRemoveOwnedWishlist,
            v =>
            {
                config.AutoRemoveOwnedWishlist = v;
                config.Save();
            }));
        list.AddNode(MakeMuted(
            "Only applies to sets and pieces you add while this is on. Older wishlist entries stay until you remove them.",
            width));

        list.AddNode(MakeSection("Glamour dresser"));
        var haselAlert = HaselTweaksGate.IsGlamourDresserAlertEnabled(Plugin.PluginInterface);
        var armoireCb = MakeCheckbox(
            "Show armoire notes beside dresser",
            config.ShowArmoireCandidates,
            v =>
            {
                if (HaselTweaksGate.IsGlamourDresserAlertEnabled(Plugin.PluginInterface))
                    return;
                config.ShowArmoireCandidates = v;
                config.Save();
            });
        armoireCb.IsEnabled = !haselAlert;
        armoireCb.TextTooltip = haselAlert
            ? "Unavailable while HaselTweaks Glamour Dresser Alert is on."
            : "Lists dresser pieces that can go in the armoire.";
        list.AddNode(armoireCb);
        if (haselAlert)
        {
            list.AddNode(MakeMuted(
                "HaselTweaks Glamour Dresser Alert is on, so this stays off.",
                width));
        }
        else
        {
            list.AddNode(MakeMuted(
                "When the dresser is open, list pieces that can move to the armoire.",
                width));
        }

        list.AddNode(MakeSection("Fashion Report"));
        list.AddNode(MakeCheckbox(
            "Remind me when no MGP bonus is active",
            config.RemindFashionReportMgpBuff,
            v =>
            {
                config.RemindFashionReportMgpBuff = v;
                config.Save();
            }));
        list.AddNode(MakeMuted(
            "When talking to the Masked Rose for judging, ask before continuing if VIP Card or Jackpot III is not active.",
            width));

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
#endif

        list.AddNode(MakeSection("Saved data"));
        BuildSavedDataControls(list, width);
    }

    private void BuildSavedDataControls(VerticalListNode list, float width)
    {
        if (savedDataConfirm == SavedDataConfirmKind.Character)
        {
            list.AddNode(MakeMuted(
                "Clear saved dresser, armoire, plate, wishlist, and Fashion Report data for this character only?",
                width));
            list.AddNode(MakeConfirmCancelRow(
                confirmLabel: "Yes, clear character",
                onConfirm: () =>
                {
                    savedDataConfirm = SavedDataConfirmKind.None;
                    plugin.ForgetCurrentCharacterData();
                    ScheduleRebuildForm();
                },
                onCancel: () =>
                {
                    savedDataConfirm = SavedDataConfirmKind.None;
                    ScheduleRebuildForm();
                },
                width));
            return;
        }

        if (savedDataConfirm == SavedDataConfirmKind.All)
        {
            list.AddNode(MakeMuted(
                "Clear saved data for every character on this account? This cannot be undone.",
                width));
            list.AddNode(MakeConfirmCancelRow(
                confirmLabel: "Yes, clear all",
                onConfirm: () =>
                {
                    savedDataConfirm = SavedDataConfirmKind.None;
                    plugin.ClearSavedOwnership();
                    ScheduleRebuildForm();
                },
                onCancel: () =>
                {
                    savedDataConfirm = SavedDataConfirmKind.None;
                    ScheduleRebuildForm();
                },
                width));
            return;
        }

        list.AddNode(MakeMuted(
            "Clear character data removes only the character you are logged in as. Clear all data removes every character.",
            width));

        var buttons = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
            X = TrackerNativeHelpers.Indent,
        };
        buttons.AddNode(new TextButtonNode
        {
            Size = new Vector2(170f, RowH),
            String = "Clear character data",
            TextTooltip =
                "Deletes dresser, armoire, plate, wishlist, and Fashion Report progress for the character you are logged in as. Other characters are unchanged.",
            OnClick = () =>
            {
                savedDataConfirm = SavedDataConfirmKind.Character;
                ScheduleRebuildForm();
            },
        });
        buttons.AddNode(new TextButtonNode
        {
            Size = new Vector2(140f, RowH),
            String = "Clear all data",
            TextTooltip =
                "Deletes saved dresser/armoire ownership, wishlists, and Fashion Report progress for every character. Counts stay at zero until you open the dresser or armoire again.",
            OnClick = () =>
            {
                savedDataConfirm = SavedDataConfirmKind.All;
                ScheduleRebuildForm();
            },
        });
        list.AddNode(buttons);
    }

    private static HorizontalListNode MakeConfirmCancelRow(
        string confirmLabel,
        Action onConfirm,
        Action onCancel,
        float width)
    {
        var row = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
            X = TrackerNativeHelpers.Indent,
        };
        row.AddNode(new TextButtonNode
        {
            Size = new Vector2(180f, RowH),
            String = confirmLabel,
            OnClick = onConfirm,
        });
        row.AddNode(new TextButtonNode
        {
            Size = new Vector2(100f, RowH),
            String = "Cancel",
            OnClick = onCancel,
        });
        return row;
    }
}
