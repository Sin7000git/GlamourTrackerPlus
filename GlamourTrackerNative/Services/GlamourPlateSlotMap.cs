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
    /// Empty equipment-slot silhouettes from ItemUICategory (inventory filter icons),
    /// not Emperor's New Attire item icons.
    /// </summary>
    public static readonly uint[] EmptySlotIconIds =
    [
        60102, // Main hand — Gladiator's Arm
        60110, // Off hand — Shield
        60124, // Head
        60126, // Body
        60129, // Hands
        60128, // Legs
        60130, // Feet
        60133, // Ears — Earrings
        60132, // Neck — Necklace
        60134, // Wrists — Bracelets
        60135, // Right ring
        60135, // Left ring
    ];

    public static uint EmptySlotIcon(int slot) =>
        IsValidIndex(slot) ? EmptySlotIconIds[slot] : 60126;

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
