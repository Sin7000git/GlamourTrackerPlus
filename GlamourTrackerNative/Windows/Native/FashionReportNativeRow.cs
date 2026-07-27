using System.Numerics;
using GlamourTracker.Services.FashionReport;

namespace GlamourTracker.Windows.Native;

internal enum FashionReportNativeRowKind
{
    Item,
    Dye,
    Info,
}

/// <summary>Virtual-list row model for the native Fashion Report shell.</summary>
internal sealed class FashionReportNativeRow
{
    public required FashionReportNativeRowKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public uint IconId { get; init; }
    public string Badge { get; init; } = string.Empty;
    public Vector4 BadgeColor { get; init; } = new(0.8f, 0.8f, 0.8f, 1f);
    public FashionResolvedItem? Item { get; init; }
}
