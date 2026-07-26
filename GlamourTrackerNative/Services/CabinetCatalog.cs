using System.Collections.Frozen;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services;

internal sealed class CabinetCatalog
{
    private FrozenDictionary<uint, uint> itemToCabinetRow = FrozenDictionary<uint, uint>.Empty;
    private FrozenDictionary<uint, uint> cabinetToItem = FrozenDictionary<uint, uint>.Empty;

    public void Build(IDataManager dataManager)
    {
        var itemToCabinet = new Dictionary<uint, uint>();
        var cabinetToItemMap = new Dictionary<uint, uint>();

        foreach (var row in dataManager.GetExcelSheet<Cabinet>())
        {
            var itemId = row.Item.RowId;
            if (itemId == 0)
                continue;

            itemToCabinet[itemId] = row.RowId;
            cabinetToItemMap[row.RowId] = itemId;
        }

        this.itemToCabinetRow = itemToCabinet.ToFrozenDictionary();
        this.cabinetToItem = cabinetToItemMap.ToFrozenDictionary();
    }

    public bool TryGetCabinetRow(uint itemId, out uint cabinetRowId) =>
        this.itemToCabinetRow.TryGetValue(ItemIdHelper.Normalize(itemId), out cabinetRowId);

    public bool IsArmoireEligible(uint itemId) =>
        this.itemToCabinetRow.ContainsKey(ItemIdHelper.Normalize(itemId));

    public IReadOnlyDictionary<uint, uint> CabinetToItem => this.cabinetToItem;
}
