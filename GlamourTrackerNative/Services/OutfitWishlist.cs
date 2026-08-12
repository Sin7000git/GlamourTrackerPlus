using System.Globalization;

namespace GlamourTracker.Services;

/// <summary>Per-character outfit wishlist helpers (sets and individual pieces).</summary>
internal static class OutfitWishlist
{
    public static string PieceKey(uint setId, uint itemId) =>
        string.Create(CultureInfo.InvariantCulture, $"{setId}:{ItemIdHelper.GlamourBaseId(itemId)}");

    public static bool TryParsePieceKey(string key, out uint setId, out uint itemId)
    {
        setId = 0;
        itemId = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sep = key.IndexOf(':');
        if (sep <= 0 || sep >= key.Length - 1)
            return false;

        return uint.TryParse(key.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out setId)
               && uint.TryParse(key.AsSpan(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId)
               && setId != 0
               && itemId != 0;
    }

    public static CharacterTrackerCache? TryGetCache(Configuration config, ulong contentId)
    {
        if (contentId == 0)
            return null;
        return config.CharacterCaches.TryGetValue(contentId, out var cache) ? cache : null;
    }

    public static CharacterTrackerCache GetOrCreateCache(Configuration config, ulong contentId)
    {
        if (contentId == 0)
            throw new ArgumentOutOfRangeException(nameof(contentId));

        if (!config.CharacterCaches.TryGetValue(contentId, out var cache))
        {
            cache = new CharacterTrackerCache();
            config.CharacterCaches[contentId] = cache;
        }

        return cache;
    }

    /// <summary>Prefer <see cref="TryGetCache"/> for reads; this creates an empty cache when missing.</summary>
    public static CharacterTrackerCache? GetCache(Configuration config, ulong contentId) =>
        contentId == 0 ? null : GetOrCreateCache(config, contentId);

    public static bool IsSetWishlisted(CharacterTrackerCache cache, uint setId) =>
        setId != 0 && cache.WishlistSetRowIds.Contains(setId);

    public static bool IsPieceWishlisted(CharacterTrackerCache cache, uint setId, uint itemId) =>
        setId != 0 && itemId != 0 && cache.WishlistPieceKeys.Contains(PieceKey(setId, itemId));

    public static bool SetHasWishlistMatch(CharacterTrackerCache cache, OutfitSetInfo set)
    {
        if (IsSetWishlisted(cache, set.SetId))
            return true;

        foreach (var piece in set.Pieces)
        {
            if (IsPieceWishlisted(cache, set.SetId, piece.ItemId))
                return true;
        }

        return false;
    }

    /// <param name="markAutoPrune">
    /// When true and the entry is newly added, mark it for auto-remove-when-owned.
    /// Pre-existing wishlist entries are never marked this way.
    /// </param>
    public static bool ToggleSet(CharacterTrackerCache cache, uint setId, bool markAutoPrune = false)
    {
        if (setId == 0)
            return false;

        if (cache.WishlistSetRowIds.Remove(setId))
        {
            cache.WishlistAutoPruneSetRowIds.Remove(setId);
            cache.WishlistSetRowIds = Configuration.NormalizeIds(cache.WishlistSetRowIds);
            cache.WishlistAutoPruneSetRowIds = Configuration.NormalizeIds(cache.WishlistAutoPruneSetRowIds);
            return true;
        }

        cache.WishlistSetRowIds.Add(setId);
        if (markAutoPrune)
            cache.WishlistAutoPruneSetRowIds.Add(setId);

        cache.WishlistSetRowIds = Configuration.NormalizeIds(cache.WishlistSetRowIds);
        cache.WishlistAutoPruneSetRowIds = Configuration.NormalizeIds(cache.WishlistAutoPruneSetRowIds);
        return true;
    }

    /// <inheritdoc cref="ToggleSet"/>
    public static bool TogglePiece(CharacterTrackerCache cache, uint setId, uint itemId, bool markAutoPrune = false)
    {
        if (setId == 0 || itemId == 0)
            return false;

        var key = PieceKey(setId, itemId);
        if (cache.WishlistPieceKeys.Remove(key))
        {
            cache.WishlistAutoPrunePieceKeys.Remove(key);
            NormalizePieceKeys(cache);
            NormalizeAutoPrunePieceKeys(cache);
            return true;
        }

        cache.WishlistPieceKeys.Add(key);
        if (markAutoPrune)
            cache.WishlistAutoPrunePieceKeys.Add(key);

        NormalizePieceKeys(cache);
        NormalizeAutoPrunePieceKeys(cache);
        return true;
    }

    public static void NormalizePieceKeys(CharacterTrackerCache cache)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in cache.WishlistPieceKeys)
        {
            if (TryParsePieceKey(key, out _, out _))
                set.Add(key);
        }

        cache.WishlistPieceKeys = [.. set];
    }

    public static void NormalizeAutoPrunePieceKeys(CharacterTrackerCache cache)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in cache.WishlistAutoPrunePieceKeys)
        {
            if (TryParsePieceKey(key, out _, out _) && cache.WishlistPieceKeys.Contains(key))
                set.Add(key);
        }

        cache.WishlistAutoPrunePieceKeys = [.. set];
    }

    /// <summary>
    /// Remove wishlisted sets that are complete and pieces that are stored,
    /// regardless of auto-prune eligibility. Returns true if anything was removed.
    /// </summary>
    public static bool ClearOwned(
        Configuration config,
        OutfitSetCatalog outfitSets,
        ulong contentId)
    {
        if (contentId == 0)
            return false;

        var cache = TryGetCache(config, contentId);
        if (cache == null)
            return false;

        if (cache.WishlistSetRowIds.Count == 0 && cache.WishlistPieceKeys.Count == 0)
            return false;

        var setsById = outfitSets.GetSets().ToDictionary(s => s.SetId);
        var dirty = false;

        foreach (var setId in cache.WishlistSetRowIds.ToList())
        {
            if (!setsById.TryGetValue(setId, out var set) || set.MissingPieces > 0)
                continue;

            cache.WishlistSetRowIds.Remove(setId);
            cache.WishlistAutoPruneSetRowIds.Remove(setId);
            dirty = true;
        }

        foreach (var key in cache.WishlistPieceKeys.ToList())
        {
            if (!TryParsePieceKey(key, out var setId, out var itemId))
                continue;
            if (!setsById.TryGetValue(setId, out var set))
                continue;

            var piece = set.Pieces.FirstOrDefault(p =>
                ItemIdHelper.GlamourBaseId(p.ItemId) == ItemIdHelper.GlamourBaseId(itemId));
            if (piece.ItemId == 0 || piece.Storage == GlamourStorageLocation.None)
                continue;

            cache.WishlistPieceKeys.Remove(key);
            cache.WishlistAutoPrunePieceKeys.Remove(key);
            dirty = true;
        }

        if (!dirty)
            return false;

        cache.WishlistSetRowIds = Configuration.NormalizeIds(cache.WishlistSetRowIds);
        cache.WishlistAutoPruneSetRowIds = Configuration.NormalizeIds(cache.WishlistAutoPruneSetRowIds);
        NormalizePieceKeys(cache);
        NormalizeAutoPrunePieceKeys(cache);
        config.Save();
        return true;
    }

    /// <summary>
    /// Drop auto-prune-eligible wishlist entries that are now owned (piece stored / set complete).
    /// Only entries marked when the setting was on are eligible — older wishlist items are left alone.
    /// </summary>
    public static bool PruneOwnedIfEnabled(
        Configuration config,
        OutfitSetCatalog outfitSets,
        ulong contentId)
    {
        if (!config.AutoRemoveOwnedWishlist || contentId == 0)
            return false;

        var cache = TryGetCache(config, contentId);
        if (cache == null)
            return false;

        if (cache.WishlistAutoPruneSetRowIds.Count == 0 && cache.WishlistAutoPrunePieceKeys.Count == 0)
            return false;

        var setsById = outfitSets.GetSets().ToDictionary(s => s.SetId);
        var dirty = false;

        foreach (var setId in cache.WishlistAutoPruneSetRowIds.ToList())
        {
            if (!cache.WishlistSetRowIds.Contains(setId))
            {
                cache.WishlistAutoPruneSetRowIds.Remove(setId);
                dirty = true;
                continue;
            }

            if (!setsById.TryGetValue(setId, out var set) || set.MissingPieces > 0)
                continue;

            cache.WishlistSetRowIds.Remove(setId);
            cache.WishlistAutoPruneSetRowIds.Remove(setId);
            dirty = true;
        }

        foreach (var key in cache.WishlistAutoPrunePieceKeys.ToList())
        {
            if (!cache.WishlistPieceKeys.Contains(key))
            {
                cache.WishlistAutoPrunePieceKeys.Remove(key);
                dirty = true;
                continue;
            }

            if (!TryParsePieceKey(key, out var setId, out var itemId))
                continue;
            if (!setsById.TryGetValue(setId, out var set))
                continue;

            var piece = set.Pieces.FirstOrDefault(p =>
                ItemIdHelper.GlamourBaseId(p.ItemId) == ItemIdHelper.GlamourBaseId(itemId));
            if (piece.ItemId == 0 || piece.Storage == GlamourStorageLocation.None)
                continue;

            cache.WishlistPieceKeys.Remove(key);
            cache.WishlistAutoPrunePieceKeys.Remove(key);
            dirty = true;
        }

        if (!dirty)
            return false;

        cache.WishlistSetRowIds = Configuration.NormalizeIds(cache.WishlistSetRowIds);
        cache.WishlistAutoPruneSetRowIds = Configuration.NormalizeIds(cache.WishlistAutoPruneSetRowIds);
        NormalizePieceKeys(cache);
        NormalizeAutoPrunePieceKeys(cache);
        config.Save();
        return true;
    }
}
