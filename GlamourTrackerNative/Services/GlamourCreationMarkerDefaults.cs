namespace GlamourTracker.Services;

/// <summary>Built-in Glamour Creation crystallize-list marker placement (tuned in-game).</summary>
internal static class GlamourCreationMarkerDefaults
{
    /// <summary>Extra X offset (local UI units). Negative moves markers left.</summary>
    public const float NudgeX = -26f;

    public const float NudgeY = 0f;

    /// <summary>Gap between the rightmost marker and the row’s right edge.</summary>
    public const float GapFromRight = 0f;

    /// <summary>Horizontal space between dresser and armoire markers.</summary>
    public const float IconSpacing = 0f;

    /// <summary>Inset from the list row’s right edge before the rightmost marker.</summary>
    public const float PadRight = 0f;
}
