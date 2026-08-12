using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GlamourTracker.Services;
using GlamourTracker.Windows.Native;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Windows;

internal sealed class TrackerWindow : Window
{
    private readonly Plugin plugin;
    private readonly FashionReportPanel fashionReportPanel;
    private string outfitFilter = string.Empty;
    private bool showMissingOnly;
    private int localStyleVarsPushed;
    private int localStyleColorsPushed;
    private int savedDataConfirm; // 0 none, 1 character, 2 all

    public TrackerWindow(Plugin plugin)
        : base("Glamour Tracker+###GlamourTrackerNativeMain", ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        this.fashionReportPanel = new FashionReportPanel(plugin);
        this.Size = new Vector2(920, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        this.localStyleVarsPushed = 0;
        this.localStyleColorsPushed = 0;
        var config = this.plugin.Configuration;
        if (!config.UsePlateOverlayLocalUiStyle)
            return;

        config.PlateOverlayLocalUiTheme ??= PluginLocalUiTheme.CreateDefault();
        (this.localStyleVarsPushed, this.localStyleColorsPushed) = config.PlateOverlayLocalUiTheme.Push();
    }

    public override void PostDraw()
    {
        PluginLocalUiTheme.Pop(this.localStyleVarsPushed, this.localStyleColorsPushed);
        this.localStyleVarsPushed = 0;
        this.localStyleColorsPushed = 0;
    }

    public override void Draw()
    {
        // New ID clears any persisted ImGui tab order from earlier layouts.
        if (ImGui.BeginTabBar("GlamourTrackerTabs_v2"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                DrawOverview();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Outfit sets"))
            {
                DrawOutfitSets();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Glamour plates"))
            {
                DrawGlamourPlates();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Fashion Report"))
            {
                this.fashionReportPanel.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Randomize"))
            {
                DrawRandomize();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawOverview()
    {
        var index = this.plugin.OwnershipIndex;

        var plateCount = 0;
        if (this.plugin.Configuration.CharacterCaches.TryGetValue(Plugin.GetLocalContentId(), out var cache))
            plateCount = cache.GlamourPlates.Count;
        if (plateCount > 0)
            ImGui.Text($"Saved glamour plates: {plateCount}");

        ImGui.Separator();
        var persisted = index.HasPersistedData ? "saved" : "not saved yet";
        ImGui.Text($"Dresser slots used: {index.DresserSlotsUsed} / 800 ({persisted})");
        ImGui.Text($"Unique stored appearances: {index.DresserUniqueCount}");
        ImGui.Text($"Outfit sets in dresser: {index.OutfitSetsInDresser}");
        ImGui.Text($"Outfit sets in armoire: {this.plugin.OutfitSets.CountSetsInArmoire()}");
        ImGui.Text($"Armoire pieces: {index.ArmoireCount}");
        ImGui.TextDisabled($"Last refresh: {index.LastRefresh.ToLocalTime():T}");

        ImGui.Separator();
        ImGui.TextDisabled(
            "Clear character data removes only the character you are logged in as. Clear all data removes every character.");

        if (this.savedDataConfirm == 1)
        {
            ImGui.TextWrapped("Clear saved data for this character only?");
            if (ImGui.Button("Yes, clear character"))
            {
                this.savedDataConfirm = 0;
                this.plugin.ForgetCurrentCharacterData();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##clearChar"))
                this.savedDataConfirm = 0;
        }
        else if (this.savedDataConfirm == 2)
        {
            ImGui.TextWrapped("Clear saved data for every character on this account? This cannot be undone.");
            if (ImGui.Button("Yes, clear all"))
            {
                this.savedDataConfirm = 0;
                this.plugin.ClearSavedOwnership();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##clearAll"))
                this.savedDataConfirm = 0;
        }
        else
        {
            if (ImGui.Button("Clear character data"))
                this.savedDataConfirm = 1;
            ImGui.SameLine();
            if (ImGui.Button("Clear all data"))
                this.savedDataConfirm = 2;
        }
    }

    private void DrawOutfitSets()
    {
        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##outfitFilter", "Filter by name…", ref this.outfitFilter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Missing pieces only", ref this.showMissingOnly);

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var sets = this.plugin.OutfitSets.GetSets();
        var shown = 0;

        ImGui.BeginChild("OutfitSetList", new Vector2(0, -4));
        foreach (var set in sets)
        {
            if (!string.IsNullOrWhiteSpace(this.outfitFilter)
                && !set.Name.Contains(this.outfitFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (this.showMissingOnly && set.MissingPieces == 0)
                continue;

            shown++;
            var unlockLabel = set.IsUnlocked ? "Unlocked" : "Not acquired";
            var progress = set.TotalPieces == 0 ? 0f : set.OwnedPieceCount / (float)set.TotalPieces;

            var storageLabel = FormatSetStorage(set.SetStorage);
            ImGui.PushID((int)set.SetId);

            var header = $"{set.Name} ({set.OwnedPieceCount}/{set.TotalPieces})";
            var open = ImGui.CollapsingHeader(header);
            if (!string.IsNullOrEmpty(storageLabel))
            {
                var labelWidth = ImGui.CalcTextSize(storageLabel).X;
                ImGui.SameLine(ImGui.GetContentRegionMax().X - labelWidth);
                ImGui.TextColored(GetSetStorageLabelColor(set), storageLabel);
            }

            if (open)
            {
                ImGui.TextDisabled(unlockLabel);
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{set.OwnedPieceCount}/{set.TotalPieces} stored");

                foreach (var piece in set.Pieces)
                {
                    var item = itemSheet.GetRow(piece.ItemId);
                    var name = string.IsNullOrWhiteSpace(item.Name.ExtractText())
                        ? $"Item #{piece.ItemId}"
                        : item.Name.ExtractText();
                    var status = TrackerNativeHelpers.FormatStorage(piece.Storage);
                    var color = status == "Missing"
                        ? new Vector4(1f, 0.45f, 0.45f, 1f)
                        : new Vector4(0.55f, 1f, 0.65f, 1f);

                    ImGui.TextColored(color, $"{piece.SlotLabel}: {name} — {status}");
                }
            }

            ImGui.PopID();
        }

        if (shown == 0)
            ImGui.TextDisabled("No outfit sets match your filter.");

        ImGui.EndChild();
    }

    private void DrawRandomize()
    {
        var config = this.plugin.Configuration;
        GlamourPlateRandomizer.EnsureLockArray(config);

        var includeDresser = config.RandomizeIncludeDresser;
        var includeArmoire = config.RandomizeIncludeArmoire;
        var changed = false;

        changed |= ImGui.Checkbox("Use dresser items", ref includeDresser);
        changed |= ImGui.Checkbox("Use armoire items", ref includeArmoire);

        ImGui.Separator();
        changed |= RandomizeFilterUi.Draw(config, Plugin.DataManager, Plugin.ObjectTable, "main");

        ImGui.Separator();
        changed |= RandomizeSlotLockUi.Draw(config, "main");

        var editorOpen = this.plugin.PlateRandomizer.IsPlateEditorOpen();
        var busy = this.plugin.PlateRandomizer.IsBusy;

        ImGui.Spacing();
        for (var i = 0; i < GlamourPlateSlotMap.SlotCount; i++)
        {
            if (i % 4 != 0)
                ImGui.SameLine(0, 8);

            ImGui.BeginDisabled(!editorOpen || busy || (!includeDresser && !includeArmoire));
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                if (ImGui.SmallButton($"{FontAwesomeIcon.Sync.ToIconString()} {GlamourPlateSlotMap.Labels[i]}##reroll{i}"))
                {
                    var slot = i;
                    _ = Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        var result = this.plugin.PlateRandomizer.BeginRandomizeSlot(slot, r =>
                        {
                            if (!r.InProgress)
                                Plugin.ChatGui.Print($"[Glamour Tracker+] {r.Message}");
                            if (r is { Success: true, InProgress: false })
                                this.plugin.RefreshAll(true);
                        });
                        Plugin.ChatGui.Print($"[Glamour Tracker+] {result.Message}");
                    });
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Reroll {GlamourPlateSlotMap.Labels[i]}");

            ImGui.EndDisabled();
        }

        if (changed)
        {
            config.RandomizeIncludeDresser = includeDresser;
            config.RandomizeIncludeArmoire = includeArmoire;
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextColored(
            editorOpen ? new Vector4(0.55f, 1f, 0.65f, 1f) : new Vector4(1f, 0.7f, 0.4f, 1f),
            editorOpen ? "Plate editor is open." : "Plate editor is closed — open it at a dresser.");

        ImGui.BeginDisabled(!editorOpen || busy || (!includeDresser && !includeArmoire));
        if (ImGui.Button("Randomize current plate"))
        {
            _ = Plugin.Framework.RunOnFrameworkThread(() =>
            {
                var result = this.plugin.BeginRandomizeOpenPlate(r =>
                {
                    if (!r.InProgress)
                        Plugin.ChatGui.Print($"[Glamour Tracker+] {r.Message}");
                    if (r is { Success: true, InProgress: false })
                        this.plugin.RefreshAll(true);
                });
                Plugin.ChatGui.Print($"[Glamour Tracker+] {result.Message}");
            });
        }

        ImGui.EndDisabled();
        if (busy)
            ImGui.TextDisabled("Applying slots…");
        ImGui.TextDisabled("Use Randomize above, or the plate editor overlay. Slot reload icons reroll one piece.");
    }

    private void DrawGlamourPlates()
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var plates = GlamourPlateStore.GetPlates(
            this.plugin.Configuration,
            Plugin.GetLocalContentId(),
            this.plugin.OwnershipIndex);
        if (plates.Count == 0)
        {
            ImGui.TextWrapped(
                "No saved glamour plates yet. Edit plates in-game (or open the glamour dresser), then use Refresh. "
                + "Saved plates stay in this tab after you relog.");
            return;
        }

        ImGui.BeginChild("PlateList", new Vector2(0, -4));
        foreach (var plate in plates)
        {
            var stored = plate.Pieces.Count(p => p.Storage != GlamourStorageLocation.None);
            if (ImGui.CollapsingHeader($"Plate {plate.PlateIndex} ({stored}/{plate.Pieces.Count})###plate{plate.PlateIndex}"))
            {
                foreach (var piece in plate.Pieces.OrderBy(p => p.Slot))
                {
                    var item = itemSheet.GetRow(piece.ItemId);
                    var name = string.IsNullOrWhiteSpace(item.Name.ExtractText())
                        ? $"Item #{piece.ItemId}"
                        : item.Name.ExtractText();
                    ImGui.BulletText($"{name} — {TrackerNativeHelpers.FormatStorage(piece.Storage)}");
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawSettings()
    {
        var config = this.plugin.Configuration;
        var enabled = config.Enabled;
        var showIcons = config.ShowTooltipIcons;
        var showGc = config.ShowGcExpertDeliveryStatus;
        var showCreation = config.ShowGlamourCreationOwnershipIcons;
        var colorCode = config.ColorCodeStorageIcons;
        var onlyOwned = config.StorageIconsOnlyWhenOwned;
        var changed = false;

        changed |= ImGui.Checkbox("Enable plugin", ref enabled);

        ImGui.Separator();
        ImGui.Text("Appearance");
        var useLocalStyle = config.UsePlateOverlayLocalUiStyle;
        changed |= ImGui.Checkbox("Use Glamour Tracker+ theme (ImGui windows)", ref useLocalStyle);
        ImGui.TextDisabled(
            useLocalStyle
                ? "ImGui windows use the plugin theme below. Native tracker UI is unchanged."
                : "ImGui windows follow your Dalamud global style.");
        if (useLocalStyle)
        {
            config.PlateOverlayLocalUiTheme ??= PluginLocalUiTheme.CreateDefault();
            if (ImGui.CollapsingHeader("Edit theme colors"))
            {
                changed |= config.PlateOverlayLocalUiTheme.DrawEditor();
                if (ImGui.Button("Reset theme to defaults"))
                {
                    config.PlateOverlayLocalUiTheme = PluginLocalUiTheme.CreateDefault();
                    changed = true;
                }

                ImGui.SameLine();
                if (ImGui.Button("Write theme snapshot"))
                {
                    var paths = config.PlateOverlayLocalUiTheme.WriteSnapshot();
                    if (paths.Count > 0)
                        Plugin.ChatGui.Print($"[Glamour Tracker+] Theme snapshot saved ({paths.Count} copy(ies)).");
                    else
                        Plugin.ChatGui.PrintError("[Glamour Tracker+] Could not write theme snapshot. Check the log.");
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Saves your current theme to theme-snapshot.json "
                        + "(plugin log folder and next to the DLL) so it can be made the default later.");
                }
            }
        }

        ImGui.Separator();
        changed |= ImGui.Checkbox("Color-code dresser/armoire icons on tooltips", ref showIcons);
        ImGui.TextDisabled("Green = stored, red = missing (for items that can use that storage).");
        changed |= ImGui.Checkbox("Show dresser/armoire icons on GC expert delivery", ref showGc);
        ImGui.TextDisabled("Uses ui/uld/ItemDetailPutIn (baked). Atlas UV can still be tuned below.");
        changed |= ImGui.Checkbox("Show dresser/armoire ownership icons on Glamour Creation", ref showCreation);
        ImGui.TextDisabled("Crystallize list. Dresser on the right; armoire on the left when both apply.");
        changed |= ImGui.Checkbox("Color-code owned Glamour Creation icons green", ref colorCode);
        ImGui.TextDisabled("Owned = green; missing stay normal (untinted). Crystallize list only.");
        changed |= ImGui.Checkbox("Only show Glamour Creation icons where owned", ref onlyOwned);
        ImGui.TextDisabled("Hides missing storage icons on the crystallize list only.");

        ImGui.Separator();
        var showPlateOverlay = config.ShowPlateEditorOverlay;
        var overlayOnRight = config.PlateEditorOverlayOnRight;
        var showSlotReroll = config.ShowSlotRerollButtons;
        changed |= ImGui.Checkbox("Show controls above plate editor", ref showPlateOverlay);
        ImGui.TextDisabled("Appears when the plate editor is open with the dresser or armoire.");
        if (showPlateOverlay)
        {
            ImGui.Indent();
            changed |= ImGui.Checkbox("Place on the right (avoids Glamaholic)", ref overlayOnRight);
            ImGui.Unindent();
        }

        changed |= ImGui.Checkbox("Show reroll next to each slot", ref showSlotReroll);

#if GLAMOUR_DEV
        if (showSlotReroll && ImGui.CollapsingHeader("Slot button positions"))
        {
            ImGui.Indent();
            changed |= PlateEditorOverlay.DrawSlotRerollPlacementControls(config, "settings");
            ImGui.Unindent();
        }
#endif

        if (showGc && ImGui.CollapsingHeader("GC icon atlas (tuning)"))
            changed |= DrawGcIconTuning(config);

        if (changed)
        {
            config.Enabled = enabled;
            config.UsePlateOverlayLocalUiStyle = useLocalStyle;
            config.ShowTooltipIcons = showIcons;
            config.ColorCodeStorageIcons = colorCode;
            config.StorageIconsOnlyWhenOwned = onlyOwned;
            config.ShowGcExpertDeliveryStatus = showGc;
            config.ShowGlamourCreationOwnershipIcons = showCreation;
            config.ShowPlateEditorOverlay = showPlateOverlay;
            config.PlateEditorOverlayOnRight = overlayOnRight;
            config.ShowSlotRerollButtons = showSlotReroll;
            config.Save();

            if (!enabled)
                this.plugin.RestoreTooltipEnhancements();
        }
    }

    private bool DrawGcIconTuning(Configuration config)
    {
        var changed = false;

        if (ImGui.Button("Re-learn texture path from tooltip"))
            this.plugin.RefreshGcIconPath();

        ImGui.SameLine();
        if (ImGui.Button("Reset atlas UV to defaults"))
        {
            StorageIconAtlasDefaults.ApplyUvDefaults(config);
            changed = true;
        }

        ImGui.TextDisabled(
            "These pixel atlas values feed GC delivery ATK icons (and tooltips). "
            + "Keep the delivery window open while tuning — icons refresh when you change a value.");
        ImGui.TextDisabled("Start with Flip off, then nudge Atlas V if you need the other row of the sheet.");

        changed |= DrawStorageIconTuning(config, "Dresser", isDresser: true);
        changed |= DrawStorageIconTuning(config, "Armoire", isDresser: false);

        return changed;
    }

    private bool DrawStorageIconTuning(Configuration config, string label, bool isDresser)
    {
        var changed = false;
        ImGui.Separator();
        ImGui.TextUnformatted(label);

        if (ImGui.TreeNodeEx($"Atlas crop (pixels)##{label}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var u = (int)(isDresser ? config.DresserUiIconU : config.ArmoireUiIconU);
            var v = (int)(isDresser ? config.DresserUiIconV : config.ArmoireUiIconV);
            var w = (int)(isDresser ? config.DresserUiIconW : config.ArmoireUiIconW);
            var h = (int)(isDresser ? config.DresserUiIconH : config.ArmoireUiIconH);
            changed |= ImGui.InputInt($"U (left)##{label}", ref u, 1, 36);
            changed |= ImGui.InputInt($"V (top)##{label}", ref v, 1, 36);
            changed |= ImGui.InputInt($"Width##{label}", ref w, 1, 4);
            changed |= ImGui.InputInt($"Height##{label}", ref h, 1, 4);
            u = Math.Clamp(u, 0, ushort.MaxValue);
            v = Math.Clamp(v, 0, ushort.MaxValue);
            w = Math.Clamp(w, 1, ushort.MaxValue);
            h = Math.Clamp(h, 1, ushort.MaxValue);
            if (isDresser)
            {
                config.DresserUiIconU = (ushort)u;
                config.DresserUiIconV = (ushort)v;
                config.DresserUiIconW = (ushort)w;
                config.DresserUiIconH = (ushort)h;
            }
            else
            {
                config.ArmoireUiIconU = (ushort)u;
                config.ArmoireUiIconV = (ushort)v;
                config.ArmoireUiIconW = (ushort)w;
                config.ArmoireUiIconH = (ushort)h;
            }

            var uOff = isDresser ? config.DresserIconUOffset : config.ArmoireIconUOffset;
            var vOff = isDresser ? config.DresserIconVOffset : config.ArmoireIconVOffset;
            var wOff = isDresser ? config.DresserIconWOffset : config.ArmoireIconWOffset;
            var hOff = isDresser ? config.DresserIconHOffset : config.ArmoireIconHOffset;
            changed |= ImGui.InputInt($"U offset##{label}", ref uOff, 1, 8);
            changed |= ImGui.InputInt($"V offset##{label}", ref vOff, 1, 8);
            changed |= ImGui.InputInt($"Width +##{label}", ref wOff, 1, 4);
            changed |= ImGui.InputInt($"Height +##{label}", ref hOff, 1, 4);
            if (isDresser)
            {
                config.DresserIconUOffset = uOff;
                config.DresserIconVOffset = vOff;
                config.DresserIconWOffset = wOff;
                config.DresserIconHOffset = hOff;
            }
            else
            {
                config.ArmoireIconUOffset = uOff;
                config.ArmoireIconVOffset = vOff;
                config.ArmoireIconWOffset = wOff;
                config.ArmoireIconHOffset = hOff;
            }

            var effU = Math.Clamp(u + uOff, 0, ushort.MaxValue);
            var effV = Math.Clamp(v + vOff, 0, ushort.MaxValue);
            var effW = Math.Clamp(w + wOff, 1, ushort.MaxValue);
            var effH = Math.Clamp(h + hOff, 1, ushort.MaxValue);
            ImGui.TextDisabled($"Effective crop: U={effU} V={effV} W={effW} H={effH}");

            ImGui.TreePop();
        }

        var scale = isDresser ? config.DresserIconDisplayScale : config.ArmoireIconDisplayScale;
        var flipV = isDresser ? config.FlipDresserIconV : config.FlipArmoireIconV;
        changed |= ImGui.SliderFloat($"On-screen size##{label}", ref scale, 0.5f, 2f);
        ImGui.SameLine();
        if (ImGui.Button($"Reset##{label}Size"))
        {
            scale = 1f;
            changed = true;
        }

        changed |= ImGui.Checkbox($"Flip vertically##{label}", ref flipV);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("ATK mirror flip. Prefer adjusting V to pick the bright/dark atlas row.");

        if (isDresser)
        {
            config.DresserIconDisplayScale = scale;
            config.FlipDresserIconV = flipV;
        }
        else
        {
            config.ArmoireIconDisplayScale = scale;
            config.FlipArmoireIconV = flipV;
        }

        return changed;
    }

    private static string FormatSetStorage(OutfitSetStorageLocation storage) => storage switch
    {
        OutfitSetStorageLocation.Dresser => "Dresser",
        OutfitSetStorageLocation.Armoire => "Armoire",
        OutfitSetStorageLocation.Both => "Both",
        _ => string.Empty,
    };

    private static Vector4 GetSetStorageLabelColor(OutfitSetInfo set)
    {
        if (set.SetStorage == OutfitSetStorageLocation.Both)
            return new Vector4(1f, 0.45f, 0.45f, 1f);

        if (set.SetStorage is OutfitSetStorageLocation.Dresser or OutfitSetStorageLocation.Armoire)
        {
            return set.MissingPieces == 0
                ? new Vector4(0.55f, 1f, 0.65f, 1f)
                : new Vector4(0.55f, 0.78f, 1f, 1f);
        }

        return new Vector4(0.7f, 0.7f, 0.7f, 1f);
    }

}
