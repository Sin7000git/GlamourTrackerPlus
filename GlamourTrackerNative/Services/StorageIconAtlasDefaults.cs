using Dalamud.Plugin.Services;

namespace GlamourTracker.Services;

/// <summary>
/// ItemDetail storage icon sheet layout (shared UI atlas) for GC markers / tooltips.
/// Texture is <c>ui/uld/ItemDetailPutIn</c> (not a QoL Extra sheet id).
/// Tuned 2026-07-27 for ATK SimpleImageNode sampling (no ImGui flip remap).
/// </summary>
internal static class StorageIconAtlasDefaults
{
    /// <summary>ULD stem for dresser/armoire eligibility icons (no .tex / _hr1).</summary>
    public const string TextureStem = "ui/uld/ItemDetailPutIn";

    public const ushort IconV = 0;
    public const ushort IconW = 18;
    public const ushort IconH = 18;

    /// <summary>Dresser symbol atlas U (before V offset to bright row).</summary>
    public const ushort DresserU = 18;

    /// <summary>Armoire symbol atlas U (before V offset to bright row).</summary>
    public const ushort ArmoireU = 36;

    /// <summary>Shift V down to the bright row of the sheet.</summary>
    public const int BrightRowVOffset = 18;

    /// <summary>On-screen draw size before display scale.</summary>
    public const float DisplaySize = 24f;

    public const float DisplayScale = 0.81f;

    public const bool FlipVertically = false;

    // Legacy names used by Configuration property initializers.
    public const ushort DresserBrightU = DresserU;
    public const ushort ArmoireBrightU = ArmoireU;
    public const bool FlipBrightRow = FlipVertically;

#if GLAMOUR_DEV
    public static void ApplyUvDefaults(Configuration config)
    {
        config.DresserUiIconU = DresserU;
        config.DresserUiIconV = IconV;
        config.DresserUiIconW = IconW;
        config.DresserUiIconH = IconH;
        config.DresserIconUOffset = 0;
        config.DresserIconVOffset = BrightRowVOffset;
        config.DresserIconWOffset = 0;
        config.DresserIconHOffset = 0;
        config.DresserIconDisplayScale = DisplayScale;
        config.FlipDresserIconV = FlipVertically;
        config.DresserUiDisplayW = DisplaySize;
        config.DresserUiDisplayH = DisplaySize;

        config.ArmoireUiIconU = ArmoireU;
        config.ArmoireUiIconV = IconV;
        config.ArmoireUiIconW = IconW;
        config.ArmoireUiIconH = IconH;
        config.ArmoireIconUOffset = 0;
        config.ArmoireIconVOffset = BrightRowVOffset;
        config.ArmoireIconWOffset = 0;
        config.ArmoireIconHOffset = 0;
        config.ArmoireIconDisplayScale = DisplayScale;
        config.FlipArmoireIconV = FlipVertically;
        config.ArmoireUiDisplayW = DisplaySize;
        config.ArmoireUiDisplayH = DisplaySize;
    }
#endif

    /// <summary>Resolves <see cref="TextureStem"/> preferring HR when present.</summary>
    public static string ResolveTexturePath(IDataManager data)
    {
        var hr = TextureStem + "_hr1.tex";
        if (data.FileExists(hr))
            return hr;

        var sd = TextureStem + ".tex";
        if (data.FileExists(sd))
            return sd;

        return hr;
    }

    public static bool IsItemDetailPutInPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.Contains("ItemDetailPutIn", StringComparison.OrdinalIgnoreCase);
    }
}
