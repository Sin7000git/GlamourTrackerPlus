using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;

namespace GlamourTracker.Windows;

internal sealed partial class FashionReportNativeAddon
{
    private List<FashionReportNativeRow> BuildRows(FashionReportSnapshot? snap)
    {
        if (snap == null)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = "empty",
                    Title = "No Fashion Report loaded",
                    Subtitle = "Press Refresh week",
                },
            ];
        }

        if (selectedTabKey == TabDyes)
            return BuildDyeRows(snap);

        var easy100Key = TabKeyForEasy(snap.Easy100, "Easy 100");
        if (selectedTabKey == easy100Key)
            return BuildEasyRows(snap.Easy100, easy100Key);

        var easy80Key = TabKeyForEasy(snap.Easy80, "Easy 80");
        if (selectedTabKey == easy80Key)
            return BuildEasyRows(snap.Easy80, easy80Key);

        var hint = snap.Hints.FirstOrDefault(h => TabKeyForHint(h) == selectedTabKey);
        if (hint == null)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = "missing-tab",
                    Title = "Slot unavailable",
                    Subtitle = "Refresh week and try again",
                },
            ];
        }

        var rows = new List<FashionReportNativeRow>();
        foreach (var item in hint.Items)
        {
            if (ownedOnly && !item.Owned)
                continue;
            rows.Add(RowForItem(item, hint.SlotKey));
        }

        if (rows.Count == 0)
        {
            rows.Add(new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Info,
                Key = $"empty-{hint.SlotKey}",
                Title = ownedOnly ? "No owned pieces" : "No items listed",
                Subtitle = hint.Hint,
            });
        }

        return rows;
    }

    private List<FashionReportNativeRow> BuildDyeRows(FashionReportSnapshot snap)
    {
        if (snap.Dyes.Count == 0)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = "dye-empty",
                    Title = snap.DyesFresh ? "No dye data" : "Dyes not available yet",
                    Subtitle = snap.DyesFresh ? string.Empty : "Usually posts Friday",
                },
            ];
        }

        return snap.Dyes.Select(dye =>
        {
            var exact = string.IsNullOrWhiteSpace(dye.ExactDye) ? "—" : dye.ExactDye;
            var family = string.IsNullOrWhiteSpace(dye.ColorFamily) ? "—" : dye.ColorFamily;
            return new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Dye,
                Key = $"dye-{dye.SlotKey}",
                Title = $"{dye.SlotLabel}: {exact}",
                Subtitle = $"Family: {family}",
                IconId = ResolveDyeIcon(exact),
            };
        }).ToList();
    }

    private List<FashionReportNativeRow> BuildEasyRows(FashionEasyOutfitView? easy, string tabKey)
    {
        if (easy == null)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = $"{tabKey}-missing",
                    Title = "Not available",
                },
            ];
        }

        if (!easy.Fresh)
        {
            return
            [
                new FashionReportNativeRow
                {
                    Kind = FashionReportNativeRowKind.Info,
                    Key = $"{tabKey}-stale",
                    Title = "Not ready yet",
                    Subtitle = "Waiting for dye confirmation",
                },
            ];
        }

        var rows = new List<FashionReportNativeRow>();
        foreach (var item in easy.Items)
        {
            if (ownedOnly && !item.Owned)
                continue;
            rows.Add(RowForItem(item, "easy-" + easy.Title));
        }

        foreach (var (slot, dye) in easy.Dyes)
        {
            rows.Add(new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Dye,
                Key = $"{tabKey}-dye-{slot}",
                Title = $"{slot}: {dye}",
                Subtitle = "Outfit dye",
                IconId = ResolveDyeIcon(dye),
            });
        }

        if (rows.Count == 0)
        {
            rows.Add(new FashionReportNativeRow
            {
                Kind = FashionReportNativeRowKind.Info,
                Key = $"{tabKey}-empty",
                Title = ownedOnly ? "No owned pieces" : "No items listed",
            });
        }

        return rows;
    }

    private static FashionReportNativeRow RowForItem(FashionResolvedItem item, string scope) =>
        new()
        {
            Kind = FashionReportNativeRowKind.Item,
            Key = $"{scope}|{item.ItemId}|{item.Name}",
            Title = item.Name,
            Subtitle = item.Summary,
            IconId = item.IconId,
            Badge = FashionReportNativeHelpers.ListBadge(item),
            BadgeColor = FashionReportNativeHelpers.ListBadgeColor(item),
            Item = item,
        };

    private ushort ResolveDyeIcon(string? dyeName)
    {
        if (string.IsNullOrWhiteSpace(dyeName) || dyeName == "—")
            return 0;

        if (dyeIconCache.TryGetValue(dyeName, out var cached))
            return cached;

        var icon = DyeIconIndex.Resolve(Plugin.DataManager, dyeName);
        dyeIconCache[dyeName] = icon;
        return icon;
    }
}
