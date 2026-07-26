using System.Text.Json;

namespace GlamourTracker.Services.FashionReport;

internal static class FashionAcquisitionParser
{
    public static (FashionItemAcquireKind Kind, string Summary, FashionVendorPick? Vendor, List<FashionAcquireSection> Sections)
        Parse(
            IReadOnlyList<JsonElement>? sections,
            FashionVendorLocator locator,
            FashionVendorLocator.PlayerAreaContext? playerContext,
            bool owned,
            string? ownedWhereLabel)
    {
        var parsed = new List<FashionAcquireSection>();
        FashionVendorPick? preferredVendor = null;
        var hasMarket = false;
        FashionAcquireSection? craftSection = null;
        FashionAcquireSection? questSection = null;
        FashionAcquireSection? exchangeSection = null;
        FashionAcquireSection? gcSection = null;
        FashionAcquireSection? achieveSection = null;
        FashionAcquireSection? dutySection = null;
        FashionAcquireSection? cofferSection = null;

        if (sections != null)
        {
            foreach (var element in sections)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var type = GetString(element, "type");
                if (string.IsNullOrEmpty(type))
                    continue;

                switch (type.ToLowerInvariant())
                {
                    case "market":
                        hasMarket = true;
                        parsed.Add(new FashionAcquireSection
                        {
                            Type = type,
                            Label = "Market Board",
                            Headline = "Available on the Market Board",
                        });
                        break;

                    case "vendor":
                    {
                        // Exchange/barter NPCs sometimes appear elsewhere; only real gil shops count here.
                        var price = GetInt(element, "price") ?? 0;
                        if (price <= 0)
                            break;

                        var vendors = ReadVendors(element);
                        preferredVendor ??= locator.PickBest(vendors, price, playerContext);
                        var lines = vendors.Select(v => FormatVendorLine(v.Name, v.Loc)).ToList();
                        parsed.Add(new FashionAcquireSection
                        {
                            Type = type,
                            Label = "NPC Vendor",
                            Headline = $"{price:N0} gil",
                            Lines = lines,
                        });
                        break;
                    }

                    case "craft":
                        craftSection = ParseCraft(element);
                        if (craftSection != null)
                            parsed.Add(craftSection);
                        break;

                    case "quest":
                        questSection = ParseQuest(element, locator, playerContext, ref preferredVendor);
                        if (questSection != null)
                            parsed.Add(questSection);
                        break;

                    case "barter":
                        exchangeSection = ParseBarter(element);
                        if (exchangeSection != null)
                            parsed.Add(exchangeSection);
                        break;

                    case "gc":
                        gcSection = new FashionAcquireSection
                        {
                            Type = type,
                            Label = "Grand Company",
                            Headline = FormatGc(element),
                        };
                        parsed.Add(gcSection);
                        break;

                    case "achievement":
                        achieveSection = ParseAchievement(element);
                        if (achieveSection != null)
                            parsed.Add(achieveSection);
                        break;

                    case "duty_drop":
                        dutySection = new FashionAcquireSection
                        {
                            Type = type,
                            Label = "Chest Drop",
                            Lines = ReadStringArray(element, "duties"),
                        };
                        parsed.Add(dutySection);
                        break;

                    case "coffer":
                        cofferSection = new FashionAcquireSection
                        {
                            Type = type,
                            Label = "Treasure Coffer",
                            Headline = GetString(element, "name"),
                            Lines = ReadStringArray(element, "duties").Select(d => $"From: {d}").ToList(),
                        };
                        parsed.Add(cofferSection);
                        break;
                }
            }
        }

        if (owned)
        {
            var where = string.IsNullOrWhiteSpace(ownedWhereLabel) ? "stored" : ownedWhereLabel;
            return (FashionItemAcquireKind.Owned, $"You already have this ({where})", preferredVendor, parsed);
        }

        if (preferredVendor is { Gil: > 0 })
        {
            var summary = preferredVendor.SameArea
                ? $"Nearby vendor · {preferredVendor.Gil:N0} gil · {preferredVendor.Name} · {preferredVendor.Location}"
                : $"NPC vendor · {preferredVendor.Gil:N0} gil · {preferredVendor.Name} · {preferredVendor.Location}";
            return (FashionItemAcquireKind.Vendor, summary, preferredVendor, parsed);
        }

        if (craftSection != null)
            return (FashionItemAcquireKind.Craft, craftSection.Headline ?? "Crafting", null, parsed);

        if (questSection != null)
            return (FashionItemAcquireKind.Quest, questSection.Headline ?? "Quest reward", preferredVendor, parsed);

        if (exchangeSection != null)
        {
            var exchangeSummary = exchangeSection.Headline ?? "Exchange";
            var npcLine = exchangeSection.Lines.FirstOrDefault(l => !l.StartsWith("From:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(npcLine))
                exchangeSummary = $"{exchangeSummary} · {npcLine}";
            return (FashionItemAcquireKind.Exchange, exchangeSummary, null, parsed);
        }

        if (gcSection != null)
            return (FashionItemAcquireKind.GrandCompany, gcSection.Headline ?? "Grand Company", preferredVendor, parsed);

        if (achieveSection != null)
            return (FashionItemAcquireKind.Achievement, achieveSection.Headline ?? "Achievement", preferredVendor, parsed);

        if (dutySection != null)
            return (FashionItemAcquireKind.DutyDrop, "Chest drop", preferredVendor, parsed);

        if (cofferSection != null)
            return (FashionItemAcquireKind.TreasureCoffer, cofferSection.Headline ?? "Treasure coffer", preferredVendor, parsed);

        if (hasMarket)
            return (FashionItemAcquireKind.Market, "Market Board", preferredVendor, parsed);

        if (parsed.Count == 0)
            return (FashionItemAcquireKind.Unknown, "No location data found", preferredVendor, parsed);

        return (FashionItemAcquireKind.Unknown, parsed[0].Label, preferredVendor, parsed);
    }

    private static FashionAcquireSection? ParseCraft(JsonElement element)
    {
        if (!element.TryGetProperty("recipes", out var recipes) || recipes.ValueKind != JsonValueKind.Array || recipes.GetArrayLength() == 0)
            return null;

        var first = recipes[0];
        var job = GetString(first, "job") ?? "Crafter";
        var level = GetInt(first, "level");
        var stars = GetInt(first, "stars") ?? 0;
        var ilvl = GetInt(first, "ilvl");
        var starText = stars > 0 ? " " + new string('★', stars) : string.Empty;
        var headline = level is { } lv
            ? $"{job} Lv.{lv}{starText}" + (ilvl is { } i ? $" → item level {i}" : string.Empty)
            : job;

        var ingredients = new List<FashionCraftIngredient>();
        if (first.TryGetProperty("ingredients", out var ings) && ings.ValueKind == JsonValueKind.Array)
        {
            foreach (var ing in ings.EnumerateArray())
            {
                var name = GetString(ing, "name");
                var amount = GetInt(ing, "amount") ?? 1;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                ingredients.Add(new FashionCraftIngredient
                {
                    Name = name,
                    Required = amount,
                });
            }
        }

        return new FashionAcquireSection
        {
            Type = "craft",
            Label = "Crafting",
            Headline = headline,
            Ingredients = ingredients,
            Lines = ingredients.Select(i => $"{i.Required}× {i.Name}").ToList(),
        };
    }

    private static FashionAcquireSection? ParseQuest(
        JsonElement element,
        FashionVendorLocator locator,
        FashionVendorLocator.PlayerAreaContext? playerContext,
        ref FashionVendorPick? preferredVendor)
    {
        var quests = ReadStringArray(element, "quests");
        var price = GetInt(element, "price");
        var vendors = ReadVendors(element);
        if (preferredVendor == null && vendors.Count > 0 && price is > 0)
            preferredVendor = locator.PickBest(vendors, price.Value, playerContext);

        var lines = quests.ToList();
        if (vendors.Count > 0)
        {
            lines.Add(price is > 0 ? $"Repurchase ({price:N0} gil):" : "Repurchase:");
            lines.AddRange(vendors.Select(v => FormatVendorLine(v.Name, v.Loc)));
        }

        return new FashionAcquireSection
        {
            Type = "quest",
            Label = "Quest Reward",
            Headline = quests.Count > 0 ? quests[0] : "Quest reward",
            Lines = lines,
        };
    }

    private static FashionAcquireSection? ParseBarter(JsonElement element)
    {
        var currency = GetString(element, "currencyName") ?? "currency";
        var amount = GetInt(element, "currencyAmount");
        var vendors = ReadVendors(element);

        // Do not treat exchange NPCs as gil vendors (that produced "NPC vendor 0 gil").
        var lines = ReadStringArray(element, "sourceDuties").Select(d => $"From: {d}").ToList();
        lines.AddRange(vendors.Select(v => FormatVendorLine(v.Name, v.Loc)));

        return new FashionAcquireSection
        {
            Type = "barter",
            Label = "Exchange",
            Headline = amount is { } a ? $"{currency} ×{a}" : currency,
            Lines = lines,
        };
    }

    private static FashionAcquireSection? ParseAchievement(JsonElement element)
    {
        var achievements = ReadStringArray(element, "achievements");
        var price = GetInt(element, "price");
        var lines = achievements.ToList();
        lines.AddRange(ReadVendors(element).Select(v => FormatVendorLine(v.Name, v.Loc)));

        return new FashionAcquireSection
        {
            Type = "achievement",
            Label = "Achievement",
            Headline = price is { } p
                ? $"Purchasable for {p:N0} gil after completion"
                : "Achievement reward",
            Lines = lines,
        };
    }

    private static string FormatGc(JsonElement element)
    {
        var amount = GetInt(element, "amount");
        var shop = GetString(element, "shop");
        if (amount is { } a && !string.IsNullOrWhiteSpace(shop))
            return $"{a:N0} seals · {shop}";
        if (amount is { } a2)
            return $"{a2:N0} seals";
        return shop ?? "Grand Company shop";
    }

    private static List<(string Name, string Loc)> ReadVendors(JsonElement element)
    {
        var list = new List<(string, string)>();
        if (!element.TryGetProperty("vendors", out var vendors) || vendors.ValueKind != JsonValueKind.Array)
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vendors.EnumerateArray())
        {
            var name = GetString(v, "name") ?? string.Empty;
            var loc = GetString(v, "loc") ?? string.Empty;
            var key = name + "\0" + loc;
            if (!seen.Add(key))
                continue;
            list.Add((name, loc));
        }

        return list;
    }

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        var list = new List<string>();
        if (!element.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }

        return list;
    }

    private static string FormatVendorLine(string name, string loc) =>
        string.IsNullOrWhiteSpace(loc) ? name : $"{name} · {loc}";

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
            return i;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
