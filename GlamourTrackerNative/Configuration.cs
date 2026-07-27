using Dalamud.Configuration;
using GlamourTracker.Services;
using System;

namespace GlamourTracker;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;

    public bool Enabled { get; set; } = true;

    /// <summary>When true, Glamour Tracker+ uses <see cref="LocalUiTheme"/> instead of Dalamud's global ImGui style.</summary>
    public bool UseLocalUiStyle { get; set; } = true;

    /// <summary>Editable FFXIV-inspired theme used when <see cref="UseLocalUiStyle"/> is on.</summary>
    public PluginLocalUiTheme LocalUiTheme { get; set; } = PluginLocalUiTheme.CreateDefault();
    public bool ShowTooltipIcons { get; set; } = true;
    public bool ShowTooltipText { get; set; } = false;
    public bool MarkSafeToSell { get; set; } = false;
    public bool ShowOnlyForGlamourItems { get; set; } = false;
    public bool ShowGcExpertDeliveryStatus { get; set; } = true;
    public bool ShowGcExpertDeliveryColorCoding { get; set; } = true;

    /// <summary>Include glamour dresser (Prism Box) items when randomizing plates.</summary>
    public bool RandomizeIncludeDresser { get; set; } = true;

    /// <summary>Include armoire (Cabinet) items when randomizing plates.</summary>
    public bool RandomizeIncludeArmoire { get; set; } = true;

    /// <summary>Show Randomize / menu controls above the glamour plate editor.</summary>
    public bool ShowPlateEditorOverlay { get; set; } = true;

    /// <summary>Show a Reroll button next to each equipment slot in the plate editor.</summary>
    public bool ShowSlotRerollButtons { get; set; } = true;

    /// <summary>
    /// When true, overlay sits on the top-right of the plate window (avoids Glamaholic's top-left menu).
    /// </summary>
    public bool PlateEditorOverlayOnRight { get; set; } = true;

    // --- Manual reroll placement (fractions of plate on-screen size, plus pixel nudges) ---

    /// <summary>Top of the first slot row as a fraction of plate height (0–1).</summary>
    public float SlotRerollFirstRowY { get; set; } = 0.20f;

    /// <summary>Top of the last slot row as a fraction of plate height (0–1).</summary>
    public float SlotRerollLastRowY { get; set; } = 0.58f;

    /// <summary>Left column icon left edge as a fraction of plate width (0–1).</summary>
    public float SlotRerollLeftColumnX { get; set; } = 0.08f;

    /// <summary>Right column icon right edge as a fraction of plate width (0–1, from the left).</summary>
    public float SlotRerollRightColumnX { get; set; } = 0.92f;

    /// <summary>Slot icon size as a fraction of plate height.</summary>
    public float SlotRerollIconSize { get; set; } = 0.055f;

    /// <summary>When true, buttons sit toward the character preview; when false, on the outer edges.</summary>
    public bool SlotRerollTowardCenter { get; set; }

    /// <summary>Extra horizontal nudge in UI-scaled pixels (positive = toward plate center).</summary>
    public float SlotRerollNudgeX { get; set; }

    /// <summary>Extra vertical nudge in UI-scaled pixels (positive = down).</summary>
    public float SlotRerollNudgeY { get; set; }

    /// <summary>Gap between slot edge and button in UI-scaled pixels.</summary>
    public float SlotRerollGap { get; set; } = 3f;

    /// <summary>Per-slot locks for plate randomization (length 12). Locked slots are left unchanged.</summary>
    public bool[] RandomizeLockedSlots { get; set; } = new bool[12];

    /// <summary>How to restrict random picks by class/job.</summary>
    public RandomizeJobFilterMode RandomizeJobFilter { get; set; } = RandomizeJobFilterMode.Any;

    /// <summary>ClassJob row id when <see cref="RandomizeJobFilter"/> is <see cref="RandomizeJobFilterMode.SpecificJob"/>.</summary>
    public uint RandomizeSpecificJobId { get; set; }

    /// <summary>When true, only items with required level in <see cref="RandomizeMinRequiredLevel"/>–<see cref="RandomizeMaxRequiredLevel"/>.</summary>
    public bool RandomizeLimitRequiredLevel { get; set; }

    /// <summary>Minimum LevelEquip (character level requirement). Used when limit is on.</summary>
    public int RandomizeMinRequiredLevel { get; set; } = 1;

    /// <summary>Maximum LevelEquip (character level requirement). Used when limit is on.</summary>
    public int RandomizeMaxRequiredLevel { get; set; } = 100;

    /// <summary>When true, only items whose item level is within min/max.</summary>
    public bool RandomizeLimitItemLevel { get; set; }

    public int RandomizeMinItemLevel { get; set; } = 1;
    public int RandomizeMaxItemLevel { get; set; } = 800;

    /// <summary>Game texture path for the glamour dresser symbol (captured from ItemDetail).</summary>
    public string? DresserUiIconPath { get; set; }

    public ushort DresserUiIconU { get; set; } = StorageIconAtlasDefaults.DresserBrightU;
    public ushort DresserUiIconV { get; set; } = StorageIconAtlasDefaults.IconV;
    public ushort DresserUiIconW { get; set; } = StorageIconAtlasDefaults.IconW;
    public ushort DresserUiIconH { get; set; } = StorageIconAtlasDefaults.IconH;
    public float DresserUiDisplayW { get; set; } = StorageIconAtlasDefaults.DisplaySize;
    public float DresserUiDisplayH { get; set; } = StorageIconAtlasDefaults.DisplaySize;

    /// <summary>Game texture path for the armoire symbol (captured from ItemDetail).</summary>
    public string? ArmoireUiIconPath { get; set; }

    public ushort ArmoireUiIconU { get; set; } = StorageIconAtlasDefaults.ArmoireBrightU;
    public ushort ArmoireUiIconV { get; set; } = StorageIconAtlasDefaults.IconV;
    public ushort ArmoireUiIconW { get; set; } = StorageIconAtlasDefaults.IconW;
    public ushort ArmoireUiIconH { get; set; } = StorageIconAtlasDefaults.IconH;
    public float ArmoireUiDisplayW { get; set; } = StorageIconAtlasDefaults.DisplaySize;
    public float ArmoireUiDisplayH { get; set; } = StorageIconAtlasDefaults.DisplaySize;

    /// <summary>Pixel adjustments applied on top of captured dresser icon atlas UV (for tuning).</summary>
    public int DresserIconUOffset { get; set; }
    public int DresserIconVOffset { get; set; }
    public int DresserIconWOffset { get; set; }
    public int DresserIconHOffset { get; set; }
    public float DresserIconDisplayScale { get; set; } = 1f;
    public bool FlipDresserIconV { get; set; } = StorageIconAtlasDefaults.FlipBrightRow;

    public int ArmoireIconUOffset { get; set; }
    public int ArmoireIconVOffset { get; set; }
    public int ArmoireIconWOffset { get; set; }
    public int ArmoireIconHOffset { get; set; }
    public float ArmoireIconDisplayScale { get; set; } = 1f;
    public bool FlipArmoireIconV { get; set; } = StorageIconAtlasDefaults.FlipBrightRow;

    /// <summary>Texture path + UV saved after first item tooltip; later hovers do not overwrite atlas coordinates.</summary>
    public bool StorageIconAtlasConfigured { get; set; }

    /// <summary>Persisted glamour ownership per character (ContentId).</summary>
    public Dictionary<ulong, CharacterGlamourCache> CharacterCaches { get; set; } = new();

    [NonSerialized]
    private Action? save;

    public void Save() => this.save?.Invoke();

    public void AssignSave(Action save) => this.save = save;
}

public enum RandomizeJobFilterMode : byte
{
    Any = 0,
    CurrentJob = 1,
    SpecificJob = 2,
}
