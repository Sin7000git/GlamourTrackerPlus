using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlamourTracker.Services;

internal static unsafe class GlamourPlateCatalog
{
    public static IReadOnlyList<GlamourPlateInfo> BuildFromMirage(
        MirageManager* mirage,
        GlamourOwnershipIndex ownershipIndex)
    {
        var plates = new List<GlamourPlateInfo>();

        for (var i = 0; i < mirage->GlamourPlates.Length; i++)
        {
            ref var plate = ref mirage->GlamourPlates[i];
            var pieces = new List<GlamourPlatePieceInfo>();

            for (var slot = 0; slot < plate.ItemIds.Length; slot++)
            {
                var itemId = plate.ItemIds[slot];
                if (itemId == 0)
                    continue;

                pieces.Add(new GlamourPlatePieceInfo(
                    slot,
                    itemId,
                    ownershipIndex.GetStorage(itemId)));
            }

            if (pieces.Count > 0)
                plates.Add(new GlamourPlateInfo(i + 1, pieces));
        }

        return plates;
    }
}

internal sealed record GlamourPlateInfo(int PlateIndex, IReadOnlyList<GlamourPlatePieceInfo> Pieces);

internal readonly record struct GlamourPlatePieceInfo(int Slot, uint ItemId, GlamourStorageLocation Storage);
