using Lumina.Excel.Sheets;
using GlamourTracker;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionReportService
{
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
}
