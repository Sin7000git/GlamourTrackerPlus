using System.Numerics;
using GlamourTracker.Services;

namespace GlamourTracker.Windows.Native;

/// <summary>List row for Outfit sets browser.</summary>
internal sealed class TrackerNativeListRow
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public uint IconId { get; init; }
    public string Badge { get; init; } = string.Empty;
    public Vector4 BadgeColor { get; init; } = TrackerNativeHelpers.ColorMuted;
    public OutfitSetInfo? OutfitSet { get; init; }
}
