using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.Nodes;

using static GlamourTracker.Windows.TrackerNativeNodeFactory;

namespace GlamourTracker.Windows;

internal sealed partial class TrackerNativeAddon
{
    private void BuildOutfitDetail(VerticalListNode list, OutfitSetInfo set, float width)
    {
        var (storedPieces, missingPieces, total) = TrackerNativeHelpers.SplitPiecesForFilter(
            set,
            storageFilter,
            IsGlamourPiece,
            plugin.CabinetCatalog.IsArmoireEligible);

        list.AddNode(MakeText(set.Name, 16, TrackerNativeHelpers.ColorTitle, width, 22f));

        if (total == 0)
        {
            list.AddNode(MakeMuted(
                storageFilter == OutfitStorageFilter.Dresser
                    ? "No dresser pieces in this set."
                    : storageFilter == OutfitStorageFilter.Armoire
                        ? "No armoire pieces in this set."
                        : "No pieces in this set.",
                width));
            return;
        }

        list.AddNode(MakeMuted("Expand a piece for sources. Try on shows a preview.", width));

        list.AddNode(MakeText(
            $"{storedPieces.Count}/{total} stored",
            13,
            TrackerNativeHelpers.GetSetStatusColor(storedPieces.Count, missingPieces.Count),
            width,
            18f));

        foreach (var piece in storedPieces)
            AddOutfitPieceRow(list, set, piece, width);

        if (missingPieces.Count > 0)
        {
            list.AddNode(MakeText(
                $"{missingPieces.Count}/{total} missing",
                13,
                TrackerNativeHelpers.ColorMissing,
                width,
                18f));

            foreach (var piece in missingPieces)
                AddOutfitPieceRow(list, set, piece, width);
        }
    }

    private void AddOutfitPieceRow(VerticalListNode list, OutfitSetInfo set, OutfitPieceInfo piece, float width)
    {
        var name = TrackerNativeHelpers.ResolveItemName(piece.ItemId);
        var status = TrackerNativeHelpers.FormatStorage(piece.Storage);
        var pieceKey = PieceKey(set.SetId, piece);
        var expanded = expandedPieceKeys.Contains(pieceKey);
        var iconId = TrackerNativeHelpers.ResolveItemIcon(piece.ItemId);

        const float iconSize = 28f;
        const float iconGap = 4f;
        var headerWidth = iconId != 0
            ? MathF.Max(120f, width - iconSize - iconGap)
            : width;
        var contentWidth = MathF.Max(80f, headerWidth - 8f);

        var row = new HorizontalListNode
        {
            Size = new Vector2(width, iconSize),
            ItemSpacing = iconGap,
            FitToContentHeight = true,
        };

        if (iconId != 0)
        {
            var pieceIcon = new IconImageNode
            {
                Size = new Vector2(iconSize, iconSize),
                TextureSize = new Vector2(iconSize, iconSize),
                IconId = iconId,
                ImageNodeFlags = ImageNodeFlags.AutoFit,
            };
            // Native item detail tooltip (same as inventory hover).
            if (piece.ItemId != 0)
                pieceIcon.ItemTooltip = piece.ItemId;
            row.AddNode(pieceIcon);
        }

        var header = new CollapsingHeaderNode
        {
            Size = new Vector2(headerWidth, 28f),
            String = $"{piece.SlotLabel}: {name} — {status}",
            FitWidth = true,
            IsCollapsed = !expanded,
            ItemSpacing = 3f,
        };

        var tryOn = new TextButtonNode
        {
            Size = new Vector2(MathF.Min(120f, contentWidth), RowH),
            String = "Try on",
            OnClick = () => TryOnItem(piece.ItemId, name),
        };
        header.AddNode(tryOn);

        if (itemAcquireCache.TryGetValue(piece.ItemId, out var acquired))
        {
            if (!string.IsNullOrWhiteSpace(acquired.Summary))
                header.AddNode(MakeMuted(acquired.Summary, contentWidth));

            foreach (var costLine in EnumerateAcquireCosts(acquired))
            {
                header.AddNode(MakeText(
                    costLine,
                    12,
                    TrackerNativeHelpers.ColorInfo,
                    contentWidth,
                    16f));
            }

            foreach (var section in acquired.Sections)
                AddAcquireSection(header, section, acquired, contentWidth);
            if (acquired.Sections.Count == 0 && string.IsNullOrWhiteSpace(acquired.Summary))
                header.AddNode(MakeMuted("No source data for this piece.", contentWidth));
        }
        else if (setAcquireRetryAfter.ContainsKey(set.SetId))
        {
            header.AddNode(MakeMuted("Couldn't load sources. Reopen this set to try again.", contentWidth));
        }
        else if (!setAcquireLoaded.ContainsKey(set.SetId))
        {
            header.AddNode(MakeMuted("Loading sources…", contentWidth));
        }
        else
        {
            header.AddNode(MakeMuted("No source data for this piece.", contentWidth));
        }

        // Track expand state only — never rebuild the tree from OnToggle (that flickers the headers).
        header.OnToggle = visible =>
        {
            if (visible)
                expandedPieceKeys.Add(pieceKey);
            else
                expandedPieceKeys.Remove(pieceKey);

            RelayoutBrowserDetail();

            if (visible && NeedsAcquireLoad(set.SetId))
                _ = LoadSetAcquireAsync(set, refreshUi: true, WindowToken);
        };

        row.AddNode(header);
        list.AddNode(row);
    }

    /// <summary>Distinct buy/exchange cost lines (gil, tomestones, seals, etc.).</summary>
    private static IEnumerable<string> EnumerateAcquireCosts(FashionResolvedItem item)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in item.Sections)
        {
            if (!IsCostSection(section.Type) || string.IsNullOrWhiteSpace(section.Headline))
                continue;

            var line = section.Headline!;
            // Avoid duplicating the same cost already present in the summary line.
            if (!string.IsNullOrWhiteSpace(item.Summary)
                && item.Summary.Contains(line, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen.Add(line))
                yield return line;
        }

        if (item.PreferredVendor is { Gil: > 0 } vendor)
        {
            var gilLine = $"Cost: {vendor.Gil:N0} gil";
            if ((string.IsNullOrWhiteSpace(item.Summary)
                 || !item.Summary.Contains(gilLine, StringComparison.OrdinalIgnoreCase))
                && seen.Add(gilLine))
            {
                yield return gilLine;
            }
        }
    }

    private static bool IsCostSection(string type) =>
        type.Equals("vendor", StringComparison.OrdinalIgnoreCase)
        || type.Equals("barter", StringComparison.OrdinalIgnoreCase)
        || type.Equals("gc", StringComparison.OrdinalIgnoreCase);

    private void RelayoutBrowserDetail()
    {
        if (browserDetail == null)
            return;
        browserDetail.ContentNode.RecalculateLayout();
        browserDetail.RecalculateSizes();
    }

    private static string PieceKey(uint setId, OutfitPieceInfo piece) =>
        $"{setId}|{piece.SlotIndex}|{piece.ItemId}";

    private void TryOnItem(uint itemId, string name)
    {
        if (itemId == 0)
            return;

        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (!AgentTryon.TryOn(0, itemId))
                    Plugin.ChatGui.PrintError($"[Glamour Tracker+] Could not try on {name}.");
            }
            catch (Exception ex)
            {
                PluginFileLog.Error("outfit.tryon", $"Try on failed for {name} ({itemId})", ex);
                Plugin.ChatGui.PrintError($"[Glamour Tracker+] Could not try on {name}.");
            }
        });
    }

    private void AddAcquireSection(
        LayoutListNode list,
        FashionAcquireSection section,
        FashionResolvedItem item,
        float width)
    {
        list.AddNode(MakeText(
            section.Label,
            12,
            FashionReportNativeHelpers.TagColor(section.Type),
            width,
            16f));

        // Cost headlines are shown above via EnumerateAcquireCosts; skip repeating them here.
        if (!string.IsNullOrWhiteSpace(section.Headline)
            && !IsCostSection(section.Type)
            && !FashionReportNativeHelpers.IsRedundantSummary(section.Headline, item))
        {
            list.AddNode(MakeMuted(section.Headline!, width));
        }

        if (section.Type.Equals("duty_drop", StringComparison.OrdinalIgnoreCase))
        {
            var duties = new List<string>();
            if (!string.IsNullOrWhiteSpace(section.Headline))
                duties.Add(section.Headline!);
            duties.AddRange(section.Lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            foreach (var duty in duties.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dutyName = duty;
                list.AddNode(new TextButtonNode
                {
                    Size = new Vector2(MathF.Min(width, 260f), RowH),
                    String = $"Travel to {Truncate(dutyName, 28)}",
                    TextTooltip = dutyName,
                    OnClick = () => _ = Plugin.Framework.RunOnFrameworkThread(() =>
                        OutfitDutyTravel.TryOpenDuty(dutyName, Plugin.DataManager, Plugin.ChatGui)),
                });
            }

            return;
        }

        if (section.Type.Equals("craft", StringComparison.OrdinalIgnoreCase) && item.ItemId != 0)
        {
            list.AddNode(new TextButtonNode
            {
                Size = new Vector2(MathF.Min(width, 200f), RowH),
                String = "Open Crafting Log",
                OnClick = () => TryOpenCraftingLog(item.ItemId, item.Name),
            });
        }

        foreach (var line in section.Lines)
        {
            if (FashionReportNativeHelpers.LineDuplicatesHeadline(line, section.Headline))
                continue;

            if (FashionReportNativeHelpers.HasMapCoordinates(line))
            {
                var target = line;
                list.AddNode(MakeVendorRow(Truncate(line, 42), target, width));
            }
            else
            {
                list.AddNode(MakeMuted(line, width));
            }
        }
    }

    private ResNode MakeVendorRow(string label, string teleportTarget, float width)
    {
        const float buttonW = 96f;
        var row = new ResNode { Size = new Vector2(width, RowH) };
        var text = MakeText(label, 12, Vector4.One, Math.Max(40f, width - buttonW - 8f), 18f);
        text.Position = new Vector2(0f, 5f);
        text.AttachNode(row);
        var teleport = new TextButtonNode
        {
            Position = new Vector2(width - buttonW, 0f),
            Size = new Vector2(buttonW, RowH),
            String = "Teleport",
            OnClick = () => plugin.VendorTravel.TeleportNearLocation(teleportTarget),
        };
        teleport.AttachNode(row);
        return row;
    }

    private void TryOpenCraftingLog(uint itemId, string name)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                try
                {
                    if (!plugin.RecipeLookup.TryGetRecipeId(itemId, out var recipeId))
                    {
                        Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {name}.");
                        return;
                    }

                    var agent = AgentRecipeNote.Instance();
                    if (agent == null)
                    {
                        Plugin.ChatGui.PrintError("[Glamour Tracker+] Crafting Log is not available.");
                        return;
                    }

                    agent->OpenRecipeByRecipeId(recipeId);
                }
                catch (Exception ex)
                {
                    PluginFileLog.Error("outfit.craft", $"Open Crafting Log failed for {name}", ex);
                    Plugin.ChatGui.PrintError("[Glamour Tracker+] Could not open the Crafting Log.");
                }
            }
        });
    }

}
