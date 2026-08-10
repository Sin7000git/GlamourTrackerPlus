using System;

namespace GlamourTracker;

/// <summary>
/// Where a single gear piece lives. <see cref="FlagsAttribute"/> so a piece can be in both
/// dresser and armoire at once (bitwise OR). Distinct from <see cref="OutfitSetStorageLocation"/>,
/// which describes a whole outfit set's completion story and uses a non-flags <c>Both</c> value.
/// </summary>
[Flags]
public enum GlamourStorageLocation
{
    None = 0,
    Dresser = 1,
    Armoire = 2,
}
