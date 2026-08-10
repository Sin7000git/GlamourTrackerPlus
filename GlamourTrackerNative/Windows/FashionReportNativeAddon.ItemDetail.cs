using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

using static GlamourTracker.Windows.FashionReportNativeNodeFactory;

namespace GlamourTracker.Windows;

internal sealed partial class FashionReportNativeAddon
{
    private void RebuildDetail(FashionReportNativeRow row)
    {
        if (detailScroll == null)
            return;

        if (row.Key == lastDetailKey && row.Kind != FashionReportNativeRowKind.Item)
            return;

        // Item rows refresh when ownership/materials change even if key matches.
        var mats = row.Item is { } selected
            ? FashionReportNativeHelpers.MaterialsBadge(selected)
            : string.Empty;
        var detailKey = row.Kind == FashionReportNativeRowKind.Item
            ? $"{row.Key}|{row.Badge}|{mats}|{row.Subtitle}"
            : row.Key;
        if (detailKey == lastDetailKey)
            return;
        lastDetailKey = detailKey;

        var list = detailScroll.ContentNode;
        list.Clear();
        autocraftButton = null;
        craftingLogButton = null;
        garlandButton = null;
        lodestoneButton = null;

        var width = detailScroll.Width - 18f;

        list.AddNode(MakeText(row.Title, 16, FashionReportNativeHelpers.ColorSlot, width, 22f));
        if (!string.IsNullOrEmpty(row.Badge))
            list.AddNode(MakeText(row.Badge, 13, row.BadgeColor, width, 18f));
        if (row.Item is { } badgeItem)
        {
            var materials = FashionReportNativeHelpers.MaterialsBadge(badgeItem);
            if (!string.IsNullOrEmpty(materials))
            {
                list.AddNode(MakeText(
                    materials,
                    13,
                    FashionReportNativeHelpers.MaterialsBadgeColor(badgeItem),
                    width,
                    18f));
            }
        }

        if (!string.IsNullOrEmpty(row.Subtitle)
            && (row.Item is null || !FashionReportNativeHelpers.IsRedundantSummary(row.Subtitle, row.Item)))
        {
            list.AddNode(MakeWrappedText(row.Subtitle, 12, FashionReportNativeHelpers.ColorMuted, width));
        }

        if (row.Kind == FashionReportNativeRowKind.Item && row.Item is { } item)
            AddItemDetail(list, item, width);
        else if (row.Kind == FashionReportNativeRowKind.Dye)
            list.AddNode(MakeWrappedText("Exact dye for this slot (plus family).", 12, FashionReportNativeHelpers.ColorMuted, width));

        list.RecalculateLayout();
        detailScroll.RecalculateSizes();
        detailScroll.ScrollToTop();
    }

    private void ClearDetail(string message)
    {
        if (detailScroll == null)
            return;
        lastDetailKey = "clear:" + message;
        var list = detailScroll.ContentNode;
        list.Clear();
        autocraftButton = null;
        craftingLogButton = null;
        garlandButton = null;
        lodestoneButton = null;
        list.AddNode(MakeWrappedText(message, 13, FashionReportNativeHelpers.ColorMuted, detailScroll.Width - 18f));
        list.RecalculateLayout();
        detailScroll.RecalculateSizes();
    }

    private void AddItemDetail(VerticalListNode list, FashionResolvedItem item, float width)
    {
        var canCraft = item.HasCraftRecipe && item.ItemId != 0
                       && plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out _);

        if (canCraft && !item.Owned && plugin.ArtisanIpc.IsAvailable)
        {
            autocraftButton = new TextButtonNode
            {
                Size = new Vector2(Math.Min(220f, width), 28f),
                String = "Autocraft with Artisan",
                TextTooltip = "Starts this recipe in Artisan (×1).\nMaterials must already be in your bags.",
                OnClick = () => TryAutocraft(item),
            };
            list.AddNode(autocraftButton);
        }

        if (canCraft)
        {
            craftingLogButton = new TextButtonNode
            {
                Size = new Vector2(Math.Min(220f, width), 28f),
                String = "Open Crafting Log",
                TextTooltip = "Opens this recipe in the in-game Crafting Log.",
                OnClick = () => TryOpenCraftingLog(item),
            };
            list.AddNode(craftingLogButton);
        }

        foreach (var section in item.Sections)
        {
            list.AddNode(MakeText(section.Label, 13, FashionReportNativeHelpers.TagColor(section.Type), width, 18f));
            if (!string.IsNullOrWhiteSpace(section.Headline))
                list.AddNode(MakeWrappedText(section.Headline, 12, FashionReportNativeHelpers.ColorMuted, width));

            if (section.Type.Equals("craft", StringComparison.OrdinalIgnoreCase) && section.Ingredients.Count > 0)
            {
                foreach (var ing in section.Ingredients)
                {
                    list.AddNode(MakeText(
                        FashionReportNativeHelpers.FormatIngredientLine(ing),
                        12,
                        ing.HasEnough
                            ? FashionReportNativeHelpers.ColorOwned
                            : FashionReportNativeHelpers.ColorMatsMissing,
                        width,
                        16f));
                }

                continue;
            }

            foreach (var line in section.Lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (FashionReportNativeHelpers.LineDuplicatesHeadline(line, section.Headline))
                    continue;

                var preferred = item.PreferredVendor;
                var isPreferred = preferred != null
                    && !string.IsNullOrWhiteSpace(preferred.Location)
                    && (line.Contains(preferred.Location, StringComparison.OrdinalIgnoreCase)
                        || line.Contains(preferred.Name, StringComparison.OrdinalIgnoreCase));
                var label = isPreferred && preferred!.SameArea
                    ? $"• {line}  ← nearby"
                    : $"• {line}";

                // Any NPC/location line with coordinates gets Teleport (vendors, exchange, recompense, etc.).
                if (FashionReportNativeHelpers.HasMapCoordinates(line))
                    list.AddNode(MakeVendorRow(label, line, width));
                else
                    list.AddNode(MakeWrappedText(label, 12, FashionReportNativeHelpers.ColorMuted, width));
            }
        }

        if (item.Sections.Count == 0)
            list.AddNode(MakeText("No acquisition details available.", 12, FashionReportNativeHelpers.ColorMuted, width, 16f));

        if (!string.IsNullOrWhiteSpace(item.GarlandUrl) || !string.IsNullOrWhiteSpace(item.LodestoneUrl))
        {
            var buttons = new HorizontalListNode
            {
                Size = new Vector2(width, 28f),
                ItemSpacing = 8f,
            };

            if (!string.IsNullOrWhiteSpace(item.GarlandUrl))
            {
                garlandButton = new TextButtonNode
                {
                    Size = new Vector2(120f, 28f),
                    String = "Garland Tools",
                    OnClick = () => FashionReportNativeHelpers.OpenUrl(item.GarlandUrl!),
                };
                buttons.AddNode(garlandButton);
            }

            if (!string.IsNullOrWhiteSpace(item.LodestoneUrl))
            {
                lodestoneButton = new TextButtonNode
                {
                    Size = new Vector2(100f, 28f),
                    String = "Lodestone",
                    OnClick = () => FashionReportNativeHelpers.OpenUrl(item.LodestoneUrl!),
                };
                buttons.AddNode(lodestoneButton);
            }

            list.AddNode(buttons);
        }
    }

    private ResNode MakeVendorRow(string label, string teleportTarget, float width)
    {
        const float buttonW = 96f;
        var row = new ResNode
        {
            Size = new Vector2(width, 28f),
        };

        var text = new TextNode
        {
            Position = new Vector2(0f, 5f),
            Size = new Vector2(Math.Max(40f, width - buttonW - 8f), 18f),
            FontSize = 12,
            TextColor = Vector4.One,
            String = (ReadOnlySeString)label,
            TextFlags = TextFlags.Ellipsis,
        };
        text.AttachNode(row);

        var teleport = new TextButtonNode
        {
            Position = new Vector2(width - buttonW, 0f),
            Size = new Vector2(buttonW, 28f),
            String = "Teleport",
            TextTooltip = "Teleport to the nearest aetheryte and flag this vendor on the map.",
            OnClick = () => plugin.VendorTravel.TeleportNearLocation(teleportTarget),
        };
        teleport.AttachNode(row);
        return row;
    }

    private void TryAutocraft(FashionResolvedItem item)
    {
        if (!plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out var recipeId))
        {
            Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {item.Name}.");
            return;
        }

        if (plugin.ArtisanIpc.TryCraftItem(recipeId, 1, out var message))
            Plugin.ChatGui.Print($"[Glamour Tracker+] {message}");
        else
            Plugin.ChatGui.PrintError($"[Glamour Tracker+] {message}");
    }

    private void TryOpenCraftingLog(FashionResolvedItem item)
    {
        if (!plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out var recipeId))
        {
            Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {item.Name}.");
            return;
        }

        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                unsafe
                {
                    var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRecipeNote.Instance();
                    if (agent == null)
                    {
                        Plugin.ChatGui.PrintError("[Glamour Tracker+] Crafting Log is unavailable right now.");
                        return;
                    }

                    agent->OpenRecipeByRecipeId(recipeId);
                }
            }
            catch (Exception ex)
            {
                PluginFileLog.Error("fashion.native", $"Open Crafting Log failed for {item.Name}", ex);
                Plugin.ChatGui.PrintError("[Glamour Tracker+] Could not open the Crafting Log.");
            }
        });
    }
}
