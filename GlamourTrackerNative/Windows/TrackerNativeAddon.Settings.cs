using System.Numerics;
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
                "Clear saved dresser, armoire, plate, and Fashion Report data for this character only?",
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
                "Deletes dresser, armoire, plate, and Fashion Report progress for the character you are logged in as. Other characters are unchanged.",
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
                "Deletes saved dresser/armoire ownership and Fashion Report progress for every character. Counts stay at zero until you open the dresser or armoire again.",
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
