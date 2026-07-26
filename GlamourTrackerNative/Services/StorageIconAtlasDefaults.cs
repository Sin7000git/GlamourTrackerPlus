namespace GlamourTracker.Services;

/// <summary>
/// ItemDetail storage icon sheet layout (shared UI atlas). GC markers use the bright row (flip V).
/// </summary>
internal static class StorageIconAtlasDefaults
{
    public const ushort IconV = 0;
    public const ushort IconW = 36;
    public const ushort IconH = 36;

    /// <summary>Can be placed in a glamour dresser.</summary>
    public const ushort DresserBrightU = 36;

    /// <summary>Can be placed in a glamour dresser and used as outfit glamour.</summary>
    public const ushort ArmoireBrightU = 72;

    /// <summary>On-screen draw size before <see cref="Configuration.DresserIconDisplayScale"/>.</summary>
    public const float DisplaySize = 24f;

    public const bool FlipBrightRow = true;

    public static void ApplyUvDefaults(Configuration config)
    {
        config.DresserUiIconU = DresserBrightU;
        config.DresserUiIconV = IconV;
        config.DresserUiIconW = IconW;
        config.DresserUiIconH = IconH;

        config.ArmoireUiIconU = ArmoireBrightU;
        config.ArmoireUiIconV = IconV;
        config.ArmoireUiIconW = IconW;
        config.ArmoireUiIconH = IconH;

        config.FlipDresserIconV = FlipBrightRow;
        config.FlipArmoireIconV = FlipBrightRow;

        if (config.DresserUiDisplayW <= 0f)
            config.DresserUiDisplayW = DisplaySize;
        if (config.DresserUiDisplayH <= 0f)
            config.DresserUiDisplayH = DisplaySize;
        if (config.ArmoireUiDisplayW <= 0f)
            config.ArmoireUiDisplayW = DisplaySize;
        if (config.ArmoireUiDisplayH <= 0f)
            config.ArmoireUiDisplayH = DisplaySize;
    }
}
