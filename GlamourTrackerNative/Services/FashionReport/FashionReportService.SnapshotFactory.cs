using GlamourTracker;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionReportService
{
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
}
