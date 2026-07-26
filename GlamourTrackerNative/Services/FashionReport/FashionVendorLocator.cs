using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionVendorLocator
{
    private static readonly Regex CoordRegex = CoordPattern();

    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;

    public FashionVendorLocator(IDataManager dataManager, IClientState clientState, IObjectTable objectTable)
    {
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.objectTable = objectTable;
    }

    public FashionVendorPick? PickBest(
        IReadOnlyList<(string Name, string Loc)> vendors,
        int gil,
        PlayerAreaContext? context)
    {
        if (vendors.Count == 0)
            return null;

        FashionVendorPick? best = null;
        var bestRank = (SameArea: false, HasDistance: false, Dist: float.MaxValue, Index: int.MaxValue);

        for (var i = 0; i < vendors.Count; i++)
        {
            var (name, loc) = vendors[i];
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(loc))
                continue;

            var sameArea = context != null && IsSameArea(loc, context);
            float? distSq = null;
            if (sameArea && context is { HasMapPos: true } && TryParseCoords(loc, out var x, out var y))
                distSq = DistanceSquared(context.MapX, context.MapY, x, y);

            var rank = (sameArea, distSq.HasValue, distSq ?? float.MaxValue, i);
            if (best != null && CompareRank(rank, bestRank) >= 0)
                continue;

            bestRank = rank;
            best = new FashionVendorPick
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Vendor" : name,
                Location = loc ?? string.Empty,
                Gil = gil,
                SameArea = sameArea,
                DistanceSquared = distSq,
            };
        }

        return best;
    }

    private static int CompareRank(
        (bool SameArea, bool HasDistance, float Dist, int Index) a,
        (bool SameArea, bool HasDistance, float Dist, int Index) b)
    {
        var area = b.SameArea.CompareTo(a.SameArea);
        if (area != 0)
            return area;

        var hasDist = b.HasDistance.CompareTo(a.HasDistance);
        if (hasDist != 0)
            return hasDist;

        var dist = a.Dist.CompareTo(b.Dist);
        if (dist != 0)
            return dist;

        return a.Index.CompareTo(b.Index);
    }

    /// <summary>Must be called on the game/framework thread (reads LocalPlayer).</summary>
    public PlayerAreaContext? CapturePlayerContext()
    {
        if (!clientState.IsLoggedIn)
            return null;

        var territoryId = clientState.TerritoryType;
        if (territoryId == 0)
            return null;

        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        if (!sheet.TryGetRow(territoryId, out var territory))
            return null;

        var place = territory.PlaceName.Value.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(place))
            return null;

        var aliases = GetAreaAliases(place);

        try
        {
            var player = objectTable.LocalPlayer;
            if (player == null)
                return new PlayerAreaContext(place, aliases, 0, 0, false);

            if (TryWorldToMap(territory, player.Position, out var mapX, out var mapY))
                return new PlayerAreaContext(place, aliases, mapX, mapY, true);
        }
        catch (InvalidOperationException)
        {
            // ObjectTable is framework-thread only; fall back to area name matching.
            return new PlayerAreaContext(place, aliases, 0, 0, false);
        }

        return new PlayerAreaContext(place, aliases, 0, 0, false);
    }

    private static bool TryWorldToMap(TerritoryType territory, Vector3 world, out float mapX, out float mapY)
    {
        mapX = 0;
        mapY = 0;
        try
        {
            var map = territory.Map.Value;
            var sizeFactor = map.SizeFactor <= 0 ? 100f : map.SizeFactor;
            var scale = sizeFactor / 100f;
            mapX = (world.X + map.OffsetX) / (50f * scale) + 1f;
            mapY = (world.Z + map.OffsetY) / (50f * scale) + 1f;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameArea(string location, PlayerAreaContext context)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;

        foreach (var alias in context.Aliases)
        {
            if (location.Contains(alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return location.Contains(context.PlaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetAreaAliases(string placeName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { placeName };

        if (ContainsAny(placeName, "Ul'dah", "Uldah", "Steps of Thal", "Steps of Nald"))
        {
            set.Add("Ul'dah");
            set.Add("Steps of Thal");
            set.Add("Steps of Nald");
        }

        if (ContainsAny(placeName, "Limsa", "Lower Decks", "Upper Decks"))
        {
            set.Add("Limsa Lominsa");
            set.Add("Limsa Lominsa Lower Decks");
            set.Add("Limsa Lominsa Upper Decks");
        }

        if (ContainsAny(placeName, "Gridania", "Old Gridania", "New Gridania"))
        {
            set.Add("Gridania");
            set.Add("Old Gridania");
            set.Add("New Gridania");
        }

        if (ContainsAny(placeName, "Ishgard", "Foundation", "Pillars"))
        {
            set.Add("Ishgard");
            set.Add("Foundation");
            set.Add("The Pillars");
        }

        if (ContainsAny(placeName, "Kugane"))
            set.Add("Kugane");

        if (ContainsAny(placeName, "Crystarium"))
            set.Add("The Crystarium");

        if (ContainsAny(placeName, "Old Sharlayan", "Sharlayan"))
            set.Add("Old Sharlayan");

        if (ContainsAny(placeName, "Tuliyollal"))
            set.Add("Tuliyollal");

        return set;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryParseCoords(string location, out float x, out float y)
    {
        x = 0;
        y = 0;
        var match = CoordRegex.Match(location);
        if (!match.Success)
            return false;

        return float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
               && float.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    private static float DistanceSquared(float x1, float y1, float x2, float y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return (dx * dx) + (dy * dy);
    }

    internal sealed record PlayerAreaContext(
        string PlaceName,
        HashSet<string> Aliases,
        float MapX,
        float MapY,
        bool HasMapPos);

    [GeneratedRegex(@"\(X:\s*([0-9.]+)\s*Y:\s*([0-9.]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoordPattern();
}
