using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Windows;

internal sealed class FashionReportPanel
{
    private const float ItemIconSize = 42f;
    private const float DyeIconSize = 33f;
    private const float EasyDyeIconSize = 27f;
    private const float ColumnGap = 8f;

    private readonly Plugin plugin;
    private readonly Dictionary<string, ushort> dyeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool ownedOnly;
    private string? expandedItemKey;

    public FashionReportPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var service = this.plugin.FashionReport;

        ImGui.Text("Weekly Fashion Report. Thanks to ");
        ImGui.SameLine(0, 0);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.75f, 1f, 1f));
        if (ImGui.Selectable(
                "FashionReportXIV.com",
                false,
                ImGuiSelectableFlags.DontClosePopups,
                new Vector2(ImGui.CalcTextSize("FashionReportXIV.com").X, 0)))
        {
            FashionReportNativeHelpers.OpenUrl("https://fashionreportxiv.com/");
        }

        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        ImGui.Text(" and its contributors.");

        if (ImGui.Button(service.IsRefreshing ? "Refreshing…" : "Refresh week"))
        {
            this.plugin.RefreshAll(false);
            _ = service.RefreshAsync(force: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Update ownership"))
            service.RebindOwnership();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-check bags, armoury, saddlebag, dresser/armoire, and nearby vendors.");

        ImGui.SameLine();
        ImGui.Checkbox("Owned pieces only", ref this.ownedOnly);

        if (service.LastFetchUtc is { } fetched)
            ImGui.TextDisabled($"Last update: {fetched.ToLocalTime():g}");

        if (!string.IsNullOrEmpty(service.LastError))
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), service.LastError);

        var snap = service.Snapshot;
        if (snap == null)
        {
            if (!service.IsRefreshing)
                ImGui.TextDisabled("No Fashion Report loaded yet. Press Refresh week.");
            return;
        }

        ImGui.Separator();
        DrawWeekHeader(snap);

        if (!string.IsNullOrWhiteSpace(snap.TheorycraftUrl) || !string.IsNullOrWhiteSpace(snap.ResultsUrl))
        {
            if (!string.IsNullOrWhiteSpace(snap.TheorycraftUrl) && ImGui.SmallButton("Open theorycraft"))
                FashionReportNativeHelpers.OpenUrl(snap.TheorycraftUrl);
            if (!string.IsNullOrWhiteSpace(snap.ResultsUrl))
            {
                if (!string.IsNullOrWhiteSpace(snap.TheorycraftUrl))
                    ImGui.SameLine();
                if (ImGui.SmallButton("Open results"))
                    FashionReportNativeHelpers.OpenUrl(snap.ResultsUrl);
            }
        }

        // Prefer fitting both rows with no outer scrollbar; only scroll when the window
        // is too short to keep the bottom row fully visible at minimum sizes.
        const float minHintHeight = 160f;
        const float minBottomHeight = 140f;
        var availY = ImGui.GetContentRegionAvail().Y;
        var minNeeded = minHintHeight + ColumnGap + minBottomHeight;

        if (availY >= minNeeded)
        {
            var bottomHeight = Math.Clamp(availY * 0.38f, minBottomHeight, availY - ColumnGap - minHintHeight);
            var hintHeight = availY - ColumnGap - bottomHeight;
            DrawHintColumns(snap, hintHeight);
            ImGui.Dummy(new Vector2(0, ColumnGap));
            DrawBottomRow(snap, bottomHeight);
        }
        else
        {
            ImGui.BeginChild("FashionReportBody", new Vector2(0, 0));
            DrawHintColumns(snap, minHintHeight);
            ImGui.Dummy(new Vector2(0, ColumnGap));
            DrawBottomRow(snap, minBottomHeight);
            ImGui.EndChild();
        }
    }

    private void DrawWeekHeader(FashionReportSnapshot snap)
    {
        const float statusScale = 1.35f;
        const string reminder = "Remember to use MGP bonus buffs";
        var progress = this.plugin.FashionProgress.GetProgress();
        var (statusColor, statusLabel, _) = FashionReportNativeHelpers.FormatProgress(progress);

        var lineY = ImGui.GetCursorPosY();
        ImGui.Text($"Week {snap.Week} — {snap.Title}");
        ImGui.SameLine(0, 0);

        var reminderSize = ImGui.CalcTextSize(reminder);
        ImGui.SetWindowFontScale(statusScale);
        var statusSize = ImGui.CalcTextSize(statusLabel);
        ImGui.SetWindowFontScale(1f);

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var groupWidth = reminderSize.X + gap + statusSize.X;
        var cursorX = ImGui.GetCursorPosX();
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(cursorX + Math.Max(8f, avail - groupWidth));

        // Keep reminder at normal size, vertically centered against the larger status text.
        ImGui.SetCursorPosY(lineY + Math.Max(0f, (statusSize.Y - reminderSize.Y) * 0.5f));
        ImGui.TextColored(FashionReportNativeHelpers.ColorMgpReminder, reminder);

        ImGui.SameLine(0, gap);
        ImGui.SetCursorPosY(lineY);
        ImGui.SetWindowFontScale(statusScale);
        ImGui.TextColored(statusColor, statusLabel);
        ImGui.SetWindowFontScale(1f);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FashionReportNativeHelpers.ProgressTooltip(progress));
    }

    private void DrawHintColumns(FashionReportSnapshot snap, float height)
    {
        if (snap.Hints.Count == 0)
        {
            ImGui.TextDisabled("No hint slots for this week.");
            return;
        }

        var width = ImGui.GetContentRegionAvail().X;
        var cols = Math.Clamp(snap.Hints.Count, 1, 4);
        var colWidth = Math.Max(180f, (width - (cols - 1) * ColumnGap) / cols);

        for (var i = 0; i < snap.Hints.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, ColumnGap);

            var hint = snap.Hints[i];
            ImGui.BeginChild($"hintcol-{hint.SlotKey}", new Vector2(colWidth, height), true);

            ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.45f, 1f), hint.SlotLabel);
            ImGui.TextWrapped(hint.Hint);
            ImGui.TextDisabled($"{hint.OwnedCount} owned");
            if (!string.IsNullOrWhiteSpace(hint.RingNote))
                ImGui.TextDisabled($"Ring: {hint.RingNote}");

            ImGui.Separator();

            var shown = 0;
            foreach (var item in hint.Items)
            {
                if (this.ownedOnly && !item.Owned)
                    continue;
                shown++;
                DrawItemCard(item, hint.SlotKey);
            }

            if (shown == 0)
                ImGui.TextDisabled(this.ownedOnly ? "No owned pieces." : "No items listed.");

            ImGui.EndChild();
        }
    }

    private void DrawBottomRow(FashionReportSnapshot snap, float height)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var colWidth = Math.Max(160f, (width - 16f) / 3f);

        DrawDyeColumn(snap, colWidth, height);
        ImGui.SameLine(0, ColumnGap);
        DrawEasyColumn(snap.Easy100, colWidth, height);
        ImGui.SameLine(0, ColumnGap);
        DrawEasyColumn(snap.Easy80, colWidth, height);
    }

    private void DrawDyeColumn(FashionReportSnapshot snap, float width, float height)
    {
        ImGui.BeginChild("dye-col", new Vector2(width, height), true);
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Dyes");
        ImGui.Separator();

        if (snap.Dyes.Count == 0)
        {
            ImGui.TextDisabled(snap.DyesFresh
                ? "No dye data."
                : "Dyes are not available yet (usually Friday).");
        }
        else
        {
            foreach (var dye in snap.Dyes)
            {
                var exact = string.IsNullOrWhiteSpace(dye.ExactDye) ? "—" : dye.ExactDye;
                var family = string.IsNullOrWhiteSpace(dye.ColorFamily) ? "—" : dye.ColorFamily;
                DrawItemIcon(ResolveDyeIcon(exact), DyeIconSize);
                ImGui.SameLine();
                ImGui.TextWrapped($"{dye.SlotLabel}: {exact} ({family})");
            }
        }

        ImGui.EndChild();
    }

    private void DrawEasyColumn(FashionEasyOutfitView? easy, float width, float height)
    {
        ImGui.PushID(easy?.Title ?? "easy-missing");
        ImGui.BeginChild("easy-col", new Vector2(width, height), true);
        var title = easy?.Title ?? "Easy";
        ImGui.TextColored(new Vector4(0.7f, 1f, 0.75f, 1f), title);
        ImGui.Separator();

        if (easy == null)
        {
            ImGui.TextDisabled("Not available.");
            ImGui.EndChild();
            ImGui.PopID();
            return;
        }

        if (!easy.Fresh)
        {
            ImGui.TextDisabled("Not ready until dyes are confirmed.");
            ImGui.EndChild();
            ImGui.PopID();
            return;
        }

        foreach (var item in easy.Items)
            DrawItemCard(item, "easy-" + easy.Title);

        if (easy.Dyes.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Dyes:");
            foreach (var (slot, dye) in easy.Dyes)
            {
                DrawItemIcon(ResolveDyeIcon(dye), EasyDyeIconSize);
                ImGui.SameLine();
                ImGui.Text($"{slot}: {dye}");
            }
        }

        ImGui.EndChild();
        ImGui.PopID();
    }

    private void DrawItemCard(FashionResolvedItem item, string scope)
    {
        var key = scope + "|" + item.Name;
        ImGui.PushID(key);

        DrawItemIcon(item.IconId, ItemIconSize);
        ImGui.SameLine();

        var flags = this.expandedItemKey == key ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        var open = ImGui.TreeNodeEx(item.Name + $"###tree-{key}", flags);
        DrawStatusBadge(item);

        if (open)
        {
            this.expandedItemKey = key;
            ImGui.TextWrapped(item.Summary);

            DrawAutocraftControls(item);

            foreach (var section in item.Sections)
            {
                ImGui.Spacing();
                ImGui.TextColored(FashionReportNativeHelpers.TagColor(section.Type), section.Label);
                if (!string.IsNullOrWhiteSpace(section.Headline))
                    ImGui.TextWrapped(section.Headline);

                var preferred = item.PreferredVendor;
                var lines = section.Lines;
                if (section.Type.Equals("vendor", StringComparison.OrdinalIgnoreCase) && preferred != null)
                {
                    ImGui.BulletText($"{preferred.Name} · {preferred.Location}" + (preferred.SameArea ? "  ← nearby" : string.Empty));
                    var extras = 0;
                    foreach (var line in lines)
                    {
                        if (line.Contains(preferred.Location, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (extras >= 8)
                            break;
                        ImGui.BulletText(line);
                        extras++;
                    }

                    if (lines.Count > extras + 1)
                        ImGui.TextDisabled($"+{lines.Count - extras - 1} more vendors…");
                }
                else if (section.Type.Equals("craft", StringComparison.OrdinalIgnoreCase) && section.Ingredients.Count > 0)
                {
                    foreach (var ing in section.Ingredients)
                    {
                        ImGui.TextColored(
                            ing.HasEnough
                                ? FashionReportNativeHelpers.ColorOwned
                                : FashionReportNativeHelpers.ColorMatsMissing,
                            FashionReportNativeHelpers.FormatIngredientLine(ing));
                    }
                }
                else
                {
                    var shown = 0;
                    foreach (var line in lines)
                    {
                        if (shown >= 12)
                            break;
                        ImGui.BulletText(line);
                        shown++;
                    }

                    if (lines.Count > 12)
                        ImGui.TextDisabled($"+{lines.Count - 12} more…");
                }
            }

            if (item.Sections.Count == 0)
                ImGui.TextDisabled("No acquisition details available.");

            if (!string.IsNullOrWhiteSpace(item.GarlandUrl) || !string.IsNullOrWhiteSpace(item.LodestoneUrl))
            {
                ImGui.Spacing();
                if (!string.IsNullOrWhiteSpace(item.GarlandUrl) && ImGui.SmallButton("Garland Tools"))
                    FashionReportNativeHelpers.OpenUrl(item.GarlandUrl!);
                if (!string.IsNullOrWhiteSpace(item.LodestoneUrl))
                {
                    if (!string.IsNullOrWhiteSpace(item.GarlandUrl))
                        ImGui.SameLine();
                    if (ImGui.SmallButton("Lodestone"))
                        FashionReportNativeHelpers.OpenUrl(item.LodestoneUrl!);
                }
            }

            ImGui.TreePop();
        }
        else if (this.expandedItemKey == key)
        {
            this.expandedItemKey = null;
        }

        ImGui.PopID();
        ImGui.Spacing();
    }

    private static void DrawStatusBadge(FashionResolvedItem item)
    {
        if (item.Owned)
        {
            ImGui.SameLine();
            ImGui.TextColored(FashionReportNativeHelpers.ColorOwned, "owned");
            return;
        }

        if (item.CraftMatsTotal == 0)
            return;

        ImGui.SameLine();
        ImGui.TextColored(
            item.CraftMatsReady == item.CraftMatsTotal
                ? FashionReportNativeHelpers.ColorMatsReady
                : FashionReportNativeHelpers.ColorMatsMissing,
            $"Materials {item.CraftMatsReady}/{item.CraftMatsTotal}");
    }

    private void DrawAutocraftControls(FashionResolvedItem item)
    {
        if (!item.HasCraftRecipe || item.Owned || item.ItemId == 0)
            return;

        var artisan = this.plugin.ArtisanIpc;
        if (!artisan.IsAvailable)
            return;

        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.55f, 0.48f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.68f, 0.58f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.42f, 0.36f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 1f, 0.98f, 1f));

        var clicked = ImGui.Button("Autocraft with Artisan");
        ImGui.PopStyleColor(4);

        if (clicked)
        {
            if (!this.plugin.RecipeLookup.TryGetRecipeId(item.ItemId, out var recipeId))
            {
                Plugin.ChatGui.PrintError($"[Glamour Tracker+] No craft recipe found for {item.Name}.");
            }
            else if (artisan.TryCraftItem(recipeId, 1, out var message))
            {
                Plugin.ChatGui.Print($"[Glamour Tracker+] {message}");
            }
            else
            {
                Plugin.ChatGui.PrintError($"[Glamour Tracker+] {message}");
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Starts this recipe in Artisan (×1).\n"
                + "Materials must already be in your bags.");
        }
    }

    private void DrawItemIcon(ushort iconId, float size)
    {
        if (iconId == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        try
        {
            var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        }
        catch
        {
            ImGui.Dummy(new Vector2(size, size));
        }
    }

    private ushort ResolveDyeIcon(string? dyeName)
    {
        if (string.IsNullOrWhiteSpace(dyeName) || dyeName == "—")
            return 0;

        if (this.dyeIconCache.TryGetValue(dyeName, out var cached))
            return cached;

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var withDyeSuffix = dyeName.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase)
            ? dyeName
            : dyeName + " Dye";

        ushort icon = 0;
        foreach (var item in sheet)
        {
            if (item.RowId == 0)
                continue;
            var name = item.Name.ExtractText();
            if (string.Equals(name, dyeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, withDyeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                icon = item.Icon;
                break;
            }
        }

        this.dyeIconCache[dyeName] = icon;
        return icon;
    }
}
