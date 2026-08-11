using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using GlamourTracker.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Windows;

/// <summary>Shared ImGui controls for randomizer job / level filters.</summary>
internal static class RandomizeFilterUi
{
    private static readonly string[] JobModeLabels =
    [
        "Any job",
        "Current job",
        "Choose a job",
    ];

    /// <summary>Draws filter controls. Returns true if config fields changed (caller should Save).</summary>
    public static bool Draw(Configuration config, IDataManager dataManager, IObjectTable objectTable, string idSuffix = "")
    {
        _ = objectTable;
        var changed = false;

        ImGui.TextUnformatted("Filters");

        var mode = (int)config.RandomizeJobFilter;
        if (mode < 0 || mode >= JobModeLabels.Length)
            mode = 0;

        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo($"Job restriction##jobMode{idSuffix}", ref mode, JobModeLabels, JobModeLabels.Length))
        {
            config.RandomizeJobFilter = (RandomizeJobFilterMode)mode;
            changed = true;
        }

        if (config.RandomizeJobFilter == RandomizeJobFilterMode.SpecificJob)
            changed |= DrawJobPicker(config, dataManager, idSuffix);

        var limitReq = config.RandomizeLimitRequiredLevel;
        if (ImGui.Checkbox($"Limit by required level##reqLvl{idSuffix}", ref limitReq))
        {
            config.RandomizeLimitRequiredLevel = limitReq;
            changed = true;
        }

        if (limitReq)
        {
            ImGui.Indent();
            var reqCap = GameLevelCaps.MaxRequiredLevel(dataManager);
            var minReq = Math.Clamp(config.RandomizeMinRequiredLevel, 1, reqCap);
            var maxReq = GameLevelCaps.ResolveRequiredLevelMax(dataManager, config.RandomizeMaxRequiredLevel);

            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt($"Lowest required level##minReq{idSuffix}", ref minReq, 1, reqCap))
            {
                config.RandomizeMinRequiredLevel = minReq;
                changed = true;
            }

            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt($"Highest required level##maxReq{idSuffix}", ref maxReq, 1, reqCap))
            {
                config.RandomizeMaxRequiredLevel = maxReq >= reqCap ? 0 : maxReq;
                changed = true;
            }

            ImGui.Unindent();
        }

        var limitIlvl = config.RandomizeLimitItemLevel;
        if (ImGui.Checkbox($"Limit by item level##ilvl{idSuffix}", ref limitIlvl))
        {
            config.RandomizeLimitItemLevel = limitIlvl;
            changed = true;
        }

        if (limitIlvl)
        {
            ImGui.Indent();
            var ilvlCap = GameLevelCaps.MaxItemLevel(dataManager);
            var minIlvl = Math.Clamp(config.RandomizeMinItemLevel, 1, ilvlCap);
            var maxIlvl = GameLevelCaps.ResolveItemLevelMax(dataManager, config.RandomizeMaxItemLevel);

            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt($"Minimum item level##minIlvl{idSuffix}", ref minIlvl, 1, ilvlCap))
            {
                config.RandomizeMinItemLevel = minIlvl;
                changed = true;
            }

            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt($"Maximum item level##maxIlvl{idSuffix}", ref maxIlvl, 1, ilvlCap))
            {
                config.RandomizeMaxItemLevel = maxIlvl >= ilvlCap ? 0 : maxIlvl;
                changed = true;
            }

            ImGui.Unindent();
        }

        return changed;
    }

    private static bool DrawJobPicker(Configuration config, IDataManager dataManager, string idSuffix)
    {
        var sheet = dataManager.GetExcelSheet<ClassJob>();
        var jobs = ClassJobFilterList.Build(sheet);
        if (jobs.Count == 0)
        {
            ImGui.TextDisabled("No jobs found in game data.");
            return false;
        }

        var changed = false;
        var resolved = ClassJobFilterList.ResolveStoredJobId(config.RandomizeSpecificJobId, sheet);
        if (resolved != config.RandomizeSpecificJobId)
        {
            config.RandomizeSpecificJobId = resolved;
            changed = true;
        }

        var selectedIndex = jobs.FindIndex(j => j.RowId == config.RandomizeSpecificJobId);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            config.RandomizeSpecificJobId = jobs[0].RowId;
            changed = true;
        }

        var labels = jobs.Select(j => j.Label).ToArray();
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo($"Job##jobPick{idSuffix}", ref selectedIndex, labels, labels.Length)
            && selectedIndex >= 0
            && selectedIndex < jobs.Count
            && config.RandomizeSpecificJobId != jobs[selectedIndex].RowId)
        {
            config.RandomizeSpecificJobId = jobs[selectedIndex].RowId;
            changed = true;
        }

        return changed;
    }
}
