using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourTracker.Services;

internal readonly record struct GlamourCandidate(
    AgentMiragePrismMiragePlateData.ItemSource Source,
    uint SourceId,
    uint ItemId,
    byte Stain0Id,
    byte Stain1Id,
    uint EquipSlotCategory);

/// <summary>
/// Identifies one wearable thing on a plate. A stored outfit shares its dresser slot with up to
/// eleven pieces, so the slot alone cannot say which of them is already spoken for.
/// </summary>
internal readonly record struct GlamourCandidateKey(
    AgentMiragePrismMiragePlateData.ItemSource Source,
    uint SourceId,
    uint ItemId)
{
    public static GlamourCandidateKey For(
        AgentMiragePrismMiragePlateData.ItemSource source,
        uint sourceId,
        uint itemId) =>
        new(source, sourceId, ItemIdHelper.GlamourBaseId(itemId));
}
