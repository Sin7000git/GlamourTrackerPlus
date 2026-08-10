using System.Numerics;
using GlamourTracker.Services.FashionReport;
using GlamourTracker.Windows.Native;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

internal sealed partial class FashionReportNativeAddon
{
    private void RefreshChrome()
    {
        var service = plugin.FashionReport;
        var snap = service.Snapshot;

        if (refreshButton != null)
        {
            refreshButton.String = service.IsRefreshing ? "Refreshing…" : "Refresh week";
            refreshButton.IsEnabled = !service.IsRefreshing;
        }

        if (theorycraftButton != null)
            theorycraftButton.IsVisible = !string.IsNullOrWhiteSpace(snap?.TheorycraftUrl);
        if (resultsButton != null)
            resultsButton.IsVisible = !string.IsNullOrWhiteSpace(snap?.ResultsUrl);

        string weekText;
        string statusText;
        Vector4 statusColor;
        byte statusFontSize = 16;
        string metaText;

        if (snap == null)
        {
            weekText = service.IsRefreshing ? "Loading Fashion Report…" : "No Fashion Report loaded yet";
            statusText = string.Empty;
            statusColor = FashionReportNativeHelpers.ColorMuted;
            metaText = string.IsNullOrEmpty(service.LastError)
                ? "Press Refresh week to fetch this week's hints."
                : service.LastError;
        }
        else
        {
            weekText = $"Week {snap.Week} — {snap.Title}";
            var progress = plugin.FashionProgress.GetProgress();
            (statusColor, statusText, statusFontSize) = FashionReportNativeHelpers.FormatProgress(progress);
            if (statusNode != null)
                statusNode.TextTooltip = FashionReportNativeHelpers.ProgressTooltip(progress);

            var parts = new List<string>();
            if (service.LastFetchUtc is { } fetched)
                parts.Add($"Updated {fetched.ToLocalTime():g}");
            if (!string.IsNullOrEmpty(service.LastError))
                parts.Add(service.LastError);
            if (!string.IsNullOrWhiteSpace(selectedTabKey))
            {
                var hint = snap.Hints.FirstOrDefault(h => TabKeyForHint(h) == selectedTabKey);
                if (hint != null)
                {
                    parts.Add(hint.Hint);
                    parts.Add($"{hint.OwnedCount} owned");
                    if (!string.IsNullOrWhiteSpace(hint.RingNote))
                        parts.Add($"Ring: {hint.RingNote}");
                }
            }

            metaText = string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        RefreshVipChrome();

        if (weekNode != null && weekText != lastWeekText)
        {
            weekNode.String = (ReadOnlySeString)weekText;
            lastWeekText = weekText;
        }

        if (statusNode != null
            && (statusText != lastStatusText || statusFontSize != lastStatusFontSize))
        {
            statusNode.String = (ReadOnlySeString)statusText;
            statusNode.TextColor = statusColor;
            statusNode.FontSize = statusFontSize;
            lastStatusText = statusText;
            lastStatusFontSize = statusFontSize;
        }

        if (metaNode != null && metaText != lastMetaText)
        {
            metaNode.String = (ReadOnlySeString)metaText;
            metaNode.TextColor = string.IsNullOrEmpty(service.LastError)
                ? FashionReportNativeHelpers.ColorMuted
                : FashionReportNativeHelpers.ColorError;
            lastMetaText = metaText;
        }
    }

    private void RefreshVipChrome()
    {
        if (vipButton == null && vipIconNode == null)
            return;

        var view = plugin.FashionMgpBuff.GetView();
        var iconId = view.IconId != 0 ? view.IconId : 26173u;

        if (vipIconNode != null && iconId != lastVipIconId)
        {
            vipIconNode.IconId = iconId;
            lastVipIconId = iconId;
        }

        if (vipIconNode != null)
            vipIconNode.Color = view.CanUse
                ? Vector4.One
                : new Vector4(0.55f, 0.55f, 0.55f, 0.85f);

        if (vipButton == null)
            return;

        if (view.ButtonLabel != lastVipLabel)
        {
            vipButton.String = view.ButtonLabel;
            lastVipLabel = view.ButtonLabel;
        }

        vipButton.TextTooltip = view.Tooltip;
        if (view.CanUse != lastVipEnabled || vipButton.IsEnabled != view.CanUse)
        {
            vipButton.IsEnabled = view.CanUse;
            lastVipEnabled = view.CanUse;
        }
    }
}
