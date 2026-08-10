using System.Numerics;
using GlamourTracker.Services;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.Nodes;

using static GlamourTracker.Windows.TrackerNativeNodeFactory;

namespace GlamourTracker.Windows;

internal sealed partial class TrackerNativeAddon
{
    private bool OverviewInputsUnchanged()
    {
        var index = plugin.OwnershipIndex;
        var progress = plugin.FashionProgress.GetProgress();
        var week = plugin.FashionReport.Snapshot?.Week ?? string.Empty;
        var packed = ((int)progress.Kind << 16) ^ progress.HighestScore;
        var catalogEpoch = plugin.OutfitSets.CatalogEpoch;

        if (lastFormSignature.Length > 0
            && index.Revision == lastOverviewOwnershipRevision
            && catalogEpoch == lastOverviewCatalogEpoch
            && week == lastOverviewWeek
            && packed == lastOverviewProgressPacked)
            return true;

        lastOverviewOwnershipRevision = index.Revision;
        lastOverviewCatalogEpoch = catalogEpoch;
        lastOverviewWeek = week;
        lastOverviewProgressPacked = packed;
        return false;
    }

    private string BuildOverviewSignature(GlamourOwnershipIndex index)
    {
        var sets = plugin.OutfitSets.GetOverviewStats();
        var progress = plugin.FashionProgress.GetProgress();
        var week = plugin.FashionReport.Snapshot?.Week ?? string.Empty;

        // Deliberately omit LastRefresh: background ownership ticks update that clock even when
        // every count is unchanged, and putting it here rebuilt the whole Overview every 30s.
        return $"ov|{index.Revision}|{plugin.OutfitSets.CatalogEpoch}|{index.DresserSlotsUsed}|{index.DresserUniqueCount}|{index.ArmoireCount}|{index.HasPersistedData}|{sets.DresserEligible}|{sets.ArmoireEligible}|{sets.SetsInDresser}|{sets.SetsInArmoire}|{sets.CompletedInDresser}|{sets.CompletedInArmoire}|{week}|{(int)progress.Kind}|{progress.HighestScore}";
    }

    private void BuildOverview(VerticalListNode list, float width)
    {
        var index = plugin.OwnershipIndex;
        var setStats = plugin.OutfitSets.GetOverviewStats();
        var progress = plugin.FashionProgress.GetProgress();
        var snap = plugin.FashionReport.Snapshot;

        // —— Top: Fashion Report (full width) ——
        list.AddNode(MakeSection("Fashion Report"));
        var weekLine = snap != null ? $"Week {snap.Week}" : "Not loaded yet";
        list.AddNode(MakeOverviewStatRow("This week", weekLine, width));
        var (progressColor, progressText) = FormatOverviewFashionProgress(progress);
        list.AddNode(MakeOverviewStatRow("Judging", progressText, width, progressColor));

        var frActions = new HorizontalListNode
        {
            Size = new Vector2(width, RowH),
            ItemSpacing = 8f,
            X = TrackerNativeHelpers.Indent,
        };
        frActions.AddNode(new TextButtonNode
        {
            Size = new Vector2(180f, RowH),
            String = "Open Fashion Report",
            TextTooltip = "Same as /glamplus report.",
            OnClick = () => plugin.OpenFashionReportTab(),
        });
        list.AddNode(frActions);

        list.AddNode(new HorizontalLineNode { Size = new Vector2(width, 2f) });

        // —— Two columns: storage | outfit sets ——
        var colW = MathF.Floor((width - OverviewColumnGap) * 0.5f);
        var leftCol = new VerticalListNode
        {
            Size = new Vector2(colW, 1f),
            FitContents = true,
            FitWidth = true,
            ItemSpacing = 3f,
        };
        var rightCol = new VerticalListNode
        {
            Size = new Vector2(colW, 1f),
            FitContents = true,
            FitWidth = true,
            ItemSpacing = 3f,
        };

        leftCol.AddNode(MakeSection("Stored"));
        leftCol.AddNode(MakeOverviewStatRow(
            "Dresser slots",
            $"{index.DresserSlotsUsed} / 800",
            colW));
        leftCol.AddNode(MakeOverviewStatRow("Unique items in dresser", $"{index.DresserUniqueCount}", colW));
        leftCol.AddNode(MakeOverviewStatRow("Unique items in armoire", $"{index.ArmoireCount}", colW));
        var dataNote = index.HasPersistedData
            ? index.LastRefresh == DateTime.MinValue
                ? "Showing your last saved dresser/armoire list"
                : $"Last updated {index.LastRefresh.ToLocalTime():g}"
            : "No saved list yet — open your dresser or armoire once";
        leftCol.AddNode(MakeMutedIndented(dataNote, colW));
        leftCol.RecalculateLayout();

        rightCol.AddNode(MakeSection("Outfit sets"));
        rightCol.AddNode(MakeOverviewStatRow(
            "Completed in dresser",
            FormatOwnedRatio(setStats.CompletedInDresser, setStats.SetsInDresser),
            colW,
            setStats.CompletedInDresser > 0 ? TrackerNativeHelpers.ColorOk : TrackerNativeHelpers.ColorMuted));
        rightCol.AddNode(MakeOverviewStatRow(
            "Completed in armoire",
            FormatOwnedRatio(setStats.CompletedInArmoire, setStats.SetsInArmoire),
            colW,
            setStats.CompletedInArmoire > 0 ? TrackerNativeHelpers.ColorOk : TrackerNativeHelpers.ColorMuted));
        rightCol.AddNode(MakeOverviewStatRow(
            "Total sets in dresser",
            FormatRatio(setStats.SetsInDresser, setStats.DresserEligible),
            colW));
        rightCol.AddNode(MakeOverviewStatRow(
            "Total sets in armoire",
            FormatRatio(setStats.SetsInArmoire, setStats.ArmoireEligible),
            colW));
        rightCol.RecalculateLayout();

        var columnsH = MathF.Max(leftCol.Height, rightCol.Height);
        var columns = new ResNode { Size = new Vector2(width, columnsH) };
        leftCol.Position = Vector2.Zero;
        rightCol.Position = new Vector2(colW + OverviewColumnGap, 0f);
        leftCol.AttachNode(columns);
        rightCol.AttachNode(columns);
        list.AddNode(columns);
    }

    private static string FormatRatio(int have, int total) =>
        total > 0 ? $"{have} / {total}" : "—";

    /// <summary>Completed / owned ratios collapse to a dash when nothing is owned yet.</summary>
    private static string FormatOwnedRatio(int completedOrOwned, int ownedOrEligible) =>
        ownedOrEligible > 0 ? $"{completedOrOwned} / {ownedOrEligible}" : "—";

    private static (Vector4 Color, string Text) FormatOverviewFashionProgress(FashionReportProgressView progress) =>
        progress.Kind switch
        {
            FashionReportProgressKind.Complete =>
                (TrackerNativeHelpers.ColorOk, $"Complete · Score {progress.HighestScore}"),
            FashionReportProgressKind.Incomplete =>
                (TrackerNativeHelpers.ColorWarn, $"Score {progress.HighestScore} · Keep going"),
            FashionReportProgressKind.Unknown =>
                (TrackerNativeHelpers.ColorMuted, "Talk to the Masked Rose to sync"),
            _ =>
                (TrackerNativeHelpers.ColorMuted, "Judging closed"),
        };

}
