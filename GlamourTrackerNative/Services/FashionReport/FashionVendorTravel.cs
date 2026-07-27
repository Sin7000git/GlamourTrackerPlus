using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services.FashionReport;

/// <summary>
/// Resolve vendor location strings and teleport via Teleporter (Pohky) when available,
/// with city/special-zone aliases and a direct Telepo fallback.
/// </summary>
internal sealed partial class FashionVendorTravel
{
    private static readonly Regex CoordRegex = CoordPattern();

    /// <summary>Places that have no public aetheryte — send the player to the access hub instead.</summary>
    private static readonly (string Needle, string TeleportName)[] SpecialDestinations =
    [
        ("Sinus Ardorum", "Bestways Burrow"),
        ("Phaenna", "Bestways Burrow"),
        ("Oizys", "Bestways Burrow"),
        ("Auxesia", "Bestways Burrow"),
        ("Bestways Burrow", "Bestways Burrow"),
    ];

    /// <summary>District / alias → aetheryte name that Teleporter's /tp understands.</summary>
    private static readonly (string Needle, string TeleportName)[] CityHubAliases =
    [
        ("Steps of Thal", "Ul'dah"),
        ("Steps of Nald", "Ul'dah"),
        ("Ul'dah", "Ul'dah"),
        ("Uldah", "Ul'dah"),
        ("Limsa Lominsa Lower Decks", "Limsa Lominsa"),
        ("Limsa Lominsa Upper Decks", "Limsa Lominsa"),
        ("Lower Decks", "Limsa Lominsa"),
        ("Upper Decks", "Limsa Lominsa"),
        ("Limsa Lominsa", "Limsa Lominsa"),
        ("Old Gridania", "New Gridania"),
        ("New Gridania", "New Gridania"),
        ("Gridania", "New Gridania"),
        ("The Pillars", "Foundation"),
        ("Foundation", "Foundation"),
        ("Ishgard", "Foundation"),
        ("Idyllshire", "Idyllshire"),
        ("Rhalgr's Reach", "Rhalgr's Reach"),
        ("Kugane", "Kugane"),
        ("The Crystarium", "The Crystarium"),
        ("Eulmore", "Eulmore"),
        ("Old Sharlayan", "Old Sharlayan"),
        ("Radz-at-Han", "Radz-at-Han"),
        ("Tuliyollal", "Tuliyollal"),
        ("Solution Nine", "Solution Nine"),
        ("Bestways Burrow", "Bestways Burrow"),
    ];

    private readonly IDataManager dataManager;
    private readonly IAetheryteList aetheryteList;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly ICommandManager commandManager;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private Func<uint, byte, bool>? teleportIpc;

    public FashionVendorTravel(
        IDataManager dataManager,
        IAetheryteList aetheryteList,
        IGameGui gameGui,
        IChatGui chatGui,
        ICommandManager commandManager,
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.aetheryteList = aetheryteList;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.commandManager = commandManager;
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
    }

    /// <summary>
    /// Accepts either a raw location ("Old Gridania (X: 10.7 Y: 11.7)")
    /// or a vendor line ("Merchant · Old Gridania (X: 10.7 Y: 11.7)").
    /// </summary>
    public void TeleportNearLocation(string vendorOrLocation)
    {
        _ = framework.RunOnFrameworkThread(() => TeleportNearLocationCore(vendorOrLocation));
    }

    private void TeleportNearLocationCore(string vendorOrLocation)
    {
        try
        {
            var location = ExtractLocation(vendorOrLocation);
            if (string.IsNullOrWhiteSpace(location))
            {
                chatGui.PrintError("[Glamour Tracker+] No location found for that vendor.");
                return;
            }

            TryParseCoords(location, out var mapX, out var mapY);
            var place = CoordRegex.Replace(location, string.Empty).Trim().TrimEnd(',', '.', ' ');
            if (string.IsNullOrWhiteSpace(place))
            {
                chatGui.PrintError("[Glamour Tracker+] No location found for that vendor.");
                return;
            }

            var teleportName = ResolveTeleportName(place);
            PluginFileLog.Info("fashion.travel", $"place={place} → teleportName={teleportName}");

            // Resolve an unlocked aetheryte, then prefer Teleporter IPC → Telepo.
            if (TryResolveAetheryte(teleportName, place, out var aetheryteId, out var subIndex, out var aetheryteName)
                && TryTeleportId(aetheryteId, subIndex, aetheryteName, place))
            {
                return;
            }

            // Name-based Teleporter command (city hubs / Bestways Burrow) when id resolve failed.
            if (IsTeleporterInstalled() && TryTeleporterCommand(teleportName))
                return;

            // Last resort: flag the map so the player still has a destination marker.
            if (TryResolveMap(place, mapX, mapY, out var territoryId, out var mapId, out var resolvedX, out var resolvedY, out var placeLabel))
            {
                try
                {
                    var link = new MapLinkPayload(territoryId, mapId, resolvedX, resolvedY, fudgeFactor: 0.05f);
                    gameGui.OpenMapWithMapLink(link);
                }
                catch (Exception ex)
                {
                    log.Debug(ex, "OpenMapWithMapLink failed for Fashion Report vendor.");
                }

                chatGui.PrintError(
                    $"[Glamour Tracker+] Could not teleport near {placeLabel}. "
                    + "A map flag was placed instead"
                    + (IsTeleporterInstalled() ? "." : " — install Teleporter (Pohky) for better city teleports."));
                PluginFileLog.Warn("fashion.travel", $"Fallback map flag place={place} teleportName={teleportName}");
                return;
            }

            chatGui.PrintError($"[Glamour Tracker+] Could not find a teleport for \"{place}\".");
            PluginFileLog.Warn("fashion.travel", $"Unresolved location: {place}");
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("fashion.travel", $"Teleport failed for {vendorOrLocation}", ex);
            chatGui.PrintError("[Glamour Tracker+] Teleport failed. See log for details.");
        }
    }

    private bool TryTeleporterCommand(string name)
    {
        try
        {
            // /tp matches aetheryte PlaceName (Ul'dah, New Gridania, Bestways Burrow, …).
            commandManager.ProcessCommand($"/tp {name}");
            PluginFileLog.Info("fashion.travel", $"Teleporter /tp {name}");
            return true;
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("fashion.travel", $"/tp failed for {name}: {ex.Message}");
            return false;
        }
    }

    private unsafe bool TryTeleportId(uint aetheryteId, byte subIndex, string aetheryteName, string place)
    {
        try
        {
            teleportIpc ??= TryGetTeleportIpc();
            if (teleportIpc != null)
            {
                try
                {
                    if (teleportIpc(aetheryteId, subIndex))
                    {
                        chatGui.Print($"[Glamour Tracker+] Teleporting to {aetheryteName} (near {place}).");
                        PluginFileLog.Info("fashion.travel", $"IPC Teleport aetheryte={aetheryteId} sub={subIndex}");
                        return true;
                    }
                }
                catch (IpcError ex)
                {
                    log.Debug(ex, "Teleporter IPC failed; falling back to Telepo.");
                    teleportIpc = null;
                }
            }

            var telepo = Telepo.Instance();
            if (telepo == null)
                return false;

            if (!telepo->Teleport(aetheryteId, subIndex))
                return false;

            chatGui.Print($"[Glamour Tracker+] Teleporting to {aetheryteName} (near {place}).");
            PluginFileLog.Info("fashion.travel", $"Telepo aetheryte={aetheryteId} sub={subIndex}");
            return true;
        }
        catch (Exception ex)
        {
            PluginFileLog.Warn("fashion.travel", $"TryTeleportId failed: {ex.Message}");
            return false;
        }
    }

    private Func<uint, byte, bool>? TryGetTeleportIpc()
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport").InvokeFunc;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Teleporter IPC subscriber unavailable.");
            return null;
        }
    }

    private bool IsTeleporterInstalled() =>
        pluginInterface.InstalledPlugins.Any(p =>
            p.IsLoaded
            && (string.Equals(p.InternalName, "TeleporterPlugin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "Teleporter", StringComparison.OrdinalIgnoreCase)));

    private static string ResolveTeleportName(string place)
    {
        foreach (var (needle, name) in SpecialDestinations)
        {
            if (place.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        foreach (var (needle, name) in CityHubAliases)
        {
            if (place.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        // Prefer the longest leading segment before a dash ("Ul'dah - Steps of Thal" → "Ul'dah").
        var dash = place.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            return place[..dash].Trim();

        return place;
    }

    private bool TryResolveAetheryte(
        string teleportName,
        string place,
        out uint aetheryteId,
        out byte subIndex,
        out string aetheryteName)
    {
        aetheryteId = 0;
        subIndex = 0;
        aetheryteName = teleportName;

        // Match unlocked aetherytes by PlaceName (same idea as Teleporter's /tp).
        IAetheryteEntry? best = null;
        var bestScore = -1;
        for (var i = 0; i < aetheryteList.Length; i++)
        {
            var entry = aetheryteList[i];
            if (entry is null || entry.IsApartment || entry.IsSharedHouse || !entry.AetheryteData.IsValid)
                continue;

            var name = entry.AetheryteData.Value.PlaceName.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var score = Math.Max(PlaceMatchScore(teleportName, name), PlaceMatchScore(place, name));
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = entry;
            aetheryteName = name;
        }

        if (best is not null && bestScore >= 40)
        {
            aetheryteId = best.AetheryteId;
            subIndex = best.SubIndex;
            return true;
        }

        // Territory-based fallback for the original place (and hub alias territory if different).
        if (TryResolveMap(place, 0, 0, out var territoryId, out _, out _, out _, out _)
            && TryFindAetheryteInTerritory(territoryId, out aetheryteId, out subIndex, out aetheryteName))
        {
            return true;
        }

        if (TryResolveMap(teleportName, 0, 0, out territoryId, out _, out _, out _, out _)
            && TryFindAetheryteInTerritory(territoryId, out aetheryteId, out subIndex, out aetheryteName))
        {
            return true;
        }

        return false;
    }

    private bool TryFindAetheryteInTerritory(
        uint territoryId,
        out uint aetheryteId,
        out byte subIndex,
        out string aetheryteName)
    {
        aetheryteId = 0;
        subIndex = 0;
        aetheryteName = "Aetheryte";

        for (var i = 0; i < aetheryteList.Length; i++)
        {
            var entry = aetheryteList[i];
            if (entry is null || entry.IsApartment || entry.IsSharedHouse)
                continue;
            if (entry.TerritoryId != territoryId)
                continue;

            aetheryteId = entry.AetheryteId;
            subIndex = entry.SubIndex;
            if (entry.AetheryteData.IsValid)
            {
                var name = entry.AetheryteData.Value.PlaceName.Value.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    aetheryteName = name;
            }

            return aetheryteId != 0;
        }

        return false;
    }

    private bool TryResolveMap(
        string place,
        float mapX,
        float mapY,
        out uint territoryId,
        out uint mapId,
        out float resolvedX,
        out float resolvedY,
        out string placeLabel)
    {
        territoryId = 0;
        mapId = 0;
        resolvedX = mapX;
        resolvedY = mapY;
        placeLabel = place;

        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        TerritoryType? best = null;
        var bestScore = -1;

        foreach (var territory in sheet)
        {
            if (territory.RowId == 0)
                continue;

            var name = territory.PlaceName.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var score = PlaceMatchScore(place, name);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = territory;
        }

        if (best is null || bestScore < 0)
            return false;

        territoryId = best.Value.RowId;
        mapId = best.Value.Map.RowId;
        placeLabel = best.Value.PlaceName.Value.Name.ExtractText();
        if (resolvedX <= 0 || resolvedY <= 0)
        {
            resolvedX = 11f;
            resolvedY = 11f;
        }

        return mapId != 0;
    }

    private static string ExtractLocation(string vendorOrLocation)
    {
        if (string.IsNullOrWhiteSpace(vendorOrLocation))
            return string.Empty;

        var text = vendorOrLocation.Trim();
        var sep = text.IndexOf(" · ", StringComparison.Ordinal);
        if (sep >= 0 && sep + 3 < text.Length)
            return text[(sep + 3)..].Trim();
        return text;
    }

    private static int PlaceMatchScore(string needle, string haystack)
    {
        if (string.IsNullOrWhiteSpace(needle) || string.IsNullOrWhiteSpace(haystack))
            return -1;

        if (string.Equals(needle, haystack, StringComparison.OrdinalIgnoreCase))
            return 100;
        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return 80;
        if (needle.Contains(haystack, StringComparison.OrdinalIgnoreCase))
            return 60;

        foreach (var token in needle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 4)
                continue;
            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                return 40;
        }

        return -1;
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

    [GeneratedRegex(@"\(X:\s*([0-9.]+)\s*Y:\s*([0-9.]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoordPattern();
}
