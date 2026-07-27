using System.Collections.Concurrent;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using GlamourTracker;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services.FashionReport;

internal sealed class FashionReportService : IDisposable
{
    private const int OwnershipDebounceMs = 400;

    private static readonly string[] DyeSlotOrder = ["weapon", "head", "body", "hands", "legs", "feet"];

    private static readonly Dictionary<string, string> SlotLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = "Weapon",
        ["head"] = "Head",
        ["body"] = "Body",
        ["hands"] = "Hands",
        ["legs"] = "Legs",
        ["feet"] = "Feet",
        ["ear"] = "Earrings",
        ["neck"] = "Necklace",
        ["wrist"] = "Bracelets",
        ["ring"] = "Ring",
        ["left_ring"] = "Left ring",
        ["right_ring"] = "Right ring",
    };

    private readonly IDataManager dataManager;
    private readonly GlamourOwnershipIndex ownershipIndex;
    private readonly IGameInventory gameInventory;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly FashionReportClient client;
    private readonly FashionVendorLocator vendorLocator;
    private readonly FashionInventoryIndex inventoryIndex;

    private readonly ConcurrentDictionary<string, FashionReportItemDetailDto> itemDetailCache =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, uint>? itemNameToId;
    private CancellationTokenSource? refreshCts;
    private readonly object stateGate = new();
    private readonly object ownershipRefreshGate = new();
    private bool ownershipRefreshPending;
    private DateTime ownershipRefreshDueUtc = DateTime.MinValue;
    private bool frameworkTickSubscribed;

    public FashionReportService(
        IDataManager dataManager,
        GlamourOwnershipIndex ownershipIndex,
        IClientState clientState,
        IObjectTable objectTable,
        IGameInventory gameInventory,
        IFramework framework,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.ownershipIndex = ownershipIndex;
        this.gameInventory = gameInventory;
        this.framework = framework;
        this.log = log;
        client = new FashionReportClient(log);
        vendorLocator = new FashionVendorLocator(dataManager, clientState, objectTable);
        inventoryIndex = new FashionInventoryIndex(gameInventory);

        // Buy / craft / move / split — keep Fashion Report owned + mats counts fresh.
        this.gameInventory.InventoryChanged += OnInventoryChanged;
    }

    public FashionReportSnapshot? Snapshot { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRefreshing { get; private set; }
    public DateTime? LastFetchUtc { get; private set; }

    public Task RefreshAsync(bool force = false)
    {
        CancellationToken ct;
        lock (stateGate)
        {
            if (!force
                && Snapshot != null
                && LastFetchUtc is { } last
                && (DateTime.UtcNow - last).TotalMinutes < 10)
            {
                RebindOwnership();
                return Task.CompletedTask;
            }

            // Soft refreshes should not pile up; forced refresh may supersede an in-flight one.
            if (IsRefreshing && !force)
                return Task.CompletedTask;

            refreshCts?.Cancel();
            refreshCts?.Dispose();
            refreshCts = new CancellationTokenSource();
            ct = refreshCts.Token;
            IsRefreshing = true;
            LastError = null;
        }

        // Do not pass ct into Task.Run — a pre-cancelled token can skip the body and leave
        // IsRefreshing stuck true ("Loading…" forever on later opens).
        return Task.Run(async () =>
        {
            try
            {
                await RefreshCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (stateGate)
                {
                    if (refreshCts is null || refreshCts.Token == ct)
                        IsRefreshing = false;
                }
            }
        });
    }

    public void RebindOwnership()
    {
        var current = Snapshot;
        if (current == null)
            return;

        // Prefer framework thread so LocalPlayer / inventories are available.
        if (framework.IsInFrameworkUpdateThread)
        {
            Snapshot = RebuildWithOwnership(
                current,
                vendorLocator.CapturePlayerContext(),
                inventoryIndex.Scan());
            return;
        }

        _ = framework.RunOnFrameworkThread(() =>
        {
            var snap = Snapshot;
            if (snap != null)
            {
                Snapshot = RebuildWithOwnership(
                    snap,
                    vendorLocator.CapturePlayerContext(),
                    inventoryIndex.Scan());
            }
        });
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (events.Count == 0 || Snapshot == null)
            return;

        ScheduleOwnershipRefresh();
    }

    private void ScheduleOwnershipRefresh()
    {
        lock (ownershipRefreshGate)
        {
            ownershipRefreshPending = true;
            ownershipRefreshDueUtc = DateTime.UtcNow.AddMilliseconds(OwnershipDebounceMs);
            if (!frameworkTickSubscribed)
            {
                framework.Update += OnFrameworkTickForOwnership;
                frameworkTickSubscribed = true;
            }
        }
    }

    private void OnFrameworkTickForOwnership(IFramework _)
    {
        lock (ownershipRefreshGate)
        {
            if (!ownershipRefreshPending)
            {
                if (frameworkTickSubscribed)
                {
                    framework.Update -= OnFrameworkTickForOwnership;
                    frameworkTickSubscribed = false;
                }

                return;
            }

            if (DateTime.UtcNow < ownershipRefreshDueUtc)
                return;

            ownershipRefreshPending = false;
            if (frameworkTickSubscribed)
            {
                framework.Update -= OnFrameworkTickForOwnership;
                frameworkTickSubscribed = false;
            }
        }

        try
        {
            RebindOwnership();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Fashion Report ownership rebind after inventory change failed.");
            PluginFileLog.Warn("fashion.ownership", $"Inventory-driven rebind failed: {ex.Message}");
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            EnsureItemNameIndex();
            var (playerContext, inventory) = await framework
                .RunOnFrameworkThread(() => (vendorLocator.CapturePlayerContext(), inventoryIndex.Scan()))
                .ConfigureAwait(false);

            var state = await client.GetReportStateAsync(ct).ConfigureAwait(false);
            if (state?.LastOptions == null)
            {
                LastError = "Could not load this week's Fashion Report.";
                PluginFileLog.Warn("fashion.sync", "report-state returned empty");
                return;
            }

            var hints = state.LastOptions.Hints ?? [];
            var hintViews = new List<FashionHintSlotView>();
            foreach (var hint in hints)
            {
                if (string.IsNullOrWhiteSpace(hint.Hint) || string.IsNullOrWhiteSpace(hint.Slot))
                    continue;

                var hintItems = await client.GetHintItemsAsync(hint.Hint, hint.Slot, ct).ConfigureAwait(false);
                var cards = hintItems is { Found: true, Items: not null } ? hintItems.Items : [];
                var resolved = new List<FashionResolvedItem>();

                // Bound parallelism keeps refresh responsive without hammering the API.
                using var gate = new SemaphoreSlim(4);
                var tasks = cards
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(async card =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var detail = await GetCachedItemDetailAsync(card.Name!, ct).ConfigureAwait(false);
                            return ResolveItem(
                                card.Name!,
                                card.GarlandUrl,
                                detail,
                                hint.Slot,
                                LabelForSlot(hint.Slot),
                                playerContext,
                                inventory);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    })
                    .ToArray();

                resolved.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
                resolved = RankItems(resolved);
                hintViews.Add(new FashionHintSlotView
                {
                    SlotKey = hint.Slot,
                    SlotLabel = LabelForSlot(hint.Slot),
                    Hint = hint.Hint,
                    RingNote = hint.RingNote is null or "none" ? null : hint.RingNote,
                    Items = resolved,
                    BestPick = resolved.FirstOrDefault(),
                    OwnedCount = resolved.Count(i => i.Owned),
                });
            }

            var dyes = BuildDyeViews(state);
            var easy80 = await BuildEasyAsync("Easy 80", state.Easy80, state.Easy80Fresh, playerContext, inventory, ct)
                .ConfigureAwait(false);
            var easy100 = await BuildEasyAsync("Easy 100", state.Easy100, state.Easy100Fresh, playerContext, inventory, ct)
                .ConfigureAwait(false);

            Snapshot = new FashionReportSnapshot
            {
                Week = state.LastOptions.Week ?? string.Empty,
                Title = state.LastOptions.ReportTitle ?? "Fashion Report",
                DyesFresh = state.DyesFresh,
                TheorycraftUrl = state.Links?.Theorycraft,
                ResultsUrl = state.Links?.Results,
                Hints = hintViews,
                Dyes = dyes,
                Easy80 = easy80,
                Easy100 = easy100,
                FetchedUtc = DateTime.UtcNow,
            };
            LastFetchUtc = Snapshot.FetchedUtc;
            LastError = null;

            var ownedHints = hintViews.Sum(h => h.OwnedCount);
            var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
            PluginFileLog.Info(
                "fashion.sync",
                $"week={Snapshot.Week} title={Snapshot.Title} hints={hintViews.Count} ownedMatches={ownedHints} durationMs={durationMs:0}");
        }
        catch (OperationCanceledException)
        {
            PluginFileLog.Info("fashion.sync", "Refresh cancelled");
        }
        catch (Exception ex)
        {
            // Keep any previous Snapshot so the UI does not blank out on a failed refresh.
            LastError = "Fashion Report refresh failed. See log for details.";
            PluginFileLog.Error("fashion.sync", "Refresh failed", ex);
            this.log.Error(ex, "Fashion Report refresh failed");
        }
    }

    private async Task<FashionEasyOutfitView?> BuildEasyAsync(
        string title,
        FashionReportEasySectionDto? section,
        bool fresh,
        FashionVendorLocator.PlayerAreaContext? playerContext,
        FashionInventorySnapshot inventory,
        CancellationToken ct)
    {
        if (section == null)
            return null;

        var items = new List<FashionResolvedItem>();
        foreach (var pair in section.ItemPairs ?? [])
        {
            if (string.IsNullOrWhiteSpace(pair.Name))
                continue;

            var detail = await GetCachedItemDetailAsync(pair.Name, ct).ConfigureAwait(false);
            items.Add(ResolveItem(
                pair.Name,
                detail?.GarlandUrl,
                detail,
                pair.Slot,
                LabelForSlot(pair.Slot ?? string.Empty),
                playerContext,
                inventory));
        }

        var dyes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (section.Dyes != null)
        {
            foreach (var (slot, dye) in section.Dyes)
            {
                if (!string.IsNullOrWhiteSpace(dye))
                    dyes[slot] = dye;
            }
        }

        return new FashionEasyOutfitView
        {
            Title = title,
            Fresh = fresh,
            Items = items,
            Dyes = dyes,
        };
    }

    private static IReadOnlyList<FashionDyeSlotView> BuildDyeViews(FashionReportStateDto state)
    {
        if (!state.DyesFresh || state.DyeData == null)
            return [];

        var list = new List<FashionDyeSlotView>();
        foreach (var slot in DyeSlotOrder)
        {
            if (!state.DyeData.TryGetValue(slot, out var element) || element.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            string? plus1 = null;
            string? plus2 = null;
            if (element.TryGetProperty("plus1", out var p1) && p1.ValueKind == System.Text.Json.JsonValueKind.String)
                plus1 = p1.GetString();
            if (element.TryGetProperty("plus2", out var p2) && p2.ValueKind == System.Text.Json.JsonValueKind.String)
                plus2 = p2.GetString();

            if (string.IsNullOrWhiteSpace(plus2) && string.IsNullOrWhiteSpace(plus1))
                continue;

            list.Add(new FashionDyeSlotView
            {
                SlotKey = slot,
                SlotLabel = LabelForSlot(slot),
                ExactDye = string.IsNullOrWhiteSpace(plus2) ? null : plus2,
                ColorFamily = string.IsNullOrWhiteSpace(plus1) ? null : plus1,
            });
        }

        return list;
    }

    private async Task<FashionReportItemDetailDto?> GetCachedItemDetailAsync(string name, CancellationToken ct)
    {
        if (itemDetailCache.TryGetValue(name, out var cached))
            return cached;

        var detail = await client.GetItemAsync(name, ct).ConfigureAwait(false);
        if (detail != null)
            itemDetailCache[name] = detail;
        return detail;
    }

    private FashionResolvedItem ResolveItem(
        string name,
        string? garlandUrl,
        FashionReportItemDetailDto? detail,
        string? slotKey,
        string? slotLabel,
        FashionVendorLocator.PlayerAreaContext? playerContext,
        FashionInventorySnapshot inventory)
    {
        var itemId = LookupItemId(name);
        ushort iconId = 0;
        if (itemId != 0 && dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            iconId = item.Icon;

        var gearLocations = ResolveGearLocations(itemId, inventory);
        var owned = gearLocations != FashionGearLocation.None;
        var ownedWhere = FashionInventoryIndex.FormatLocations(gearLocations);

        var sections = detail is { Found: true } ? detail.Sections : null;
        var (kind, summary, vendor, parsedSections) =
            FashionAcquisitionParser.Parse(sections, vendorLocator, playerContext, owned, ownedWhere);

        parsedSections = EnrichCraftSections(parsedSections, inventory);
        var craftIngredients = parsedSections
            .Where(s => s.Type.Equals("craft", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Ingredients)
            .ToList();
        var matsReady = 0;
        foreach (var ing in craftIngredients)
        {
            if (ing.HasEnough)
                matsReady++;
        }

        if (!owned && (detail == null || detail is { Found: false } || parsedSections.Count == 0))
        {
            if (detail is not { Found: true } || parsedSections.Count == 0)
            {
                summary = "No location data found";
                if (parsedSections.Count == 0)
                    kind = FashionItemAcquireKind.Unknown;
            }
        }

        return new FashionResolvedItem
        {
            Name = name,
            ItemId = itemId,
            IconId = iconId,
            Owned = owned,
            GearLocations = gearLocations,
            AcquireKind = kind,
            Summary = summary,
            PreferredVendor = vendor,
            Sections = parsedSections,
            CraftIngredients = craftIngredients,
            HasCraftRecipe = craftIngredients.Count > 0
                             || parsedSections.Any(s => s.Type.Equals("craft", StringComparison.OrdinalIgnoreCase)),
            CraftMatsReady = matsReady,
            CraftMatsTotal = craftIngredients.Count,
            GarlandUrl = detail?.GarlandUrl ?? garlandUrl,
            LodestoneUrl = detail?.LodestoneUrl,
            SlotKey = slotKey,
            SlotLabel = slotLabel,
        };
    }

    private List<FashionAcquireSection> EnrichCraftSections(
        List<FashionAcquireSection> sections,
        FashionInventorySnapshot inventory)
    {
        var result = new List<FashionAcquireSection>(sections.Count);
        foreach (var section in sections)
        {
            if (!section.Type.Equals("craft", StringComparison.OrdinalIgnoreCase) || section.Ingredients.Count == 0)
            {
                result.Add(section);
                continue;
            }

            var ingredients = section.Ingredients.Select(ing =>
            {
                var id = LookupItemId(ing.Name);
                return new FashionCraftIngredient
                {
                    Name = ing.Name,
                    ItemId = id,
                    Required = ing.Required,
                    OwnedCount = id == 0 ? 0 : inventory.GetCount(id),
                };
            }).ToList();

            var allReady = ingredients.Count > 0 && ingredients.All(i => i.HasEnough);
            result.Add(new FashionAcquireSection
            {
                Type = section.Type,
                Label = section.Label,
                Headline = allReady && !string.IsNullOrWhiteSpace(section.Headline)
                    ? $"{section.Headline} · mats ready"
                    : section.Headline,
                Ingredients = ingredients,
                Lines = ingredients.Select(FormatIngredientLine).ToList(),
            });
        }

        return result;
    }

    private static string FormatIngredientLine(FashionCraftIngredient ing) =>
        $"{ing.Required}× {ing.Name} — {ing.OwnedCount}/{ing.Required}";

    private FashionGearLocation ResolveGearLocations(uint itemId, FashionInventorySnapshot inventory)
    {
        if (itemId == 0)
            return FashionGearLocation.None;

        var loc = inventory.GetCarryLocations(itemId);
        var glam = ownershipIndex.GetStorage(itemId);
        if (glam.HasFlag(GlamourStorageLocation.Dresser))
            loc |= FashionGearLocation.Dresser;
        if (glam.HasFlag(GlamourStorageLocation.Armoire))
            loc |= FashionGearLocation.Armoire;
        return loc;
    }

    private FashionReportSnapshot RebuildWithOwnership(
        FashionReportSnapshot current,
        FashionVendorLocator.PlayerAreaContext? playerContext,
        FashionInventorySnapshot inventory)
    {
        var hints = current.Hints.Select(h =>
        {
            var items = RankItems(h.Items.Select(i => RebindItem(i, playerContext, inventory)).ToList());
            return new FashionHintSlotView
            {
                SlotKey = h.SlotKey,
                SlotLabel = h.SlotLabel,
                Hint = h.Hint,
                RingNote = h.RingNote,
                Items = items,
                BestPick = items.FirstOrDefault(),
                OwnedCount = items.Count(i => i.Owned),
            };
        }).ToList();

        FashionEasyOutfitView? RebindEasy(FashionEasyOutfitView? easy)
        {
            if (easy == null)
                return null;
            return new FashionEasyOutfitView
            {
                Title = easy.Title,
                Fresh = easy.Fresh,
                Items = easy.Items.Select(i => RebindItem(i, playerContext, inventory)).ToList(),
                Dyes = easy.Dyes,
            };
        }

        return new FashionReportSnapshot
        {
            Week = current.Week,
            Title = current.Title,
            DyesFresh = current.DyesFresh,
            TheorycraftUrl = current.TheorycraftUrl,
            ResultsUrl = current.ResultsUrl,
            Hints = hints,
            Dyes = current.Dyes,
            Easy80 = RebindEasy(current.Easy80),
            Easy100 = RebindEasy(current.Easy100),
            FetchedUtc = current.FetchedUtc,
        };
    }

    private FashionResolvedItem RebindItem(
        FashionResolvedItem item,
        FashionVendorLocator.PlayerAreaContext? playerContext,
        FashionInventorySnapshot inventory)
    {
        if (itemDetailCache.TryGetValue(item.Name, out var detail))
            return ResolveItem(item.Name, item.GarlandUrl, detail, item.SlotKey, item.SlotLabel, playerContext, inventory);

        var gearLocations = ResolveGearLocations(item.ItemId, inventory);
        var owned = gearLocations != FashionGearLocation.None;
        var ownedWhere = FashionInventoryIndex.FormatLocations(gearLocations);
        var (_, summary, vendor, sections) =
            FashionAcquisitionParser.Parse(null, vendorLocator, playerContext, owned, ownedWhere);

        // Refresh craft mat counts even when ownership flags are unchanged.
        var craftIngredients = RefreshIngredientCounts(item.CraftIngredients, inventory);
        var matsReady = 0;
        foreach (var ing in craftIngredients)
        {
            if (ing.HasEnough)
                matsReady++;
        }

        return new FashionResolvedItem
        {
            Name = item.Name,
            ItemId = item.ItemId,
            IconId = item.IconId,
            Owned = owned,
            GearLocations = gearLocations,
            AcquireKind = owned ? FashionItemAcquireKind.Owned : item.AcquireKind,
            Summary = owned ? summary : item.Summary,
            PreferredVendor = vendor ?? item.PreferredVendor,
            Sections = sections.Count > 0 ? sections : item.Sections,
            CraftIngredients = craftIngredients,
            HasCraftRecipe = item.HasCraftRecipe || craftIngredients.Count > 0,
            CraftMatsReady = matsReady,
            CraftMatsTotal = craftIngredients.Count,
            GarlandUrl = item.GarlandUrl,
            LodestoneUrl = item.LodestoneUrl,
            SlotKey = item.SlotKey,
            SlotLabel = item.SlotLabel,
        };
    }

    private static IReadOnlyList<FashionCraftIngredient> RefreshIngredientCounts(
        IReadOnlyList<FashionCraftIngredient> ingredients,
        FashionInventorySnapshot inventory)
    {
        if (ingredients.Count == 0)
            return ingredients;

        var updated = new FashionCraftIngredient[ingredients.Count];
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ing = ingredients[i];
            var owned = ing.ItemId == 0 ? 0 : inventory.GetCount(ing.ItemId);
            updated[i] = new FashionCraftIngredient
            {
                Name = ing.Name,
                ItemId = ing.ItemId,
                Required = ing.Required,
                OwnedCount = owned,
            };
        }

        return updated;
    }

    private static List<FashionResolvedItem> RankItems(List<FashionResolvedItem> items)
    {
        return items
            .OrderBy(i => KindRank(i.AcquireKind))
            .ThenBy(i => i.PreferredVendor?.Gil ?? int.MaxValue)
            .ThenBy(i => i.PreferredVendor is { SameArea: true } ? 0 : 1)
            .ThenBy(i => i.PreferredVendor?.DistanceSquared ?? float.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int KindRank(FashionItemAcquireKind kind) => kind switch
    {
        FashionItemAcquireKind.Owned => 0,
        FashionItemAcquireKind.Vendor => 1,
        FashionItemAcquireKind.Craft => 2,
        FashionItemAcquireKind.Quest => 3,
        FashionItemAcquireKind.Exchange => 4,
        FashionItemAcquireKind.GrandCompany => 5,
        FashionItemAcquireKind.Achievement => 6,
        FashionItemAcquireKind.DutyDrop => 7,
        FashionItemAcquireKind.TreasureCoffer => 8,
        FashionItemAcquireKind.Market => 9,
        _ => 10,
    };

    private void EnsureItemNameIndex()
    {
        if (itemNameToId != null)
            return;

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var sheet = dataManager.GetExcelSheet<Item>();
        foreach (var item in sheet)
        {
            if (item.RowId == 0)
                continue;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!map.ContainsKey(name))
                map[name] = item.RowId;
        }

        itemNameToId = map;
        PluginFileLog.Info("fashion.index", $"item name index built ({map.Count} names)");
    }

    /// <summary>Resolve acquisition for an arbitrary item name (Outfit sets, etc.).</summary>
    public async Task<FashionResolvedItem> ResolveNamedItemAsync(string name, CancellationToken ct = default)
    {
        var (playerContext, inventory) = await framework
            .RunOnFrameworkThread(() => (vendorLocator.CapturePlayerContext(), inventoryIndex.Scan()))
            .ConfigureAwait(false);
        var detail = await GetCachedItemDetailAsync(name, ct).ConfigureAwait(false);
        return ResolveItem(name, detail?.GarlandUrl, detail, null, null, playerContext, inventory);
    }

    private uint LookupItemId(string name)
    {
        EnsureItemNameIndex();
        return itemNameToId!.TryGetValue(name, out var id) ? id : 0;
    }

    private static string LabelForSlot(string slot) =>
        SlotLabels.TryGetValue(slot, out var label) ? label : slot;

    public void Dispose()
    {
        gameInventory.InventoryChanged -= OnInventoryChanged;
        lock (ownershipRefreshGate)
        {
            if (frameworkTickSubscribed)
            {
                framework.Update -= OnFrameworkTickForOwnership;
                frameworkTickSubscribed = false;
            }

            ownershipRefreshPending = false;
        }

        refreshCts?.Cancel();
        refreshCts?.Dispose();
        client.Dispose();
    }
}
