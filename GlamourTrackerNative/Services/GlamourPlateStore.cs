using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using AgentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule;

namespace GlamourTracker.Services;

internal static unsafe class GlamourPlateStore
{
    public static void SyncFromGame(Configuration config, ulong contentId)
    {
        if (contentId == 0)
            return;

        var plates = ReadPlatesFromMirage(MirageManager.Instance());
        if (plates.Count == 0)
            plates = ReadPlatesFromAgent();

        if (plates.Count == 0)
            return;

        if (!config.CharacterCaches.TryGetValue(contentId, out var cache))
        {
            cache = new CharacterGlamourCache();
            config.CharacterCaches[contentId] = cache;
        }

        cache.GlamourPlates = plates;
        cache.LastSavedUtc = DateTime.UtcNow;
        config.Save();
    }

    public static IReadOnlyList<GlamourPlateInfo> GetPlates(
        Configuration config,
        ulong contentId,
        GlamourOwnershipIndex ownershipIndex)
    {
        var mirage = MirageManager.Instance();
        var live = ReadPlatesFromMirage(mirage);
        if (live.Count > 0)
            return BuildPlateInfos(live, ownershipIndex);

        live = ReadPlatesFromAgent();
        if (live.Count > 0)
            return BuildPlateInfos(live, ownershipIndex);

        if (contentId == 0
            || !config.CharacterCaches.TryGetValue(contentId, out var cache)
            || cache.GlamourPlates.Count == 0)
            return [];

        return BuildPlateInfos(cache.GlamourPlates, ownershipIndex);
    }

    private static List<StoredGlamourPlate> ReadPlatesFromMirage(MirageManager* mirage)
    {
        if (mirage == null)
            return [];

        var stored = new List<StoredGlamourPlate>();

        for (var i = 0; i < mirage->GlamourPlates.Length; i++)
        {
            ref var plate = ref mirage->GlamourPlates[i];
            var pieces = ReadMiragePlatePieces(ref plate);
            if (pieces.Count > 0)
                stored.Add(new StoredGlamourPlate { PlateIndex = i + 1, Pieces = pieces });
        }

        return stored;
    }

    private static List<StoredGlamourPlate> ReadPlatesFromAgent()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return [];

        var agent = (AgentMiragePrismMiragePlate*)agentModule->GetAgentByInternalId(AgentId.MiragePrismMiragePlate);
        if (agent == null || agent->Data == null)
            return [];

        var stored = new List<StoredGlamourPlate>();

        for (var i = 0; i < agent->Data->GlamourPlates.Length; i++)
        {
            ref var plate = ref agent->Data->GlamourPlates[i];
            var pieces = ReadAgentPlatePieces(ref plate);
            if (pieces.Count > 0)
                stored.Add(new StoredGlamourPlate { PlateIndex = i + 1, Pieces = pieces });
        }

        return stored;
    }

    private static List<StoredGlamourPlatePiece> ReadMiragePlatePieces(ref MirageManager.GlamourPlate plate)
    {
        var pieces = new List<StoredGlamourPlatePiece>();

        for (var slot = 0; slot < plate.ItemIds.Length; slot++)
        {
            var itemId = plate.ItemIds[slot];
            if (itemId == 0)
                continue;

            pieces.Add(new StoredGlamourPlatePiece { Slot = slot, ItemId = itemId });
        }

        return pieces;
    }

    private static List<StoredGlamourPlatePiece> ReadAgentPlatePieces(ref AgentMiragePrismMiragePlateData.GlamourPlate plate)
    {
        var pieces = new List<StoredGlamourPlatePiece>();

        for (var slot = 0; slot < plate.Items.Length; slot++)
        {
            ref var item = ref plate.Items[slot];
            if (item.ItemId == 0)
                continue;

            pieces.Add(new StoredGlamourPlatePiece { Slot = slot, ItemId = item.ItemId });
        }

        return pieces;
    }

    private static IReadOnlyList<GlamourPlateInfo> BuildPlateInfos(
        List<StoredGlamourPlate> storedPlates,
        GlamourOwnershipIndex ownershipIndex)
    {
        var plates = new List<GlamourPlateInfo>();

        foreach (var stored in storedPlates.OrderBy(p => p.PlateIndex))
        {
            var pieces = stored.Pieces
                .OrderBy(p => p.Slot)
                .Select(p => new GlamourPlatePieceInfo(
                    p.Slot,
                    p.ItemId,
                    ownershipIndex.GetStorage(p.ItemId)))
                .ToList();

            if (pieces.Count > 0)
                plates.Add(new GlamourPlateInfo(stored.PlateIndex, pieces));
        }

        return plates;
    }
}

internal sealed record GlamourPlateInfo(int PlateIndex, IReadOnlyList<GlamourPlatePieceInfo> Pieces);

internal readonly record struct GlamourPlatePieceInfo(int Slot, uint ItemId, GlamourStorageLocation Storage);
