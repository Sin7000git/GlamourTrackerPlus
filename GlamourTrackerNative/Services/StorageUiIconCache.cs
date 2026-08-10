using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace GlamourTracker.Services;

/// <summary>
/// Dresser/armoire marker textures from baked <c>ui/uld/ItemDetailPutIn</c> plus fixed atlas UV.
/// </summary>
internal sealed unsafe partial class StorageUiIconCache
{
    private readonly ITextureProvider textureProvider;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;

    private ISharedImmediateTexture? dresserTexture;
    private ISharedImmediateTexture? armoireTexture;

    public StorageUiIconCache(
        IGameGui gameGui,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        Func<Configuration> getConfiguration)
    {
        InitDevServices(gameGui);
        this.textureProvider = textureProvider;
        this.dataManager = dataManager;
        this.getConfiguration = getConfiguration;
        EnsureBakedTexturePath();
        ReloadTextures();
    }

    /// <summary>Dev partial wires <see cref="IGameGui"/>; Release has no implementation.</summary>
    partial void InitDevServices(IGameGui gameGui);

    public bool IsReady
    {
        get
        {
            var config = this.getConfiguration();
            return !string.IsNullOrWhiteSpace(config.DresserUiIconPath)
                || !string.IsNullOrWhiteSpace(config.ArmoireUiIconPath);
        }
    }

    public ISharedImmediateTexture? GetDresserTexture() => this.dresserTexture;

    public ISharedImmediateTexture? GetArmoireTexture() => this.armoireTexture;

    public Vector2 GetDresserSize() => GetResolvedDresserSlice().DisplaySize;

    public Vector2 GetArmoireSize() => GetResolvedArmoireSlice().DisplaySize;

    public StorageUiIconSlice GetResolvedDresserSlice() => BuildResolvedSlice(isDresser: true);

    public StorageUiIconSlice GetResolvedArmoireSlice() => BuildResolvedSlice(isDresser: false);

    /// <summary>Bake <c>ui/uld/ItemDetailPutIn</c>; keeps atlas UV.</summary>
    public string ApplyItemDetailPutInSheet()
    {
        var path = StorageIconAtlasDefaults.ResolveTexturePath(this.dataManager);
        ApplyTexturePath(path);
        return path;
    }

    /// <summary>Ensure config points at ItemDetailPutIn (no tooltip hover required).</summary>
    public void EnsureBakedTexturePath()
    {
        var config = this.getConfiguration();
        if (StorageIconAtlasDefaults.IsItemDetailPutInPath(config.DresserUiIconPath)
            && StorageIconAtlasDefaults.IsItemDetailPutInPath(config.ArmoireUiIconPath)
            && config.StorageIconAtlasConfigured)
            return;

        ApplyItemDetailPutInSheet();
    }

    private void ApplyTexturePath(string path)
    {
        var config = this.getConfiguration();
        var dirty = !string.Equals(config.DresserUiIconPath, path, StringComparison.Ordinal)
            || !string.Equals(config.ArmoireUiIconPath, path, StringComparison.Ordinal)
            || !config.StorageIconAtlasConfigured;

        config.DresserUiIconPath = path;
        config.ArmoireUiIconPath = path;
        config.StorageIconAtlasConfigured = true;

        if (dirty)
            config.Save();

        ReloadTextures();
    }

    private StorageUiIconSlice BuildResolvedSlice(bool isDresser)
    {
        var config = this.getConfiguration();
        var path = isDresser ? config.DresserUiIconPath : config.ArmoireUiIconPath;
        if (string.IsNullOrWhiteSpace(path))
            return default;

#if GLAMOUR_DEV
        var scale = isDresser ? config.DresserIconDisplayScale : config.ArmoireIconDisplayScale;
        if (scale <= 0f)
            scale = 1f;

        var baseW = isDresser ? config.DresserUiDisplayW : config.ArmoireUiDisplayW;
        var baseH = isDresser ? config.DresserUiDisplayH : config.ArmoireUiDisplayH;
        if (baseW <= 0f)
            baseW = StorageIconAtlasDefaults.DisplaySize;
        if (baseH <= 0f)
            baseH = StorageIconAtlasDefaults.DisplaySize;

        var uOff = isDresser ? config.DresserIconUOffset : config.ArmoireIconUOffset;
        var vOff = isDresser ? config.DresserIconVOffset : config.ArmoireIconVOffset;
        var wOff = isDresser ? config.DresserIconWOffset : config.ArmoireIconWOffset;
        var hOff = isDresser ? config.DresserIconHOffset : config.ArmoireIconHOffset;

        return new StorageUiIconSlice
        {
            Path = path,
            U = ApplyOffset(isDresser ? config.DresserUiIconU : config.ArmoireUiIconU, uOff),
            V = ApplyOffset(isDresser ? config.DresserUiIconV : config.ArmoireUiIconV, vOff),
            Width = ApplyOffset(isDresser ? config.DresserUiIconW : config.ArmoireUiIconW, wOff),
            Height = ApplyOffset(isDresser ? config.DresserUiIconH : config.ArmoireUiIconH, hOff),
            DisplayWidth = MathF.Max(8f, baseW * scale),
            DisplayHeight = MathF.Max(8f, baseH * scale),
        };
#else
        var scale = StorageIconAtlasDefaults.DisplayScale;
        var baseSize = StorageIconAtlasDefaults.DisplaySize;
        return new StorageUiIconSlice
        {
            Path = path,
            U = isDresser ? StorageIconAtlasDefaults.DresserU : StorageIconAtlasDefaults.ArmoireU,
            V = ApplyOffset(StorageIconAtlasDefaults.IconV, StorageIconAtlasDefaults.BrightRowVOffset),
            Width = StorageIconAtlasDefaults.IconW,
            Height = StorageIconAtlasDefaults.IconH,
            DisplayWidth = MathF.Max(8f, baseSize * scale),
            DisplayHeight = MathF.Max(8f, baseSize * scale),
        };
#endif
    }

    private static ushort ApplyOffset(ushort value, int offset)
    {
        var result = (int)value + offset;
        return (ushort)Math.Clamp(result, 0, ushort.MaxValue);
    }

    private void ReloadTextures()
    {
        var config = this.getConfiguration();
        this.dresserTexture = string.IsNullOrWhiteSpace(config.DresserUiIconPath)
            ? null
            : this.textureProvider.GetFromGame(config.DresserUiIconPath);

        var armoirePath = string.IsNullOrWhiteSpace(config.ArmoireUiIconPath)
            ? config.DresserUiIconPath
            : config.ArmoireUiIconPath;
        this.armoireTexture = string.IsNullOrWhiteSpace(armoirePath)
            ? null
            : this.textureProvider.GetFromGame(armoirePath);
    }
}
