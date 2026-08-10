using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace GlamourTracker.Services.FashionReport;

internal enum FashionMgpBuffState
{
    Ready,
    OutOfCards,
    VipActive,
    JackpotIiiActive,
}

internal readonly record struct FashionMgpBuffView(
    FashionMgpBuffState State,
    int CardCount,
    uint IconId,
    string ButtonLabel,
    string Tooltip,
    bool CanUse);

/// <summary>Gold Saucer VIP Card inventory + Jackpot / VIP buff detection.</summary>
internal sealed class FashionMgpBuffService
{
    internal const uint VipCardItemId = 14947;
    internal const uint VipCardStatusId = 1079;
    internal const uint JackpotStatusId = 902;

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly HashSet<uint> jackpotIiiCompanyActionIds = [];
    private uint vipIconId;
    private bool companyActionsResolved;
    private bool loggedJackpotParam;

    public FashionMgpBuffService(
        IDataManager dataManager,
        IObjectTable objectTable,
        IFramework framework,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.framework = framework;
        this.chatGui = chatGui;
        this.log = log;
    }

    /// <summary>True when VIP Card or Jackpot III is already giving +15% MGP.</summary>
    public bool HasActiveFashionMgpBonus()
    {
        EnsureCompanyActionsResolved();
        return DetectActiveMgpBonus() is FashionMgpBuffState.VipActive or FashionMgpBuffState.JackpotIiiActive;
    }

    public FashionMgpBuffView GetView()
    {
        EnsureCompanyActionsResolved();
        if (vipIconId == 0 && dataManager.GetExcelSheet<Item>().TryGetRow(VipCardItemId, out var item))
            vipIconId = item.Icon;

        var count = CountVipCards();
        var vipKind = DetectActiveMgpBonus();

        if (vipKind == FashionMgpBuffState.VipActive)
        {
            return new FashionMgpBuffView(
                FashionMgpBuffState.VipActive,
                count,
                vipIconId,
                count > 0 ? $"VIP Card running · ×{count}" : "VIP Card running",
                "Gold Saucer VIP Card is already providing +15% MGP.",
                CanUse: false);
        }

        if (vipKind == FashionMgpBuffState.JackpotIiiActive)
        {
            return new FashionMgpBuffView(
                FashionMgpBuffState.JackpotIiiActive,
                count,
                vipIconId,
                count > 0 ? $"Jackpot III applied · ×{count}" : "Jackpot III applied",
                "Jackpot III already gives +15% MGP. VIP Card won't stack with it.",
                CanUse: false);
        }

        if (count <= 0)
        {
            return new FashionMgpBuffView(
                FashionMgpBuffState.OutOfCards,
                0,
                vipIconId,
                "Out of VIP Cards",
                "No Gold Saucer VIP Cards in your inventory.\n"
                + "Earn them from squadron missions (Black Market Crackdown / Counter-magitek Exercises).",
                CanUse: false);
        }

        return new FashionMgpBuffView(
            FashionMgpBuffState.Ready,
            count,
            vipIconId,
            $"Use Gold Saucer VIP Card ×{count}",
            "Use a Gold Saucer VIP Card for +15% MGP for 120 minutes.",
            CanUse: true);
    }

    public void TryUseVipCard()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (!TrySendVipCardUse(out var detail, printErrors: true))
                PluginFileLog.Warn("fashion.mgp", $"VIP Card use rejected ({detail})");
        });
    }

    public int GetVipCardCount() => CountVipCards();

    /// <summary>
    /// Sends a use-item command. Callers must confirm with <see cref="IsVipUseConfirmed"/> —
    /// UseItem can return success while OccupiedInEvent without consuming a card.
    /// </summary>
    public unsafe bool TrySendVipCardUse(out string detail, bool printErrors = false)
    {
        detail = string.Empty;
        try
        {
            var view = GetView();
            if (!view.CanUse)
            {
                detail = view.ButtonLabel;
                if (printErrors)
                    chatGui.PrintError($"[Glamour Tracker+] {view.ButtonLabel}.");
                return false;
            }

            var agent = AgentInventoryContext.Instance();
            if (agent != null)
            {
                var result = agent->UseItem(VipCardItemId);
                if (result == 0)
                {
                    detail = "UseItem sent";
                    PluginFileLog.Info("fashion.mgp", "VIP Card UseItem sent");
                    return true;
                }

                PluginFileLog.Info("fashion.mgp", $"UseItem result={result}; trying ActionManager");
            }

            var am = ActionManager.Instance();
            if (am != null && am->UseAction(ActionType.Item, VipCardItemId, 0xE000_0000, ushort.MaxValue))
            {
                detail = "UseAction sent";
                PluginFileLog.Info("fashion.mgp", "VIP Card UseAction sent");
                return true;
            }

            detail = "UseItem/UseAction rejected";
            if (printErrors)
                chatGui.PrintError("[Glamour Tracker+] Could not use the Gold Saucer VIP Card.");
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            PluginFileLog.Error("fashion.mgp", "VIP Card use failed", ex);
            if (printErrors)
                chatGui.PrintError("[Glamour Tracker+] Could not use the Gold Saucer VIP Card.");
            log.Warning(ex, "VIP Card use failed");
            return false;
        }
    }

    /// <summary>True when a card was consumed or any VIP/Jackpot MGP status is present.</summary>
    public bool IsVipUseConfirmed(int cardCountBefore) =>
        CountVipCards() < cardCountBefore
        || HasActiveFashionMgpBonus()
        || HasAnyMgpBonusStatus();

    /// <summary>Raw status check — buff can appear before Param/tier heuristics settle.</summary>
    private bool HasAnyMgpBonusStatus()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return false;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == VipCardStatusId || status.StatusId == JackpotStatusId)
                return true;
        }

        return false;
    }

    private unsafe int CountVipCards()
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return 0;
        return inv->GetInventoryItemCount(VipCardItemId, false);
    }

    /// <summary>
    /// VIP Card uses status 1079 (or a short Jackpot III). FC Jackpot I–III share status 902;
    /// tier is inferred from Param / CompanyAction row / remaining time.
    /// </summary>
    private FashionMgpBuffState? DetectActiveMgpBonus()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return null;

        const float vipDurationSeconds = 2.5f * 60f * 60f; // VIP is 120m; FC actions are 24h

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == VipCardStatusId)
                return FashionMgpBuffState.VipActive;

            if (status.StatusId != JackpotStatusId)
                continue;

            var param = (uint)status.Param;
            var remaining = status.RemainingTime;
            if (!loggedJackpotParam)
            {
                loggedJackpotParam = true;
                PluginFileLog.Info(
                    "fashion.mgp",
                    $"Jackpot status param={param} remaining={remaining:0}s companyIii=[{string.Join(',', jackpotIiiCompanyActionIds)}]");
            }

            // VIP Card applies a 2h Jackpot III; FC actions last 24h.
            if (remaining > 0 && remaining <= vipDurationSeconds
                && (param == 0 || IsJackpotIiiParam(param)))
                return FashionMgpBuffState.VipActive;

            if (IsJackpotIiiParam(param))
                return FashionMgpBuffState.JackpotIiiActive;

            // Unknown Param on a long FC Jackpot — allow VIP (Jackpot I/II override).
        }

        return null;
    }

    private bool IsJackpotIiiParam(uint param) =>
        param is 3 or 15
        || jackpotIiiCompanyActionIds.Contains(param);

    private void EnsureCompanyActionsResolved()
    {
        if (companyActionsResolved)
            return;
        companyActionsResolved = true;

        try
        {
            // CompanyAction sheet name varies by Lumina generation; resolve via reflection-safe TryGet.
            if (!TryLoadCompanyActionJackpotIii())
            {
                // Known historical row ids for Jackpot III (safe extras; Param match is what matters).
                jackpotIiiCompanyActionIds.Add(23);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not resolve CompanyAction Jackpot III ids.");
        }
    }

    private bool TryLoadCompanyActionJackpotIii()
    {
        // Lumina.Excel.Sheets.CompanyAction exists on current Dalamud packs.
        try
        {
            var sheet = dataManager.GetExcelSheet<CompanyAction>();
            var found = false;
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (name.Equals("Jackpot III", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Jackpot III", StringComparison.OrdinalIgnoreCase))
                {
                    jackpotIiiCompanyActionIds.Add(row.RowId);
                    found = true;
                }
            }

            if (found)
                PluginFileLog.Info("fashion.mgp", $"Jackpot III company actions: {string.Join(',', jackpotIiiCompanyActionIds)}");
            return found;
        }
        catch
        {
            return false;
        }
    }
}
