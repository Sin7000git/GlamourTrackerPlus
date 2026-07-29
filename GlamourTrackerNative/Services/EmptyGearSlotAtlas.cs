using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace GlamourTracker.Services;

/// <summary>
/// Empty gear-slot silhouettes from the Character ULD spritesheet.
/// QoL Bar Icon Browser "Extra" ids (10_000_000+) map to <c>ui/uld/*.tex</c> —
/// e.g. 10000302 → <c>ui/uld/character(_hr1).tex</c> (not a real IconId / GetIconPath).
/// </summary>
internal static class EmptyGearSlotAtlas
{
    /// <summary>QoL Bar Extra sheet base (<c>TextureDictionary.FrameIconID</c>).</summary>
    public const uint QolExtraSheetBase = 10_000_000;

    /// <summary>Character paperdoll ULD sheet (QoL Extra 302).</summary>
    public const uint DefaultSheetIconId = 10000302;

    /// <summary>
    /// Baked UV layout for <see cref="DefaultSheetIconId"/> in ATK/SD pixel space
    /// (KamiToolKit strips <c>_hr1</c>; <c>LoadTextureWithDefaultVersion</c> picks HR).
    /// Tuned 2026-07-29. Order: MH, OH, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, R ring, L ring.
    /// </summary>
    private static readonly Slice[] UvBySlot =
    [
        new(0, 72, 32, 32),     // Main hand
        new(32, 72, 32, 32),    // Off hand
        new(64, 72, 32, 32),    // Head
        new(96, 72, 32, 32),    // Body
        new(128, 72, 32, 32),   // Hands
        new(192, 72, 32, 32),   // Legs
        new(0, 104, 32, 32),    // Feet
        new(32, 104, 32, 32),   // Ears
        new(64, 104, 32, 32),   // Neck
        new(96, 104, 32, 32),   // Wrists
        new(128, 104, 32, 32),  // Right ring
        new(128, 104, 32, 32),  // Left ring
    ];

    /// <summary>
    /// QoL Bar Extra sheet index → ULD texture stem (no .tex / _hr1).
    /// Matches <c>TextureDictionary.AddExtraTextures</c>.
    /// </summary>
    private static readonly Dictionary<uint, string> QolExtraSheets = new()
    {
        [10000000] = "ui/uld/icona_frame",
        [10000001] = "ui/uld/icona_recast",
        [10000002] = "ui/uld/icona_recast2",
        [10000100] = "ui/uld/achievement",
        [10000101] = "ui/uld/actionbar",
        [10000102] = "ui/uld/actioncross",
        [10000103] = "ui/uld/actionmenu",
        [10000104] = "ui/uld/adventurenotebook",
        [10000105] = "ui/uld/alarm",
        [10000106] = "ui/uld/aozbriefing",
        [10000107] = "ui/uld/aoznotebook",
        [10000108] = "ui/uld/aquariumsetting",
        [10000109] = "ui/uld/areamap",
        [10000110] = "ui/uld/armouryboard",
        [10000300] = "ui/uld/camerasettings",
        [10000301] = "ui/uld/cardtripletriad",
        [10000302] = "ui/uld/character",
        [10000303] = "ui/uld/charactergearset",
        [10000304] = "ui/uld/charamake",
        [10000305] = "ui/uld/charamake_dataimport",
        [10000306] = "ui/uld/charaselect",
        [10000307] = "ui/uld/circlebuttons",
        [10000308] = "ui/uld/circlefinder",
        [10000309] = "ui/uld/colosseumresult",
        [10000310] = "ui/uld/companycraftrecipe",
        [10000311] = "ui/uld/concentration",
        [10000312] = "ui/uld/configbackup",
        [10000313] = "ui/uld/contentsfinder",
        [10000314] = "ui/uld/contentsinfo",
        [10000315] = "ui/uld/contentsnotebook",
        [10000316] = "ui/uld/contentsreplayplayer",
        [10000317] = "ui/uld/contentsreplaysetting",
        [10000318] = "ui/uld/creditplayer",
        [10000319] = "ui/uld/cursor",
        [10000400] = "ui/uld/deepdungeonclassjob",
        [10000401] = "ui/uld/deepdungeonnavimap_ankh",
        [10000402] = "ui/uld/deepdungeonnavimap_key",
        [10000403] = "ui/uld/deepdungeonresult",
        [10000404] = "ui/uld/deepdungeonsavedata",
        [10000405] = "ui/uld/deepdungeontopmenu",
        [10000406] = "ui/uld/description",
        [10000407] = "ui/uld/dtr",
        [10000500] = "ui/uld/emjicon",
        [10000501] = "ui/uld/emjicon2",
        [10000502] = "ui/uld/emjicon3",
        [10000503] = "ui/uld/emjparts",
        [10000504] = "ui/uld/emote",
        [10000505] = "ui/uld/enemylist",
        [10000506] = "ui/uld/eurekaelementaledit",
        [10000507] = "ui/uld/eurekaelementalhud",
        [10000508] = "ui/uld/eurekalogosshardlist",
        [10000509] = "ui/uld/exp_gauge",
        [10000510] = "ui/uld/explorationdetail",
        [10000511] = "ui/uld/explorationship",
        [10000600] = "ui/uld/fashioncheck",
        [10000601] = "ui/uld/fashioncheckscoregauge",
        [10000602] = "ui/uld/fashioncheckscoregaugenum",
        [10000603] = "ui/uld/fate",
        [10000604] = "ui/uld/fishingnotebook",
        [10000605] = "ui/uld/freecompany",
        [10000700] = "ui/uld/gateresult",
        [10000701] = "ui/uld/gatherercraftericon",
        [10000702] = "ui/uld/gcarmy",
        [10000703] = "ui/uld/gcarmychangeclass",
        [10000704] = "ui/uld/gcarmychangemirageprism",
        [10000705] = "ui/uld/gcarmyclass",
        [10000706] = "ui/uld/gcarmyexpedition",
        [10000707] = "ui/uld/gcarmyexpeditionforecast",
        [10000708] = "ui/uld/gcarmyexpeditionresult",
        [10000709] = "ui/uld/gcarmymemberprofile",
        [10000710] = "ui/uld/goldsaucercarddeckedit",
        [10000800] = "ui/uld/housing",
        [10000801] = "ui/uld/housinggoods",
        [10000802] = "ui/uld/housingguestbook",
        [10000803] = "ui/uld/housingguestbook2",
        [10000804] = "ui/uld/howto",
        [10000900] = "ui/uld/iconverminion",
        [10000901] = "ui/uld/image2",
        [10000902] = "ui/uld/inventory",
        [10000903] = "ui/uld/itemdetail",
        [10001000] = "ui/uld/jobhudacn0",
        [10001001] = "ui/uld/jobhudast0",
        [10001002] = "ui/uld/jobhudblm0",
        [10001003] = "ui/uld/jobhudbrd0",
        [10001004] = "ui/uld/jobhuddnc0",
        [10001005] = "ui/uld/jobhuddrg0",
        [10001006] = "ui/uld/jobhuddrk0",
        [10001007] = "ui/uld/jobhuddrk1",
        [10001008] = "ui/uld/jobhudgnb",
        [10001009] = "ui/uld/jobhudmch0",
        [10001010] = "ui/uld/jobhudmnk1",
        [10001011] = "ui/uld/jobhudnin1",
        [10001012] = "ui/uld/jobhudpld",
        [10001013] = "ui/uld/jobhudsam1",
        [10001014] = "ui/uld/jobhudsch0",
        [10001015] = "ui/uld/jobhudsimple_stacka",
        [10001016] = "ui/uld/jobhudsimple_stackb",
        [10001017] = "ui/uld/jobhudsmn0",
        [10001018] = "ui/uld/jobhudsmn1",
        [10001019] = "ui/uld/jobhudwar",
        [10001020] = "ui/uld/jobhudwhm",
        [10001021] = "ui/uld/journal",
        [10001022] = "ui/uld/journal_detail",
        [10001201] = "ui/uld/letterlist2",
        [10001202] = "ui/uld/letterlist3",
        [10001203] = "ui/uld/letterviewer",
        [10001204] = "ui/uld/levelup2",
        [10001205] = "ui/uld/lfg",
        [10001206] = "ui/uld/linkshell",
        [10001207] = "ui/uld/lotterydaily",
        [10001208] = "ui/uld/lotteryweekly",
        [10001209] = "ui/uld/lovmheader",
        [10001210] = "ui/uld/lovmheadernum",
        [10001211] = "ui/uld/lovmpalette",
        [10001300] = "ui/uld/maincommand_icon",
        [10001301] = "ui/uld/minerbotanist",
        [10001302] = "ui/uld/minionnotebook",
        [10001303] = "ui/uld/minionnotebookykw",
        [10001304] = "ui/uld/mirageprismplate2",
        [10001400] = "ui/uld/navimap",
        [10001401] = "ui/uld/negotiation",
        [10001402] = "ui/uld/nikuaccepted",
        [10001403] = "ui/uld/numericstepperb",
        [10001500] = "ui/uld/orchestrionplaylist",
        [10001600] = "ui/uld/partyfinder",
        [10001601] = "ui/uld/performance",
        [10001602] = "ui/uld/puzzle",
        [10001603] = "ui/uld/pvpduelrequest",
        [10001604] = "ui/uld/pvprankpromotionqualifier",
        [10001605] = "ui/uld/pvpscreeninformation",
        [10001606] = "ui/uld/pvpsimulationheader2",
        [10001607] = "ui/uld/pvpsimulationmachineselect",
        [10001608] = "ui/uld/pvpteam",
        [10001800] = "ui/uld/racechocoboranking",
        [10001801] = "ui/uld/racechocoboresult",
        [10001802] = "ui/uld/readycheck",
        [10001803] = "ui/uld/recipenotebook",
        [10001804] = "ui/uld/relic2growth",
        [10001805] = "ui/uld/retainer",
        [10001806] = "ui/uld/rhythmaction",
        [10001807] = "ui/uld/rhythmactionstatus",
        [10001808] = "ui/uld/roadstone",
        [10001900] = "ui/uld/satisfactionsupplyicon",
        [10002000] = "ui/uld/teleport",
        [10002001] = "ui/uld/todolist",
        [10002002] = "ui/uld/togglebutton",
        [10002300] = "ui/uld/weeklybingo",
        [10002301] = "ui/uld/worldtransrate",
    };

    public readonly record struct Slice(ushort U, ushort V, ushort Width, ushort Height);

    /// <summary>Known QoL Extra sheet ids, ascending (for sheet pickers).</summary>
    public static IReadOnlyList<uint> KnownExtraSheetIds { get; } =
        QolExtraSheets.Keys.OrderBy(id => id).ToArray();

    public static bool TryGetExtraSheetStem(uint iconId, out string stem)
        => QolExtraSheets.TryGetValue(iconId, out stem!);

    public static Slice GetSlice(int plateSlot)
    {
        if (plateSlot is < 0 or >= GlamourPlateSlotMap.SlotCount)
            return UvBySlot[0];
        return UvBySlot[plateSlot];
    }

    /// <summary>Resolves the Character empty-slot sheet path (prefer HR).</summary>
    public static string? ResolveTexturePath(IDataManager data, ITextureProvider textures)
        => ResolveTexturePath(data, textures, DefaultSheetIconId);

    public static string? ResolveTexturePath(IDataManager data, ITextureProvider textures, uint iconId)
    {
        if (iconId == 0)
            iconId = DefaultSheetIconId;

        // QoL Bar Extra sheets (10_000_000+) → ui/uld/*.tex — NOT GetIconPath.
        if (QolExtraSheets.TryGetValue(iconId, out var stem))
        {
            var hr = stem + "_hr1.tex";
            if (data.FileExists(hr))
                return hr;
            var sd = stem + ".tex";
            if (data.FileExists(sd))
                return sd;
            return hr;
        }

        if (iconId >= QolExtraSheetBase)
        {
            PluginFileLog.Warn(
                "empty-slot.atlas",
                $"No QoL Extra sheet mapping for {iconId} (index {iconId - QolExtraSheetBase})");
            return null;
        }

        try
        {
            if (textures.TryGetIconPath(new GameIconLookup(iconId, itemHq: false, hiRes: true), out var hrPath)
                && !string.IsNullOrWhiteSpace(hrPath))
                return hrPath;

            if (textures.TryGetIconPath(new GameIconLookup(iconId, itemHq: false, hiRes: false), out var sdPath)
                && !string.IsNullOrWhiteSpace(sdPath))
                return sdPath;
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("empty-slot.atlas", $"TryGetIconPath failed for {iconId}", ex);
        }

        var folder = iconId / 1000 * 1000;
        return $"ui/icon/{folder:D6}/{iconId:D6}_hr1.tex";
    }
}
