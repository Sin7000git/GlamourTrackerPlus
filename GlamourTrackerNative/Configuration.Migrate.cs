using GlamourTracker.Services;

namespace GlamourTracker;

public sealed partial class Configuration
{
    /// <summary>
    /// One-shot schema upgrades. Version 7 was never shipped — migrations jump from 6 to 8 on purpose.
    /// </summary>
    public void Migrate()
    {
        var dirty = false;

#if GLAMOUR_DEV
        // Pre-0.4.1 paths without atlas UV data showed garbled atlas text.
        if (!string.IsNullOrWhiteSpace(DresserUiIconPath) && DresserUiIconW == 0)
        {
            DresserUiIconPath = null;
            dirty = true;
        }

        if (!string.IsNullOrWhiteSpace(ArmoireUiIconPath) && ArmoireUiIconW == 0)
        {
            ArmoireUiIconPath = null;
            dirty = true;
        }
#endif

        if (Version < 5)
        {
#if GLAMOUR_DEV
            StorageIconAtlasDefaults.ApplyUvDefaults(this);
#endif
            if (HasAnyIconPath())
                StorageIconAtlasConfigured = true;
            Version = 5;
            dirty = true;
        }

        if (PlateOverlayLocalUiTheme == null)
        {
            PlateOverlayLocalUiTheme = PluginLocalUiTheme.CreateDefault();
            dirty = true;
        }
        else
        {
            PlateOverlayLocalUiTheme.EnsureInitialized();
        }

        if (Version < 6)
        {
            UsePlateOverlayLocalUiStyle = true;
            Version = 6;
            dirty = true;
        }

        // Version 7 was never shipped. Next step is 8.
        if (Version < 8)
        {
#if GLAMOUR_DEV
            PlateSlotNodeLocator.ResetSlotRerollDefaults(this);
#endif
            Version = 8;
            dirty = true;
        }

        if (Version < 9)
        {
#if GLAMOUR_DEV
            StorageIconAtlasDefaults.ApplyUvDefaults(this);
#endif
            Version = 9;
            dirty = true;
        }

        if (Version < 10)
        {
#if GLAMOUR_DEV
            DresserIconDisplayScale = StorageIconAtlasDefaults.DisplayScale;
            ArmoireIconDisplayScale = StorageIconAtlasDefaults.DisplayScale;
#endif
            Version = 10;
            dirty = true;
        }

        // Bake ItemDetailPutIn — only when still below schema 11 (must not re-save every startup).
        if (Version < 11)
        {
            var baked = StorageIconAtlasDefaults.TextureStem + "_hr1.tex";
            DresserUiIconPath = baked;
            ArmoireUiIconPath = baked;
            StorageIconAtlasConfigured = true;
            Version = 11;
            dirty = true;
        }

        // v12: one-shot clear Fashion Report progress (stale Complete survived week roll).
        if (Version < 12)
        {
            foreach (var cache in CharacterCaches.Values)
            {
                cache.FashionReportHighestScore = 0;
                cache.FashionReportAllowancesRemaining = 4;
                cache.FashionReportSynced = false;
                cache.FashionReportNextResetUtc = default;
            }

            Version = 12;
            dirty = true;
        }

        // v13: normalize id lists, prune empty alt caches, one-shot path repair if still wrong.
        if (Version < 13)
        {
            if (!StorageIconAtlasDefaults.IsItemDetailPutInPath(DresserUiIconPath)
                || !StorageIconAtlasDefaults.IsItemDetailPutInPath(ArmoireUiIconPath))
            {
                var baked = StorageIconAtlasDefaults.TextureStem + "_hr1.tex";
                DresserUiIconPath = baked;
                ArmoireUiIconPath = baked;
                StorageIconAtlasConfigured = true;
            }

#if GLAMOUR_DEV
            StorageIconAtlasDefaults.ApplyUvDefaults(this);
            PlateSlotNodeLocator.ResetSlotRerollDefaults(this);
#endif

            foreach (var cache in CharacterCaches.Values)
                NormalizeCharacterIdLists(cache);

            PruneEmptyCharacterCaches();
            Version = 13;
            dirty = true;
        }

        // v14: 0 = live game max for randomize level caps (was hard-coded 100 / 800).
        if (Version < 14)
        {
            if (RandomizeMaxRequiredLevel is 0 or 100)
                RandomizeMaxRequiredLevel = 0;
            if (RandomizeMaxItemLevel is 0 or 800)
                RandomizeMaxItemLevel = 0;

            Version = 14;
            dirty = true;
        }

        // v15: wishlist lists on character caches (defaults empty).
        if (Version < 15)
        {
            foreach (var cache in CharacterCaches.Values)
            {
                cache.WishlistSetRowIds ??= [];
                cache.WishlistPieceKeys ??= [];
                OutfitWishlist.NormalizePieceKeys(cache);
            }

            Version = 15;
            dirty = true;
        }

        // v16: auto-prune eligibility lists (empty = pre-setting wishlist stays).
        if (Version < 16)
        {
            foreach (var cache in CharacterCaches.Values)
            {
                cache.WishlistAutoPruneSetRowIds ??= [];
                cache.WishlistAutoPrunePieceKeys ??= [];
            }

            Version = 16;
            dirty = true;
        }

        if (dirty)
            Save();
    }

    private bool HasAnyIconPath() =>
        !string.IsNullOrWhiteSpace(DresserUiIconPath)
        || !string.IsNullOrWhiteSpace(ArmoireUiIconPath);

    /// <summary>Sort, de-dupe, and drop zero ids on every persisted ownership list.</summary>
    public static void NormalizeCharacterIdLists(CharacterTrackerCache cache)
    {
        cache.DresserBaseIds = NormalizeIds(cache.DresserBaseIds);
        cache.DresserOutfitPieceIds = NormalizeIds(cache.DresserOutfitPieceIds);
        cache.ArmoireBaseIds = NormalizeIds(cache.ArmoireBaseIds);
        cache.DresserSetPresenceRowIds = NormalizeIds(cache.DresserSetPresenceRowIds);
        cache.DresserCompleteSetRowIds = NormalizeIds(cache.DresserCompleteSetRowIds);
        cache.WishlistSetRowIds = NormalizeIds(cache.WishlistSetRowIds ?? []);
        cache.WishlistAutoPruneSetRowIds = NormalizeIds(cache.WishlistAutoPruneSetRowIds ?? []);
        cache.WishlistPieceKeys ??= [];
        cache.WishlistAutoPrunePieceKeys ??= [];
        OutfitWishlist.NormalizePieceKeys(cache);
        OutfitWishlist.NormalizeAutoPrunePieceKeys(cache);
    }

    public static List<uint> NormalizeIds(IEnumerable<uint> ids)
    {
        var set = new SortedSet<uint>();
        foreach (var id in ids)
        {
            if (id != 0)
                set.Add(id);
        }

        return [.. set];
    }

    private void PruneEmptyCharacterCaches()
    {
        var empty = CharacterCaches
            .Where(kv => kv.Key == 0 || kv.Value.IsEmpty())
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in empty)
            CharacterCaches.Remove(key);
    }
}
