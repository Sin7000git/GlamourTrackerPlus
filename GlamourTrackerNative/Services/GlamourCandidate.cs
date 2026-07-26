using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourTracker.Services;

internal readonly record struct GlamourCandidate(
    AgentMiragePrismMiragePlateData.ItemSource Source,
    uint SourceId,
    uint ItemId,
    byte Stain0Id,
    byte Stain1Id,
    uint EquipSlotCategory);
