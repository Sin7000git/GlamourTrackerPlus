using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Windows;

namespace GlamourTracker.Services.FashionReport;

/// <summary>
/// Intercepts Masked Rose Fashion Report judging when no VIP Card / Jackpot III buff is active,
/// so the player can cancel before an allowance is spent.
/// </summary>
internal sealed unsafe class FashionReportMgpReminderService : IDisposable
{
    private const uint MaskedRoseBaseId = 1025176;

    private readonly Func<Configuration> getConfig;
    private readonly FashionMgpBuffService mgpBuff;
    private readonly FashionReportProgressTracker progress;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Hook<AtkUnitBase.Delegates.FireCallback>? fireCallbackHook;

    private FashionMgpReminderAddon? confirmAddon;
    private bool promptOpen;
    private bool allowNextSelect;
    private int pendingOptionIndex = -1;
    private bool loggedMenuOnce;

    private VipAssistPhase vipPhase = VipAssistPhase.Idle;
    private int vipTicksLeft;
    private int vipCardCountBefore;
    private int vipUseRetries;

    private enum VipAssistPhase : byte
    {
        Idle,
        WaitClear,
        SendUse,
        WaitConfirm,
        Retalk,
    }

    public FashionReportMgpReminderService(
        Func<Configuration> getConfig,
        FashionMgpBuffService mgpBuff,
        FashionReportProgressTracker progress,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        ITargetManager targetManager,
        IObjectTable objectTable,
        ICondition condition,
        IFramework framework,
        IChatGui chatGui,
        IGameInteropProvider gameInterop,
        IPluginLog log)
    {
        this.getConfig = getConfig;
        this.mgpBuff = mgpBuff;
        this.progress = progress;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.condition = condition;
        this.framework = framework;
        this.chatGui = chatGui;
        this.log = log;

        try
        {
            fireCallbackHook = gameInterop.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
                AtkUnitBase.Addresses.FireCallback.Value,
                OnFireCallback);
            fireCallbackHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not hook SelectString for Fashion Report MGP reminder.");
            PluginFileLog.Error("fashion.mgp", "FireCallback hook failed for MGP reminder", ex);
        }

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectString", OnSelectStringSetup);

        confirmAddon = new FashionMgpReminderAddon(
            OnContinue,
            OnCancel,
            OnUseVip,
            () => mgpBuff.GetView())
        {
            InternalName = "GlamMgpRemind",
            Title = "Fashion Report",
            Size = new System.Numerics.Vector2(480f, 160f),
            RememberClosePosition = false,
        };
    }

    public void Dispose()
    {
        vipPhase = VipAssistPhase.Idle;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectString", OnSelectStringSetup);
        fireCallbackHook?.Disable();
        fireCallbackHook?.Dispose();
        confirmAddon?.Dispose();
        confirmAddon = null;
    }

    private void OnSelectStringSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!IsMaskedRoseTargeted())
                return;

            var addon = (AddonSelectString*)args.Addon.Address;
            if (addon == null)
                return;

            var entries = ReadEntries(addon);
            if (entries.Count == 0)
                return;

            if (!loggedMenuOnce)
            {
                loggedMenuOnce = true;
                PluginFileLog.Info("fashion.mgp", $"Masked Rose menu: {string.Join(" | ", entries)}");
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Masked Rose SelectString setup log failed.");
        }
    }

    private bool OnFireCallback(AtkUnitBase* thisPtr, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            if (allowNextSelect)
            {
                allowNextSelect = false;
                return fireCallbackHook!.Original(thisPtr, valueCount, values, close);
            }

            if (thisPtr == null
                || valueCount == 0
                || values == null
                || thisPtr->NameString is not "SelectString")
            {
                return fireCallbackHook!.Original(thisPtr, valueCount, values, close);
            }

            var index = values[0].Type is AtkValueType.Int or AtkValueType.UInt
                ? values[0].Int
                : int.MinValue;
            if (index < 0)
                return fireCallbackHook!.Original(thisPtr, valueCount, values, close);

            if (!ShouldPromptForOption((AddonSelectString*)thisPtr, index))
                return fireCallbackHook!.Original(thisPtr, valueCount, values, close);

            if (promptOpen)
                return true;

            pendingOptionIndex = index;
            promptOpen = true;
            PluginFileLog.Info("fashion.mgp", $"Prompting before Fashion Report judging (option={index})");
            _ = framework.RunOnFrameworkThread(OpenConfirm);
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Fashion Report MGP reminder FireCallback failed.");
            PluginFileLog.Warn("fashion.mgp", $"FireCallback intercept failed: {ex.Message}");
            return fireCallbackHook!.Original(thisPtr, valueCount, values, close);
        }
    }

    private bool ShouldPromptForOption(AddonSelectString* addon, int index)
    {
        var config = getConfig();
        if (!config.Enabled || !config.RemindFashionReportMgpBuff)
            return false;

        if (!IsMaskedRoseTargeted())
            return false;

        // Don't re-prompt while we're mid Use-VIP → retalk assist.
        if (vipPhase != VipAssistPhase.Idle)
            return false;

        var view = progress.GetProgress();
        if (view.Kind is FashionReportProgressKind.Unavailable)
            return false;
        if (view.Kind is not FashionReportProgressKind.Unknown && view.AllowancesRemaining <= 0)
            return false;

        if (mgpBuff.HasActiveFashionMgpBonus())
            return false;

        if (addon == null)
            return false;

        var entries = ReadEntries(addon);
        if (index < 0 || index >= entries.Count)
            return false;

        return IsJudgingOption(entries[index]);
    }

    private void OpenConfirm()
    {
        try
        {
            confirmAddon?.Open();
        }
        catch (Exception ex)
        {
            promptOpen = false;
            pendingOptionIndex = -1;
            PluginFileLog.Error("fashion.mgp", "Failed to open MGP reminder window", ex);
        }
    }

    private void OnContinue()
    {
        var index = pendingOptionIndex;
        pendingOptionIndex = -1;
        promptOpen = false;

        PluginFileLog.Info("fashion.mgp", $"Player continued Fashion Report without MGP buff (option={index})");

        if (index < 0)
            return;

        _ = framework.RunOnFrameworkThread(() => FireSelectString(index, warnIfMissing: true));
    }

    private void OnCancel()
    {
        pendingOptionIndex = -1;
        promptOpen = false;
        PluginFileLog.Info("fashion.mgp", "Player cancelled Fashion Report judging (no MGP buff)");
        _ = framework.RunOnFrameworkThread(DismissMaskedRoseDialogue);
    }

    /// <summary>
    /// Leave Masked Rose, wait until free, use VIP Card (verified), then open talk again
    /// without picking a menu option.
    /// </summary>
    private void OnUseVip()
    {
        pendingOptionIndex = -1;
        promptOpen = false;
        vipCardCountBefore = mgpBuff.GetVipCardCount();
        vipUseRetries = 8;
        vipTicksLeft = 120;
        vipPhase = VipAssistPhase.WaitClear;
        PluginFileLog.Info("fashion.mgp", $"Closing Masked Rose to use VIP Card (have={vipCardCountBefore})");
        _ = framework.RunOnFrameworkThread(() =>
        {
            DismissMaskedRoseDialogue();
            TickVipAssist();
        });
    }

    private void TickVipAssist()
    {
        if (vipPhase == VipAssistPhase.Idle)
            return;

        try
        {
            switch (vipPhase)
            {
                case VipAssistPhase.WaitClear:
                    if (IsPlayerFreeForItemUse())
                    {
                        vipPhase = VipAssistPhase.SendUse;
                        PluginFileLog.Info("fashion.mgp", "Dialogue clear; sending VIP Card use");
                        TickVipAssist();
                        return;
                    }

                    if (--vipTicksLeft <= 0)
                    {
                        FailVipAssist("timed out waiting for Masked Rose dialogue to end");
                        return;
                    }

                    break;

                case VipAssistPhase.SendUse:
                    if (!IsPlayerFreeForItemUse())
                    {
                        vipPhase = VipAssistPhase.WaitClear;
                        break;
                    }

                    if (!mgpBuff.TrySendVipCardUse(out var sendDetail, printErrors: false))
                    {
                        if (--vipUseRetries <= 0)
                        {
                            FailVipAssist($"could not send VIP Card use ({sendDetail})");
                            return;
                        }

                        break;
                    }

                    vipPhase = VipAssistPhase.WaitConfirm;
                    vipTicksLeft = 45;
                    break;

                case VipAssistPhase.WaitConfirm:
                    if (mgpBuff.IsVipUseConfirmed(vipCardCountBefore))
                    {
                        chatGui.Print("[Glamour Tracker+] Using Gold Saucer VIP Card.");
                        PluginFileLog.Info(
                            "fashion.mgp",
                            $"VIP Card confirmed; remaining={mgpBuff.GetVipCardCount()}");
                        vipPhase = VipAssistPhase.Retalk;
                        vipTicksLeft = 45;
                        vipUseRetries = 10;
                        TickVipAssist();
                        return;
                    }

                    // Still free but not confirmed — resend a few times (false UseItem success).
                    if (IsPlayerFreeForItemUse() && vipUseRetries > 0 && vipTicksLeft > 0 && vipTicksLeft % 8 == 0)
                    {
                        vipUseRetries--;
                        _ = mgpBuff.TrySendVipCardUse(out _, printErrors: false);
                    }

                    if (--vipTicksLeft <= 0)
                    {
                        FailVipAssist("VIP Card use did not apply (still no buff / card count unchanged)");
                        return;
                    }

                    break;

                case VipAssistPhase.Retalk:
                    if (TryInteractMaskedRose())
                    {
                        PluginFileLog.Info("fashion.mgp", "Re-opened Masked Rose talk after VIP Card");
                        vipPhase = VipAssistPhase.Idle;
                        return;
                    }

                    if (--vipTicksLeft <= 0 || --vipUseRetries <= 0)
                    {
                        PluginFileLog.Warn("fashion.mgp", "Could not re-open Masked Rose; talk to him manually");
                        chatGui.Print("[Glamour Tracker+] VIP Card used — talk to the Masked Rose when ready.");
                        vipPhase = VipAssistPhase.Idle;
                        return;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            FailVipAssist(ex.Message);
            PluginFileLog.Error("fashion.mgp", "VIP assist tick failed", ex);
            return;
        }

        if (vipPhase != VipAssistPhase.Idle)
            _ = framework.RunOnTick(TickVipAssist);
    }

    private void FailVipAssist(string reason)
    {
        vipPhase = VipAssistPhase.Idle;
        PluginFileLog.Warn("fashion.mgp", $"VIP assist failed: {reason}");
        chatGui.PrintError($"[Glamour Tracker+] {reason}.");
    }

    private bool IsPlayerFreeForItemUse()
    {
        if (IsDialogueAddonVisible("SelectString") || IsDialogueAddonVisible("Talk"))
            return false;

        if (condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.Occupied]
            || condition[ConditionFlag.Occupied30]
            || condition[ConditionFlag.Occupied33]
            || condition[ConditionFlag.Occupied38])
            return false;

        return true;
    }

    private bool TryInteractMaskedRose()
    {
        var rose = FindMaskedRose();
        if (rose == null || !rose.IsTargetable)
            return false;

        targetManager.Target = rose;
        var go = (GameObject*)rose.Address;
        var ts = TargetSystem.Instance();
        if (ts == null)
            return false;

        if (ts->InteractWithObject(go, false) != 0)
            return true;
        return ts->InteractWithObject(go, true) != 0;
    }

    private IGameObject? FindMaskedRose()
    {
        if (targetManager.Target is { } target && target.BaseId == MaskedRoseBaseId)
            return target;

        foreach (var obj in objectTable)
        {
            if (obj != null && obj.BaseId == MaskedRoseBaseId)
                return obj;
        }

        return null;
    }

    private void DismissMaskedRoseDialogue()
    {
        // Prefer the real "Leave." line so the event ends cleanly; fall back to cancel.
        var addon = (AddonSelectString*)gameGui.GetAddonByName("SelectString", 1).Address;
        var leaveIndex = -1;
        if (addon != null && addon->IsVisible)
        {
            var entries = ReadEntries(addon);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Contains("leave", StringComparison.OrdinalIgnoreCase))
                {
                    leaveIndex = i;
                    break;
                }
            }
        }

        FireSelectString(leaveIndex >= 0 ? leaveIndex : -1, warnIfMissing: false);
    }

    private void FireSelectString(int index, bool warnIfMissing = true)
    {
        try
        {
            var addon = (AtkUnitBase*)gameGui.GetAddonByName("SelectString", 1).Address;
            if (addon == null || !addon->IsVisible)
            {
                if (warnIfMissing)
                    PluginFileLog.Warn("fashion.mgp", "SelectString gone; cannot finish deferred choice");
                return;
            }

            allowNextSelect = true;
            var values = stackalloc AtkValue[1];
            values[0].Type = AtkValueType.Int;
            values[0].Int = index;
            addon->FireCallback(1, values, true);
        }
        catch (Exception ex)
        {
            allowNextSelect = false;
            PluginFileLog.Error("fashion.mgp", $"Deferred SelectString fire failed (index={index})", ex);
        }
    }

    private bool IsDialogueAddonVisible(string name)
    {
        var addon = gameGui.GetAddonByName(name, 1);
        return !addon.IsNull && addon.IsVisible;
    }

    private bool IsMaskedRoseTargeted()
    {
        var target = targetManager.Target;
        return target != null && target.BaseId == MaskedRoseBaseId;
    }

    private static List<string> ReadEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        if (addon == null)
            return list;

        var count = addon->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var namePtr = addon->PopupMenu.PopupMenu.EntryNames[i].Value;
            if (namePtr == null)
            {
                list.Add(string.Empty);
                continue;
            }

            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)namePtr).TextValue);
        }

        return list;
    }

    /// <summary>
    /// Match the turn-in / judging option. Theme-only lines ("Confirm this week's fashion challenge")
    /// must not prompt — they do not spend an allowance.
    /// </summary>
    internal static bool IsJudgingOption(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return false;

        if (label.Contains("about", StringComparison.OrdinalIgnoreCase))
            return false;

        if (label.Contains("challenge", StringComparison.OrdinalIgnoreCase)
            && (label.Contains("confirm", StringComparison.OrdinalIgnoreCase)
                || label.Contains("week", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (label.Contains("judging", StringComparison.OrdinalIgnoreCase)
            || label.Contains("present yourself", StringComparison.OrdinalIgnoreCase)
            || label.Contains("undergo", StringComparison.OrdinalIgnoreCase))
            return true;

        return label.Contains("fashion report", StringComparison.OrdinalIgnoreCase)
            && (label.Contains("present", StringComparison.OrdinalIgnoreCase)
                || label.Contains("submit", StringComparison.OrdinalIgnoreCase));
    }
}
