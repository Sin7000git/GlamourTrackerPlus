using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Windows.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

/// <summary>Shared ATK node builders for <see cref="TrackerNativeAddon"/>.</summary>
internal static class TrackerNativeNodeFactory
{
    private const float RowH = 28f;
    private const float OverviewLabelWidth = 175f;
    private const float OverviewStatRowH = 22f;

    internal static TextNode MakeSection(string text) =>
        MakeText(text, 14, TrackerNativeHelpers.ColorInfo, 400f, 20f);

    internal static TextNode MakeText(string text, byte fontSize, Vector4 color, float width, float height) =>
        new()
        {
            Size = new Vector2(width, height),
            FontSize = fontSize,
            TextColor = color,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

    internal static TextNode MakeMuted(string text, float width) =>
        MakeText(text, 11, TrackerNativeHelpers.ColorMuted, width, 16f);

    internal static ResNode MakeIndented(NodeBase child, float width)
    {
        var wrap = new ResNode
        {
            Size = new Vector2(width, child.Height > 0 ? child.Height : RowH),
        };
        child.Position = new Vector2(TrackerNativeHelpers.Indent, 0f);
        child.AttachNode(wrap);
        return wrap;
    }

    internal static ResNode MakeIndentedCheckbox(string label, bool isChecked, Action<bool> onChanged, float width)
    {
        var cb = MakeCheckbox(label, isChecked, onChanged);
        return MakeIndented(cb, width);
    }

    internal static CheckboxNode MakeCheckbox(string label, bool isChecked, Action<bool> onChanged)
    {
        var node = new CheckboxNode
        {
            Size = new Vector2(24f, 24f),
            String = label,
        };
        node.IsChecked = isChecked;
        node.OnClick = onChanged;
        return node;
    }

    internal static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    internal static ResNode MakeOverviewStatRow(
        string label,
        string value,
        float width,
        Vector4? valueColor = null)
    {
        var row = new ResNode { Size = new Vector2(width, OverviewStatRowH) };
        var labelX = TrackerNativeHelpers.Indent;
        var labelNode = MakeText(label, 13, TrackerNativeHelpers.ColorMuted, OverviewLabelWidth, 18f);
        labelNode.Position = new Vector2(labelX, 2f);
        labelNode.AttachNode(row);

        var valueX = labelX + OverviewLabelWidth + 6f;
        var valueW = MathF.Max(48f, width - valueX - 4f);
        var valueNode = MakeText(value, 13, valueColor ?? TrackerNativeHelpers.ColorTitle, valueW, 18f);
        valueNode.Position = new Vector2(valueX, 2f);
        valueNode.AttachNode(row);

        return row;
    }

    internal static TextNode MakeMutedIndented(string text, float width) =>
        new()
        {
            Size = new Vector2(width - TrackerNativeHelpers.Indent, 16f),
            X = TrackerNativeHelpers.Indent,
            FontSize = 11,
            TextColor = TrackerNativeHelpers.ColorMuted,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

}
