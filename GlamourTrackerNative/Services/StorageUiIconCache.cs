using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
#if GLAMOUR_DEV
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
#endif

namespace GlamourTracker.Services;

/// <summary>
/// Dresser/armoire marker textures from baked <c>ui/uld/ItemDetailPutIn</c> plus fixed atlas UV.
/// </summary>
internal sealed unsafe class StorageUiIconCache
{
    private readonly ITextureProvider textureProvider;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;
#if GLAMOUR_DEV
    private readonly IGameGui gameGui;
#endif

    private ISharedImmediateTexture? dresserTexture;
    private ISharedImmediateTexture? armoireTexture;

    public StorageUiIconCache(
        IGameGui gameGui,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        Func<Configuration> getConfiguration)
    {
#if GLAMOUR_DEV
        this.gameGui = gameGui;
#else
        _ = gameGui;
#endif
        this.textureProvider = textureProvider;
        this.dataManager = dataManager;
        this.getConfiguration = getConfiguration;
        EnsureBakedTexturePath();
        ReloadTextures();
    }

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

#if GLAMOUR_DEV
    public void PrintSliceDebug(IChatGui chat)
    {
        chat.Print($"[GlamourTracker] Dresser: {StorageMarkerDrawer.DescribeUv(this.dresserTexture, GetResolvedDresserSlice(), GetFlipV(true))}");
        chat.Print($"[GlamourTracker] Armoire: {StorageMarkerDrawer.DescribeUv(this.armoireTexture, GetResolvedArmoireSlice(), GetFlipV(false))}");
        chat.Print("[GlamourTracker] Atlas path is baked (ItemDetailPutIn); use Settings → GC icon atlas to tune UV.");
    }

    /// <summary>Legacy: capture texture path(s) from ItemDetail tooltip. Prefer baked ItemDetailPutIn.</summary>
    public void TryEnsureConfigured(bool force = false)
    {
        var config = this.getConfiguration();
        if (!force && config.StorageIconAtlasConfigured && IsReady)
            return;

        var addonPtr = this.gameGui.GetAddonByName("ItemDetail", 1);
        if (addonPtr.Address == nint.Zero)
            return;

        var addon = (AddonItemDetail*)addonPtr.Address;
        var firstTime = !config.StorageIconAtlasConfigured || force;
        var dirty = false;

        if (TryCaptureGroupTexturePath(addon->GlamourDresserIconGroup, out var dresserPath)
            && !string.Equals(config.DresserUiIconPath, dresserPath, StringComparison.Ordinal))
        {
            config.DresserUiIconPath = dresserPath;
            dirty = true;
        }

        if (TryCaptureGroupTexturePath(addon->ArmoireIconGroup, out var armoirePath)
            && !string.Equals(config.ArmoireUiIconPath, armoirePath, StringComparison.Ordinal))
        {
            config.ArmoireUiIconPath = armoirePath;
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(config.ArmoireUiIconPath)
            && !string.IsNullOrWhiteSpace(config.DresserUiIconPath))
        {
            config.ArmoireUiIconPath = config.DresserUiIconPath;
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(config.DresserUiIconPath)
            && !string.IsNullOrWhiteSpace(config.ArmoireUiIconPath))
        {
            config.DresserUiIconPath = config.ArmoireUiIconPath;
            dirty = true;
        }

        if (firstTime)
        {
            StorageIconAtlasDefaults.ApplyUvDefaults(config);
            dirty = true;
        }

        if (!IsReady)
            return;

        if (!config.StorageIconAtlasConfigured)
        {
            config.StorageIconAtlasConfigured = true;
            dirty = true;
        }

        if (dirty)
            config.Save();

        ReloadTextures();
    }

    /// <summary>Re-learn game texture path from ItemDetail; keeps saved atlas UV.</summary>
    public void TryRecaptureTexturePath()
    {
        var config = this.getConfiguration();
        var addonPtr = this.gameGui.GetAddonByName("ItemDetail", 1);
        if (addonPtr.Address == nint.Zero)
            return;

        var addon = (AddonItemDetail*)addonPtr.Address;
        var dirty = false;

        if (TryCaptureGroupTexturePath(addon->GlamourDresserIconGroup, out var dresserPath)
            && !string.Equals(config.DresserUiIconPath, dresserPath, StringComparison.Ordinal))
        {
            config.DresserUiIconPath = dresserPath;
            dirty = true;
        }

        if (TryCaptureGroupTexturePath(addon->ArmoireIconGroup, out var armoirePath)
            && !string.Equals(config.ArmoireUiIconPath, armoirePath, StringComparison.Ordinal))
        {
            config.ArmoireUiIconPath = armoirePath;
            dirty = true;
        }

        if (dirty)
        {
            config.Save();
            ReloadTextures();
        }
    }

    /// <summary>
    /// Bake dresser/armoire marker textures from a QoL Extra sheet id (10_000_000+).
    /// Keeps existing atlas UV / display settings; only the texture path changes.
    /// </summary>
    public bool TryApplyExtraSheet(uint extraIconId, out string? resolvedPath)
    {
        resolvedPath = EmptyGearSlotAtlas.ResolveTexturePath(this.dataManager, this.textureProvider, extraIconId);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return false;

        ApplyTexturePath(resolvedPath);
        return true;
    }
#endif

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
    }

#if GLAMOUR_DEV
    private bool GetFlipV(bool isDresser)
    {
        var config = this.getConfiguration();
        return isDresser ? config.FlipDresserIconV : config.FlipArmoireIconV;
    }
#endif

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

#if GLAMOUR_DEV
    private static unsafe bool TryCaptureGroupTexturePath(AtkResNode* group, out string path)
    {
        path = string.Empty;
        if (group == null)
            return false;

        if (!TryFindGroupIconImage(group, out var image))
            return false;

        var extracted = TryExtractTexturePath(image);
        if (string.IsNullOrWhiteSpace(extracted))
            return false;

        path = extracted;
        return true;
    }

    private static unsafe bool TryFindGroupIconImage(AtkResNode* group, out AtkImageNode* image)
    {
        image = null;
        var bestScore = float.MaxValue;
        AtkImageNode* bestImage = null;

        AtkUiHelper.WalkNodes(group, node =>
        {
            if (node->Type != NodeType.Image)
                return;

            var candidate = node->GetAsAtkImageNode();
            if (candidate == null || candidate->PartsList == null)
                return;

            var width = node->Width;
            var height = node->Height;
            if (width < 12 || height < 12 || width > 56 || height > 56)
                return;

            ref var part = ref candidate->PartsList->Parts[candidate->PartId];
            if (part.Width == 0 || part.Height == 0)
                return;

            var score = MathF.Abs(width - 24f) + MathF.Abs(height - 24f);
            if (score >= bestScore)
                return;

            bestScore = score;
            bestImage = candidate;
        });

        image = bestImage;
        return bestImage != null;
    }

    private static unsafe string? TryExtractTexturePath(AtkImageNode* image)
    {
        if (image == null || image->PartsList == null)
            return null;

        ref var part = ref image->PartsList->Parts[image->PartId];
        var uldAsset = part.UldAsset;
        if (uldAsset == null)
            return null;

        var atkTexture = &uldAsset->AtkTexture;
        if (atkTexture->TextureType != TextureType.Resource)
            return null;

        var resource = atkTexture->Resource;
        if (resource == null || resource->TexFileResourceHandle == null)
            return null;

        var path = resource->TexFileResourceHandle->FileName.ToString();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
#endif
}
