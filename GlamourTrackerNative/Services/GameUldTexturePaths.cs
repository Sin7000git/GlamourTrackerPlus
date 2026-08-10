using Dalamud.Plugin.Services;

namespace GlamourTracker.Services;

/// <summary>Resolve <c>ui/uld/…</c> stems to an on-disk <c>.tex</c>, preferring HR.</summary>
internal static class GameUldTexturePaths
{
    public static string ResolvePreferHr(IDataManager data, string stem)
    {
        var hr = stem + "_hr1.tex";
        if (data.FileExists(hr))
            return hr;

        var sd = stem + ".tex";
        if (data.FileExists(sd))
            return sd;

        return hr;
    }
}
