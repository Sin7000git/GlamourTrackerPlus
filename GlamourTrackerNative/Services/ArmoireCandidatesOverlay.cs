using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

/// <summary>
/// Panel locked beside the glamour dresser (HaselTweaks Glamour Dresser Alert placement):
/// dresser pieces that can go in the armoire, including ones already in both.
/// </summary>
internal sealed class ArmoireCandidatesOverlay
{
    private const string DresserAddonName = "MiragePrismPrismBox";
    private const string PlateAddonName = "MiragePrismMiragePlate";
    private const string OverlayId = "glamour-tracker-armoire-notes";

    // Match HaselTweaks GlamourDresserAlertWindow size / offset.
    private static readonly Vector2 WindowSize = new(370f, 428f);
    private static readonly Vector2 AddonOffset = new(-12f, 9f);

    private static readonly ImGuiWindowFlags WindowFlags =
        ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoNavFocus
        | ImGuiWindowFlags.NoNavInputs
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoDocking;

    private readonly IGameGui gameGui;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly Func<Configuration> getConfiguration;
    private readonly GlamourOwnershipIndex ownership;
    private readonly CabinetCatalog cabinetCatalog;

    private int lastRevision = int.MinValue;
    private readonly List<CandidateRow> cachedRows = [];

    public ArmoireCandidatesOverlay(
        IGameGui gameGui,
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        Func<Configuration> getConfiguration,
        GlamourOwnershipIndex ownership,
        CabinetCatalog cabinetCatalog)
    {
        this.gameGui = gameGui;
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.getConfiguration = getConfiguration;
        this.ownership = ownership;
        this.cabinetCatalog = cabinetCatalog;
    }

    public void Draw()
    {
        var config = this.getConfiguration();
        if (!config.Enabled || !config.ShowArmoireCandidates)
            return;

        if (HaselTweaksGate.IsGlamourDresserAlertEnabled(this.pluginInterface))
            return;

        var addon = this.gameGui.GetAddonByName(DresserAddonName, 1);
        if (addon == null || !addon.IsReady || !addon.IsVisible)
            return;

        // Same gate as HaselTweaks: hide while the plate editor is open.
        var plate = this.gameGui.GetAddonByName(PlateAddonName, 1);
        if (plate is { IsVisible: true })
            return;

        RefreshCacheIfNeeded();
        if (this.cachedRows.Count == 0)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var pos = ImGuiHelpers.MainViewport.Pos
                  + new Vector2(addon.X, addon.Y)
                  + new Vector2(addon.ScaledWidth, 0f)
                  + AddonOffset * scale;
        var size = WindowSize * scale;

        ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);

        if (!ImGui.Begin($"##{OverlayId}", WindowFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Can be stored in armoire");
        ImGui.Separator();

        var listH = MathF.Max(80f, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild("##armoire-notes-list", new Vector2(0f, listH), false);
        foreach (var row in this.cachedRows)
            ImGui.TextUnformatted(row.Name);

        ImGui.EndChild();

        ImGui.SetWindowPos(pos);
        ImGui.SetWindowSize(size);
        ImGui.End();
    }

    private void RefreshCacheIfNeeded()
    {
        var rev = this.ownership.Revision;
        if (rev == this.lastRevision)
            return;

        this.lastRevision = rev;
        this.cachedRows.Clear();

        var setRows = this.ownership.DresserSetPresenceIds;
        var seen = new HashSet<uint>();
        var items = this.dataManager.GetExcelSheet<Item>();

        void Consider(uint id)
        {
            var baseId = ItemIdHelper.GlamourBaseId(id);
            if (baseId == 0 || !seen.Add(baseId))
                return;
            if (setRows.Contains(baseId))
                return;
            if (!this.cabinetCatalog.IsArmoireEligible(baseId))
                return;

            var name = items.TryGetRow(baseId, out var item)
                ? item.Name.ToString()
                : $"Item {baseId}";
            this.cachedRows.Add(new CandidateRow(name));
        }

        foreach (var id in this.ownership.DresserItemIds)
            Consider(id);
        foreach (var id in this.ownership.DresserOutfitPieceIds)
            Consider(id);

        this.cachedRows.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct CandidateRow(string Name);
}
