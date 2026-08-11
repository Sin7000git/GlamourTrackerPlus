using Dalamud.Bindings.ImGui;
using GlamourTracker.Services;

namespace GlamourTracker.Windows;

/// <summary>Shared ImGui slot-lock checkboxes for plate randomize (overlay + Randomize tab).</summary>
internal static class RandomizeSlotLockUi
{
    /// <summary>Left column — matches the game plate paperdoll (weapons + armor).</summary>
    private static readonly (int Index, string Label)[] LeftColumn =
    [
        (0, "MH"),
        (2, "Head"),
        (3, "Body"),
        (4, "Hands"),
        (5, "Legs"),
        (6, "Feet"),
    ];

    /// <summary>Right column — off-hand + accessories.</summary>
    private static readonly (int Index, string Label)[] RightColumn =
    [
        (1, "OH"),
        (7, "Ears"),
        (8, "Neck"),
        (9, "Wrists"),
        (10, "Right ring"),
        (11, "Left ring"),
    ];

    /// <summary>
    /// Draws the Slot locks heading and a two-column checkbox grid.
    /// Returns true if any lock value changed (caller should Save).
    /// </summary>
    public static bool Draw(Configuration config, string idSuffix = "")
    {
        GlamourPlateRandomizer.EnsureLockArray(config);
        var locks = config.RandomizeLockedSlots;
        var changed = false;

        ImGui.TextUnformatted("Slot locks");

        var startY = ImGui.GetCursorPosY();
        var colWidth = MathF.Max(110f, ImGui.CalcTextSize("Right ring").X + ImGui.GetFrameHeight() + 24f);

        changed |= DrawColumn(locks, LeftColumn, idSuffix + "L");
        ImGui.SetCursorPos(new System.Numerics.Vector2(colWidth, startY));
        changed |= DrawColumn(locks, RightColumn, idSuffix + "R");

        // Advance past the taller column before Unlock/Lock buttons.
        var rows = Math.Max(LeftColumn.Length, RightColumn.Length);
        var rowStep = ImGui.GetFrameHeightWithSpacing();
        ImGui.SetCursorPosY(startY + rows * rowStep);
        ImGui.NewLine();

        if (ImGui.Button($"Unlock all##unlock{idSuffix}"))
        {
            Array.Fill(locks, false);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button($"Lock all##lock{idSuffix}"))
        {
            Array.Fill(locks, true);
            changed = true;
        }

        return changed;
    }

    private static bool DrawColumn(bool[] locks, (int Index, string Label)[] column, string idSuffix)
    {
        var changed = false;
        ImGui.BeginGroup();
        foreach (var (index, label) in column)
        {
            var locked = locks[index];
            if (ImGui.Checkbox($"{label}##lock{index}{idSuffix}", ref locked))
            {
                locks[index] = locked;
                changed = true;
            }
        }

        ImGui.EndGroup();
        return changed;
    }
}
