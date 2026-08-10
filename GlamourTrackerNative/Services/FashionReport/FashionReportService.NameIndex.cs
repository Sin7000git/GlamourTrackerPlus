using Lumina.Excel.Sheets;
using GlamourTracker;

namespace GlamourTracker.Services.FashionReport;

internal sealed partial class FashionReportService
{
    private void EnsureItemNameIndex()
    {
        if (itemNameToId != null)
            return;

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var sheet = dataManager.GetExcelSheet<Item>();
        foreach (var item in sheet)
        {
            if (item.RowId == 0)
                continue;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!map.ContainsKey(name))
                map[name] = item.RowId;
        }

        itemNameToId = map;
        PluginFileLog.Info("fashion.index", $"item name index built ({map.Count} names)");
    }

    private uint LookupItemId(string name)
    {
        EnsureItemNameIndex();
        return itemNameToId!.TryGetValue(name, out var id) ? id : 0;
    }

    private static string LabelForSlot(string slot) =>
        SlotLabels.TryGetValue(slot, out var label) ? label : slot;
}
