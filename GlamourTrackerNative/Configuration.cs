using Dalamud.Configuration;
using GlamourTracker.Services;
using Newtonsoft.Json;
using System;

namespace GlamourTracker;

[Serializable]
public sealed partial class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 14;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, the ImGui plate-editor overlay uses <see cref="PlateOverlayLocalUiTheme"/>
    /// instead of Dalamud's global style. Does not affect native KamiToolKit windows.
    /// </summary>
    [JsonProperty("UseLocalUiStyle")]
    public bool UsePlateOverlayLocalUiStyle { get; set; } = true;

    /// <summary>Editable theme for the ImGui plate overlay only (not the native tracker UI).</summary>
    [JsonProperty("LocalUiTheme")]
    public PluginLocalUiTheme PlateOverlayLocalUiTheme { get; set; } = PluginLocalUiTheme.CreateDefault();

    public bool ShowTooltipIcons { get; set; } = true;
    public bool ShowGcExpertDeliveryStatus { get; set; } = true;

    /// <summary>
    /// When talking to the Masked Rose for Fashion Report judging, warn if no VIP Card / Jackpot III
    /// MGP bonus is active yet (default on).
    /// </summary>
    public bool RemindFashionReportMgpBuff { get; set; } = true;

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

#if GLAMOUR_DEV
    // --- Dev-only slot-reroll placement (Release uses SlotRerollDefaults constants) ---

    /// <summary>Top of the first slot row as a fraction of plate height (0–1).</summary>
    public float SlotRerollFirstRowY { get; set; } = SlotRerollDefaults.FirstRowY;

    /// <summary>Top of the last slot row as a fraction of plate height (0–1).</summary>
    public float SlotRerollLastRowY { get; set; } = SlotRerollDefaults.LastRowY;

    /// <summary>Left column icon left edge as a fraction of plate width (0–1).</summary>
    public float SlotRerollLeftColumnX { get; set; } = SlotRerollDefaults.LeftColumnX;

    /// <summary>Right column icon right edge as a fraction of plate width (0–1, from the left).</summary>
    public float SlotRerollRightColumnX { get; set; } = SlotRerollDefaults.RightColumnX;

    /// <summary>Slot icon size as a fraction of plate height.</summary>
    public float SlotRerollIconSize { get; set; } = SlotRerollDefaults.IconSize;

    /// <summary>When true, buttons sit toward the character preview; when false, on the outer edges.</summary>
    public bool SlotRerollTowardCenter { get; set; } = SlotRerollDefaults.TowardCenter;

    /// <summary>Extra horizontal nudge in UI-scaled pixels (positive = toward plate center).</summary>
    public float SlotRerollNudgeX { get; set; } = SlotRerollDefaults.NudgeX;

    /// <summary>Extra vertical nudge in UI-scaled pixels (positive = down).</summary>
    public float SlotRerollNudgeY { get; set; } = SlotRerollDefaults.NudgeY;

    /// <summary>Gap between slot edge and button in UI-scaled pixels.</summary>
    public float SlotRerollGap { get; set; } = SlotRerollDefaults.Gap;
#endif

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

    /// <summary>
    /// Maximum LevelEquip (character level requirement). Used when limit is on.
    /// 0 = current game maximum (tracks expansions via ParamGrow).
    /// </summary>
    public int RandomizeMaxRequiredLevel { get; set; }

    /// <summary>When true, only items whose item level is within min/max.</summary>
    public bool RandomizeLimitItemLevel { get; set; }

    public int RandomizeMinItemLevel { get; set; } = 1;

    /// <summary>
    /// Maximum item level when the limit is on.
    /// 0 = current game maximum (highest LevelItem in the Item sheet).
    /// </summary>
    public int RandomizeMaxItemLevel { get; set; }

    /// <summary>Game texture path for the glamour dresser symbol (baked ItemDetailPutIn).</summary>
    public string? DresserUiIconPath { get; set; }

    /// <summary>Game texture path for the armoire symbol (baked ItemDetailPutIn).</summary>
    public string? ArmoireUiIconPath { get; set; }

#if GLAMOUR_DEV
    // --- Dev-only atlas UV / display tuning (Release resolves from StorageIconAtlasDefaults) ---

    public ushort DresserUiIconU { get; set; } = StorageIconAtlasDefaults.DresserU;
    public ushort DresserUiIconV { get; set; } = StorageIconAtlasDefaults.IconV;
    public ushort DresserUiIconW { get; set; } = StorageIconAtlasDefaults.IconW;
    public ushort DresserUiIconH { get; set; } = StorageIconAtlasDefaults.IconH;
    public float DresserUiDisplayW { get; set; } = StorageIconAtlasDefaults.DisplaySize;
    public float DresserUiDisplayH { get; set; } = StorageIconAtlasDefaults.DisplaySize;

    public ushort ArmoireUiIconU { get; set; } = StorageIconAtlasDefaults.ArmoireU;
    public ushort ArmoireUiIconV { get; set; } = StorageIconAtlasDefaults.IconV;
    public ushort ArmoireUiIconW { get; set; } = StorageIconAtlasDefaults.IconW;
    public ushort ArmoireUiIconH { get; set; } = StorageIconAtlasDefaults.IconH;
    public float ArmoireUiDisplayW { get; set; } = StorageIconAtlasDefaults.DisplaySize;
    public float ArmoireUiDisplayH { get; set; } = StorageIconAtlasDefaults.DisplaySize;

    public int DresserIconUOffset { get; set; }
    public int DresserIconVOffset { get; set; } = StorageIconAtlasDefaults.BrightRowVOffset;
    public int DresserIconWOffset { get; set; }
    public int DresserIconHOffset { get; set; }
    public float DresserIconDisplayScale { get; set; } = StorageIconAtlasDefaults.DisplayScale;
    public bool FlipDresserIconV { get; set; } = StorageIconAtlasDefaults.FlipVertically;

    public int ArmoireIconUOffset { get; set; }
    public int ArmoireIconVOffset { get; set; } = StorageIconAtlasDefaults.BrightRowVOffset;
    public int ArmoireIconWOffset { get; set; }
    public int ArmoireIconHOffset { get; set; }
    public float ArmoireIconDisplayScale { get; set; } = StorageIconAtlasDefaults.DisplayScale;
    public bool FlipArmoireIconV { get; set; } = StorageIconAtlasDefaults.FlipVertically;
#endif

    /// <summary>True once dresser/armoire atlas path defaults are set (baked ItemDetailPutIn).</summary>
    public bool StorageIconAtlasConfigured { get; set; }

    /// <summary>Persisted per-character ownership, plates, and Fashion Report progress.</summary>
    public Dictionary<ulong, CharacterTrackerCache> CharacterCaches { get; set; } = new();

    [NonSerialized]
    private Action? save;

    public void Save() => this.save?.Invoke();

    public void AssignSave(Action save) => this.save = save;

    /// <summary>Drop one character's persisted cache entry (alts keep theirs).</summary>
    public bool ForgetCharacter(ulong contentId)
    {
        if (contentId == 0)
            return false;
        if (!CharacterCaches.Remove(contentId))
            return false;
        Save();
        return true;
    }
}

public enum RandomizeJobFilterMode : byte
{
    Any = 0,
    CurrentJob = 1,
    SpecificJob = 2,
}
