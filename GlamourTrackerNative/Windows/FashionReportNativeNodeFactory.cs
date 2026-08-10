using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

/// <summary>Shared ATK node builders for <see cref="FashionReportNativeAddon"/>.</summary>
internal static class FashionReportNativeNodeFactory
{
    internal static TextNode MakeText(string text, uint fontSize, Vector4 color, float width, float height) =>
        new()
        {
            Size = new Vector2(width, height),
            FontSize = fontSize,
            TextColor = color,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.Ellipsis,
        };

    internal static TextNode MakeWrappedText(string text, uint fontSize, Vector4 color, float width)
    {
        var lines = Math.Clamp(1 + (text.Length / 42), 1, 8);
        return new TextNode
        {
            Size = new Vector2(width, fontSize + 4f + (lines - 1) * (fontSize + 2f)),
            FontSize = fontSize,
            TextColor = color,
            String = (ReadOnlySeString)text,
            TextFlags = TextFlags.WordWrap | TextFlags.Ellipsis,
        };
    }
}
