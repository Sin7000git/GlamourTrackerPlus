using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>Fixed glamour plate slot order used by MirageManager / plate editor (12 slots).</summary>
internal enum GlamourPlateSlot : byte
{
    MainHand = 0,
    OffHand = 1,
    Head = 2,
    Body = 3,
    Hands = 4,
    Legs = 5,
    Feet = 6,
    Ears = 7,
    Neck = 8,
    Wrists = 9,
    RingRight = 10,
    RingLeft = 11,
}

internal static class GlamourPlateSlotMap
{
    public const int SlotCount = 12;

    public static readonly string[] Labels =
    [
        "Main hand",
        "Off hand",
        "Head",
        "Body",
        "Hands",
        "Legs",
        "Feet",
        "Ears",
        "Neck",
        "Wrists",
        "Right ring",
        "Left ring",
    ];

    /// <summary>
    /// Slot lock icons from The Emperor's New Attire (invisible glamour pieces).
    /// Icon IDs from Item sheet (xivapi): Fists, Shield, Hat, Robe, Gloves, Breeches, Boots, Earrings, Necklace, Bracelet, Ring.
    /// </summary>
    public static readonly uint[] EmptySlotIconIds =
    [
        31104, // Main hand — The Emperor's New Fists
        30150, // Off hand — The Emperor's New Shield
        41227, // Head — The Emperor's New Hat
        42422, // Body — The Emperor's New Robe
        44305, // Hands — The Emperor's New Gloves
        45539, // Legs — The Emperor's New Breeches
        46446, // Feet — The Emperor's New Boots
        55316, // Ears — The Emperor's New Earrings
        54909, // Neck — The Emperor's New Necklace
        55714, // Wrists — The Emperor's New Bracelet
        54561, // Right ring — The Emperor's New Ring
        54561, // Left ring — The Emperor's New Ring
    ];

    public static uint EmptySlotIcon(int slot) =>
        IsValidIndex(slot) ? EmptySlotIconIds[slot] : 42422;

    public static bool IsValidIndex(int slot) => slot is >= 0 and < SlotCount;

    public static string Label(int slot) =>
        IsValidIndex(slot) ? Labels[slot] : $"Slot {slot}";

    /// <summary>True when this EquipSlotCategory row can be placed in the given plate slot.</summary>
    public static bool Fits(in EquipSlotCategory category, GlamourPlateSlot slot) =>
        slot switch
        {
            GlamourPlateSlot.MainHand => category.MainHand > 0,
            GlamourPlateSlot.OffHand => category.OffHand > 0,
            GlamourPlateSlot.Head => category.Head > 0,
            GlamourPlateSlot.Body => category.Body > 0,
            GlamourPlateSlot.Hands => category.Gloves > 0,
            GlamourPlateSlot.Legs => category.Legs > 0,
            GlamourPlateSlot.Feet => category.Feet > 0,
            GlamourPlateSlot.Ears => category.Ears > 0,
            GlamourPlateSlot.Neck => category.Neck > 0,
            GlamourPlateSlot.Wrists => category.Wrists > 0,
            GlamourPlateSlot.RingRight => category.FingerR > 0,
            GlamourPlateSlot.RingLeft => category.FingerL > 0,
            _ => false,
        };
}
