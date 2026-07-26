using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services.FashionReport;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

/// <summary>
/// Proof-of-concept native Fashion Report window (KamiToolKit / ATK).
/// Full tab UI still lives in ImGui; this validates the native path.
/// </summary>
internal sealed class FashionReportNativeAddon : NativeAddon
{
    private readonly Plugin plugin;
    private TextNode? weekNode;
    private TextNode? statusNode;
    private TextNode? hintNode;
    private string lastWeekText = string.Empty;
    private string lastStatusText = string.Empty;

    public FashionReportNativeAddon(Plugin plugin)
    {
        this.plugin = plugin;
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);

        weekNode = new TextNode
        {
            Position = ContentStartPosition,
            Size = new Vector2(ContentSize.X, 28f),
            FontSize = 18,
            String = (ReadOnlySeString)"Loading Fashion Report…",
        };
        weekNode.AttachNode(this);

        statusNode = new TextNode
        {
            Position = ContentStartPosition + new Vector2(0f, 32f),
            Size = new Vector2(ContentSize.X, 24f),
            FontSize = 14,
            String = (ReadOnlySeString)"",
        };
        statusNode.AttachNode(this);

        hintNode = new TextNode
        {
            Position = ContentStartPosition + new Vector2(0f, 64f),
            Size = new Vector2(ContentSize.X, ContentSize.Y - 72f),
            FontSize = 12,
            String = (ReadOnlySeString)
                "Native UI shell (KamiToolKit).\n"
                + "Use /glamplusn for the full ImGui Fashion Report while this experiment grows.",
        };
        hintNode.AttachNode(this);

        RefreshTexts();
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        if (weekNode == null || statusNode == null)
            return;

        var snap = this.plugin.FashionReport.Snapshot;
        string weekText;
        string statusText;
        Vector4 statusColor;

        if (snap == null)
        {
            weekText = "No Fashion Report loaded yet";
            statusText = "Open /glamplusn → Fashion Report → Refresh week";
            statusColor = new Vector4(0.75f, 0.75f, 0.75f, 1f);
        }
        else
        {
            weekText = $"Week {snap.Week} — {snap.Title}";
            var progress = this.plugin.FashionProgress.GetProgress();
            statusText = progress.Kind switch
            {
                FashionReportProgressKind.Complete => $"Complete · {progress.HighestScore}",
                FashionReportProgressKind.Incomplete => $"Incomplete · best {progress.HighestScore}",
                FashionReportProgressKind.Unknown => "Not synced yet — talk to Masked Rose",
                _ => "Judging opens Friday",
            };
            statusColor = progress.Kind switch
            {
                FashionReportProgressKind.Complete => new Vector4(0.45f, 0.95f, 0.55f, 1f),
                FashionReportProgressKind.Incomplete => new Vector4(1f, 0.55f, 0.4f, 1f),
                FashionReportProgressKind.Unknown => new Vector4(0.95f, 0.8f, 0.4f, 1f),
                _ => new Vector4(0.75f, 0.75f, 0.75f, 1f),
            };
        }

        if (weekText != lastWeekText)
        {
            weekNode.String = (ReadOnlySeString)weekText;
            lastWeekText = weekText;
        }

        if (statusText != lastStatusText)
        {
            statusNode.String = (ReadOnlySeString)statusText;
            statusNode.TextColor = statusColor;
            lastStatusText = statusText;
        }
    }
}
