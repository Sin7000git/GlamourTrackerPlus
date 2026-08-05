using System.Diagnostics;
using System.Numerics;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;

namespace GlamourTracker.Windows.Native;

internal static class FashionReportNativeHelpers
{
    public static readonly Vector4 ColorOwned = new(0.45f, 0.95f, 0.55f, 1f);
    public static readonly Vector4 ColorMatsReady = new(0.45f, 0.7f, 1f, 1f);
    public static readonly Vector4 ColorMatsMissing = new(1f, 0.45f, 0.4f, 1f);
    public static readonly Vector4 ColorComplete = new(0.45f, 0.95f, 0.55f, 1f);
    public static readonly Vector4 ColorIncomplete = new(1f, 0.55f, 0.4f, 1f);
    public static readonly Vector4 ColorUnknown = new(0.95f, 0.8f, 0.4f, 1f);
    public static readonly Vector4 ColorUnavailable = new(0.65f, 0.65f, 0.7f, 1f);
    public static readonly Vector4 ColorMgpReminder = new(0.45f, 0.75f, 1f, 1f);
    public static readonly Vector4 ColorSlot = new(0.95f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 ColorSection = new(0.8f, 0.8f, 0.8f, 1f);
    public static readonly Vector4 ColorError = new(1f, 0.45f, 0.45f, 1f);
    public static readonly Vector4 ColorMuted = new(0.7f, 0.7f, 0.68f, 1f);

    public static (Vector4 Color, string Label, byte FontSize) FormatProgress(FashionReportProgressView progress) =>
        progress.Kind switch
        {
            FashionReportProgressKind.Complete => (ColorComplete, $"Complete · Score {progress.HighestScore}", 22),
            FashionReportProgressKind.Incomplete => (ColorIncomplete, $"Incomplete · Score {progress.HighestScore}", 16),
            FashionReportProgressKind.Unknown => (ColorUnknown, "Not synced yet", 16),
            _ => (ColorUnavailable, "Judging opens Friday", 16),
        };

    public static string ProgressTooltip(FashionReportProgressView progress) =>
        progress.Kind switch
        {
            FashionReportProgressKind.Complete =>
                "You scored 80 or higher this week.",
            FashionReportProgressKind.Incomplete =>
                $"Best score this week: {progress.HighestScore}. Attempts left: {progress.AllowancesRemaining}.",
            FashionReportProgressKind.Unknown =>
                "Talk to Masked Rose at the Gold Saucer to sync your score.",
            _ => "Fashion Report judging runs Friday through Tuesday reset.",
        };

    /// <summary>Left-list badge — ownership only (materials live in the detail pane).</summary>
    public static string ListBadge(FashionResolvedItem item) =>
        item.Owned ? "Owned" : string.Empty;

    public static Vector4 ListBadgeColor(FashionResolvedItem item) =>
        item.Owned ? ColorOwned : ColorMuted;

    public static string MaterialsBadge(FashionResolvedItem item)
    {
        if (item.Owned || item.CraftMatsTotal == 0)
            return string.Empty;
        return $"Materials {item.CraftMatsReady}/{item.CraftMatsTotal}";
    }

    public static Vector4 MaterialsBadgeColor(FashionResolvedItem item)
    {
        if (item.CraftMatsTotal == 0)
            return ColorMuted;
        return item.CraftMatsReady == item.CraftMatsTotal ? ColorMatsReady : ColorMatsMissing;
    }

    public static Vector4 TagColor(string type)
    {
        if (type.Equals("vendor", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.55f, 0.9f, 0.65f, 1f);
        if (type.Equals("market", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.45f, 0.75f, 0.55f, 1f);
        if (type.Equals("craft", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.45f, 0.85f, 0.9f, 1f);
        if (type.Equals("quest", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.9f, 0.45f, 0.45f, 1f);
        if (type.Equals("barter", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.85f, 0.65f, 0.95f, 1f);
        if (type.Equals("gc", StringComparison.OrdinalIgnoreCase))
            return new Vector4(0.95f, 0.8f, 0.4f, 1f);
        return ColorSection;
    }

    public static string FormatIngredientLine(FashionCraftIngredient ing) =>
        $"{ing.Required}× {ing.Name} — {ing.OwnedCount}/{ing.Required}";

    /// <summary>True when a line includes map coordinates (e.g. NPC vendor / exchange locations).</summary>
    public static bool HasMapCoordinates(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("(X:", StringComparison.OrdinalIgnoreCase)
        && text.Contains("Y:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Summary that only repeats a section label/headline — hide it in the detail pane.</summary>
    public static bool IsRedundantSummary(string? summary, FashionResolvedItem item)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return true;

        foreach (var section in item.Sections)
        {
            if (string.Equals(summary, section.Label, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(section.Headline)
                && string.Equals(summary, section.Headline, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool LineDuplicatesHeadline(string line, string? headline) =>
        !string.IsNullOrWhiteSpace(headline)
        && string.Equals(line.Trim(), headline.Trim(), StringComparison.OrdinalIgnoreCase);

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("fashion.native", $"Failed to open URL {url}", ex);
            Plugin.ChatGui.PrintError("Could not open the link in your browser.");
        }
    }
}
