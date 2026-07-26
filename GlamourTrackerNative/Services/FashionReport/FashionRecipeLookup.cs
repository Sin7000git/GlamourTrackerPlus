using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services.FashionReport;

internal sealed class FashionRecipeLookup
{
    private readonly IDataManager dataManager;
    private Dictionary<uint, ushort>? recipeByResultItem;

    public FashionRecipeLookup(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public bool TryGetRecipeId(uint itemId, out ushort recipeId)
    {
        EnsureIndex();
        if (recipeByResultItem!.TryGetValue(itemId, out recipeId))
            return true;

        recipeId = 0;
        return false;
    }

    private void EnsureIndex()
    {
        if (recipeByResultItem != null)
            return;

        var map = new Dictionary<uint, ushort>();
        foreach (var recipe in dataManager.GetExcelSheet<Recipe>())
        {
            if (recipe.RowId == 0)
                continue;

            var resultId = recipe.ItemResult.RowId;
            if (resultId == 0)
                continue;

            // Prefer the first listed recipe for each result (usual main craft path).
            if (!map.ContainsKey(resultId) && recipe.RowId <= ushort.MaxValue)
                map[resultId] = (ushort)recipe.RowId;
        }

        recipeByResultItem = map;
        PluginFileLog.Info("fashion.recipes", $"recipe index built ({map.Count} results)");
    }
}
