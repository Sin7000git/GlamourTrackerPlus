namespace GlamourTracker;

[Serializable]
public sealed class StoredGlamourPlate
{
    public int PlateIndex { get; set; }
    public List<StoredGlamourPlatePiece> Pieces { get; set; } = [];
}

[Serializable]
public sealed class StoredGlamourPlatePiece
{
    public int Slot { get; set; }
    public uint ItemId { get; set; }
}
