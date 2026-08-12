using System.Collections.Concurrent;
using System.Text.Json;
using Dalamud.Plugin;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;

namespace GlamourTracker.Services;

/// <summary>
/// Persists outfit-set source categories (and per-item acquire kinds) so filters
/// do not re-hit the network every session.
/// </summary>
internal sealed class OutfitSetCategoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string categoriesPath;
    private readonly string itemKindsPath;
    private readonly object gate = new();
    private Dictionary<uint, int>? categories;
    private Dictionary<uint, int>? itemKinds;

    public OutfitSetCategoryStore(IDalamudPluginInterface pluginInterface)
    {
        var dir = pluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(dir);
        categoriesPath = Path.Combine(dir, "outfit-set-categories.json");
        itemKindsPath = Path.Combine(dir, "outfit-item-acquire-kinds.json");
    }

    public void Hydrate(ConcurrentDictionary<uint, OutfitCategoryFilter> target)
    {
        var map = EnsureCategories();
        foreach (var (setId, raw) in map)
        {
            if (!Enum.IsDefined(typeof(OutfitCategoryFilter), raw))
                continue;
            var cat = (OutfitCategoryFilter)raw;
            if (cat == OutfitCategoryFilter.All)
                continue;
            target.TryAdd(setId, cat);
        }
    }

    public void HydrateItemKinds(ConcurrentDictionary<uint, FashionItemAcquireKind> target)
    {
        var map = EnsureItemKinds();
        foreach (var (itemId, raw) in map)
        {
            if (!Enum.IsDefined(typeof(FashionItemAcquireKind), raw))
                continue;
            var kind = (FashionItemAcquireKind)raw;
            if (kind == FashionItemAcquireKind.Unknown)
                continue;
            target.TryAdd(itemId, kind);
        }
    }

    public void UpsertMany(IEnumerable<KeyValuePair<uint, OutfitCategoryFilter>> entries)
    {
        lock (gate)
        {
            var map = EnsureCategoriesUnlocked();
            var dirty = false;
            foreach (var (setId, cat) in entries)
            {
                if (setId == 0 || cat == OutfitCategoryFilter.All)
                    continue;
                var raw = (int)cat;
                if (map.TryGetValue(setId, out var existing) && existing == raw)
                    continue;
                map[setId] = raw;
                dirty = true;
            }

            if (dirty)
                SaveIntMap(categoriesPath, map);
        }
    }

    public void Upsert(uint setId, OutfitCategoryFilter cat)
    {
        if (setId == 0 || cat == OutfitCategoryFilter.All)
            return;
        UpsertMany([new KeyValuePair<uint, OutfitCategoryFilter>(setId, cat)]);
    }

    public void UpsertItemKinds(IEnumerable<KeyValuePair<uint, FashionItemAcquireKind>> entries)
    {
        lock (gate)
        {
            var map = EnsureItemKindsUnlocked();
            var dirty = false;
            foreach (var (itemId, kind) in entries)
            {
                if (itemId == 0 || kind == FashionItemAcquireKind.Unknown)
                    continue;
                var raw = (int)kind;
                if (map.TryGetValue(itemId, out var existing) && existing == raw)
                    continue;
                map[itemId] = raw;
                dirty = true;
            }

            if (dirty)
                SaveIntMap(itemKindsPath, map);
        }
    }

    private Dictionary<uint, int> EnsureCategories()
    {
        lock (gate)
            return EnsureCategoriesUnlocked();
    }

    private Dictionary<uint, int> EnsureItemKinds()
    {
        lock (gate)
            return EnsureItemKindsUnlocked();
    }

    private Dictionary<uint, int> EnsureCategoriesUnlocked()
    {
        if (categories != null)
            return categories;

        categories = LoadIntMap(categoriesPath, "categories");
        return categories;
    }

    private Dictionary<uint, int> EnsureItemKindsUnlocked()
    {
        if (itemKinds != null)
            return itemKinds;

        itemKinds = LoadIntMap(itemKindsPath, "item kinds");
        return itemKinds;
    }

    private static Dictionary<uint, int> LoadIntMap(string path, string label)
    {
        var map = new Dictionary<uint, int>();
        try
        {
            if (!File.Exists(path))
                return map;

            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<StoreDto>(json, JsonOptions);
            if (dto?.Entries == null && dto?.Categories == null)
                return map;

            // Categories file used "Categories"; item-kinds uses "Entries". Accept both.
            var source = dto.Entries ?? dto.Categories;
            if (source == null)
                return map;

            foreach (var (key, value) in source)
            {
                if (uint.TryParse(key, out var id) && id != 0)
                    map[id] = value;
            }

            PluginFileLog.Info("outfit.category", $"Loaded persisted {label} ({map.Count})");
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("outfit.category", $"Failed loading {label}: {ex.Message}");
            map = new Dictionary<uint, int>();
        }

        return map;
    }

    private static void SaveIntMap(string path, Dictionary<uint, int> map)
    {
        try
        {
            var dto = new StoreDto
            {
                Entries = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                // Keep writing Categories too for the set-categories file shape older builds expect.
                Categories = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("outfit.category", $"Failed saving {path}: {ex.Message}");
        }
    }

    private sealed class StoreDto
    {
        public Dictionary<string, int>? Categories { get; set; }
        public Dictionary<string, int>? Entries { get; set; }
    }
}
