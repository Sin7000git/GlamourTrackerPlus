using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

/// <summary>Dev-only ItemDetail tooltip capture / Extra-sheet bake for atlas tuning.</summary>
internal sealed unsafe partial class StorageUiIconCache
{
    private IGameGui gameGui = null!;

    partial void InitDevServices(IGameGui gameGui) => this.gameGui = gameGui;

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

    private bool GetFlipV(bool isDresser)
    {
        var config = this.getConfiguration();
        return isDresser ? config.FlipDresserIconV : config.FlipArmoireIconV;
    }

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
}
