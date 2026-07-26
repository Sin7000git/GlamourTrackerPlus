using System.Numerics;

namespace GlamourTracker.Services;

/// <summary>
/// One icon graphic from an ItemDetail storage group: texture path plus atlas UV rectangle.
/// </summary>
internal readonly struct StorageUiIconSlice
{
    public string Path { get; init; }
    public ushort U { get; init; }
    public ushort V { get; init; }
    public ushort Width { get; init; }
    public ushort Height { get; init; }
    public float DisplayWidth { get; init; }
    public float DisplayHeight { get; init; }

    public bool IsValid => !string.IsNullOrWhiteSpace(this.Path) && this.Width > 0 && this.Height > 0;

    public Vector2 DisplaySize => new(this.DisplayWidth, this.DisplayHeight);
}
