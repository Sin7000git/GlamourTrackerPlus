using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows.Native;

/// <summary>List row: icon, name, subtitle, ownership badge.</summary>
internal sealed class FashionReportNativeItemNode : ListItemWithFocusNav<FashionReportNativeRow>, IListItemNode
{
    /// <summary>Matches KamiToolKit IconNode default icon face size (40×40).</summary>
    public static float ItemHeight => 48f;

    private static readonly Vector4 TitleColor = new(0.95f, 0.95f, 0.92f, 1f);
    private static readonly Vector4 SubtitleColor = new(0.7f, 0.7f, 0.68f, 1f);

    private readonly IconImageNode icon;
    private readonly TextNode title;
    private readonly TextNode subtitle;
    private readonly TextNode badge;

    public FashionReportNativeItemNode()
    {
        icon = new IconImageNode
        {
            Position = new Vector2(4f, 4f),
            Size = new Vector2(40f, 40f),
            TextureSize = new Vector2(40f, 40f),
            ImageNodeFlags = ImageNodeFlags.AutoFit,
        };
        icon.AttachNode(this);

        title = new TextNode
        {
            Position = new Vector2(52f, 4f),
            Size = new Vector2(200f, 20f),
            FontSize = 13,
            TextColor = TitleColor,
            TextFlags = TextFlags.Ellipsis,
        };
        title.AttachNode(this);

        subtitle = new TextNode
        {
            Position = new Vector2(52f, 24f),
            Size = new Vector2(200f, 18f),
            FontSize = 11,
            TextColor = SubtitleColor,
            TextFlags = TextFlags.Ellipsis,
        };
        subtitle.AttachNode(this);

        badge = new TextNode
        {
            Position = new Vector2(250f, 14f),
            Size = new Vector2(80f, 18f),
            FontSize = 12,
            AlignmentType = AlignmentType.Right,
        };
        badge.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        var textWidth = Math.Max(80f, Width - 52f - 88f);
        title.Position = new Vector2(52f, 4f);
        title.Size = new Vector2(textWidth, 20f);
        subtitle.Position = new Vector2(52f, 24f);
        subtitle.Size = new Vector2(textWidth, 18f);
        badge.Position = new Vector2(Width - 84f, 14f);
        badge.Size = new Vector2(80f, 18f);
    }

    protected override void SetNodeData(FashionReportNativeRow data)
    {
        icon.IconId = data.IconId;
        icon.IsVisible = data.IconId != 0;
        title.String = (ReadOnlySeString)data.Title;
        subtitle.String = (ReadOnlySeString)data.Subtitle;
        subtitle.IsVisible = !string.IsNullOrEmpty(data.Subtitle);
        badge.String = (ReadOnlySeString)data.Badge;
        badge.TextColor = data.BadgeColor;
        badge.IsVisible = !string.IsNullOrEmpty(data.Badge);
    }
}
