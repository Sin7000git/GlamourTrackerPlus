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
        var changed = false;

        ImGui.TextUnformatted("Filters");
        ImGui.TextDisabled("Optional. Leave off for any owned dresser/armoire gear.");

        var mode = (int)config.RandomizeJobFilter;
        if (mode < 0 || mode >= JobModeLabels.Length)
            mode = 0;

        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo($"Job restriction##jobMode{idSuffix}", ref mode, JobModeLabels, JobModeLabels.Length))
        {
            config.RandomizeJobFilter = (RandomizeJobFilterMode)mode;
            changed = true;
        }

        if (config.RandomizeJobFilter == RandomizeJobFilterMode.CurrentJob)
        {
            var player = objectTable.LocalPlayer;
            if (player != null && dataManager.GetExcelSheet<ClassJob>().TryGetRow(player.ClassJob.RowId, out var job))
                ImGui.TextDisabled($"Using {job.Abbreviation.ExtractText()} — {job.Name.ExtractText()}");
            else
                ImGui.TextDisabled("Current job unknown until you are logged in.");
        }
        else if (config.RandomizeJobFilter == RandomizeJobFilterMode.SpecificJob)
        {
            changed |= DrawJobPicker(config, dataManager, idSuffix);
        }

        var limitReq = config.RandomizeLimitRequiredLevel;
        if (ImGui.Checkbox($"Limit by required level##reqLvl{idSuffix}", ref limitReq))
        {
            config.RandomizeLimitRequiredLevel = limitReq;
            changed = true;
        }

        if (limitReq)
        {
            ImGui.Indent();
            var minReq = config.RandomizeMinRequiredLevel;
            var maxReq = config.RandomizeMaxRequiredLevel;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt($"Lowest required level##minReq{idSuffix}", ref minReq, 1, 100))
            {
                config.RandomizeMinRequiredLevel = minReq;
                changed = true;
            }

            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt($"Highest required level##maxReq{idSuffix}", ref maxReq, 1, 100))
            {
                config.RandomizeMaxRequiredLevel = maxReq;
                changed = true;
            }

            ImGui.Unindent();
        }

        ImGui.TextDisabled("Race and gender limits always apply (unusable pieces are skipped).");

        var limitIlvl = config.RandomizeLimitItemLevel;
        if (ImGui.Checkbox($"Limit by item level##ilvl{idSuffix}", ref limitIlvl))
        {
            config.RandomizeLimitItemLevel = limitIlvl;
            changed = true;
        }

        if (limitIlvl)
        {
            ImGui.Indent();
            var minIlvl = config.RandomizeMinItemLevel;
            var maxIlvl = config.RandomizeMaxItemLevel;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt($"Minimum item level##minIlvl{idSuffix}", ref minIlvl))
            {
                config.RandomizeMinItemLevel = Math.Clamp(minIlvl, 1, 9999);
                changed = true;
            }

            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt($"Maximum item level##maxIlvl{idSuffix}", ref maxIlvl))
            {
                config.RandomizeMaxItemLevel = Math.Clamp(maxIlvl, 1, 9999);
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
