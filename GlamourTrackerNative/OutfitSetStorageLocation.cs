namespace GlamourTracker;

/// <summary>
/// Where an outfit set is considered complete. Not <see cref="FlagsAttribute"/>: <see cref="Both"/> is a
/// single mutually exclusive outcome (complete in dresser and armoire), not a combination of piece flags.
/// Piece-level storage uses <see cref="GlamourStorageLocation"/> instead.
/// </summary>
public enum OutfitSetStorageLocation
{
    None = 0,
    Dresser = 1,
    Armoire = 2,
    Both = 3,
}
