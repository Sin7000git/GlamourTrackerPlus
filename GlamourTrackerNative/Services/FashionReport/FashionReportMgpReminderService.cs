using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
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
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<AtkUnitBase.Delegates.FireCallback>? fireCallbackHook;

    private FashionMgpReminderAddon? confirmAddon;
    private bool promptOpen;
    private bool allowNextSelect;
    private int pendingOptionIndex = -1;
    private bool loggedMenuOnce;

    public FashionReportMgpReminderService(
        Func<Configuration> getConfig,
        FashionMgpBuffService mgpBuff,
        FashionReportProgressTracker progress,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        ITargetManager targetManager,
        IFramework framework,
        IGameInteropProvider gameInterop,
        IPluginLog log)
    {
        this.getConfig = getConfig;
        this.mgpBuff = mgpBuff;
        this.progress = progress;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.framework = framework;
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
            {
                // Already asking — swallow duplicate clicks.
                return true;
            }

            pendingOptionIndex = index;
            promptOpen = true;
            PluginFileLog.Info("fashion.mgp", $"Prompting before Fashion Report judging (option={index})");
            _ = framework.RunOnFrameworkThread(OpenConfirm);
            return true; // swallow — do not spend the allowance yet
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
    /// VIP Card can't be used while talking to Masked Rose — dismiss the menu first, then use.
    /// </summary>
    private void OnUseVip()
    {
        pendingOptionIndex = -1;
        promptOpen = false;
        PluginFileLog.Info("fashion.mgp", "Closing Masked Rose to use VIP Card");
        _ = framework.RunOnFrameworkThread(() =>
        {
            DismissMaskedRoseDialogue();
            ScheduleVipUseAfterDialogue(attemptsLeft: 45);
        });
    }

    private void ScheduleVipUseAfterDialogue(int attemptsLeft)
    {
        if (!IsDialogueAddonVisible("SelectString") && !IsDialogueAddonVisible("Talk"))
        {
            mgpBuff.TryUseVipCard();
            return;
        }

        if (attemptsLeft <= 0)
        {
            PluginFileLog.Warn("fashion.mgp", "Dialogue still open; trying VIP Card anyway");
            mgpBuff.TryUseVipCard();
            return;
        }

        _ = framework.RunOnTick(() => ScheduleVipUseAfterDialogue(attemptsLeft - 1));
    }

    private void DismissMaskedRoseDialogue()
    {
        // -1 cancels SelectString without picking a line (no allowance spend).
        FireSelectString(-1, warnIfMissing: false);
        CloseAddonIfVisible("Talk");
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

    private void CloseAddonIfVisible(string name)
    {
        try
        {
            var addon = (AtkUnitBase*)gameGui.GetAddonByName(name, 1).Address;
            if (addon == null || !addon->IsVisible)
                return;
            addon->FireCallback(0, null, true);
        }
        catch (Exception ex)
        {
            log.Debug(ex, $"Could not close {name} before VIP Card use.");
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

        // Theme / clue option — English client wording from community guides.
        if (label.Contains("challenge", StringComparison.OrdinalIgnoreCase)
            && (label.Contains("confirm", StringComparison.OrdinalIgnoreCase)
                || label.Contains("week", StringComparison.OrdinalIgnoreCase)))
            return false;

        // English client (logged once): "Present yourself for judging."
        if (label.Contains("judging", StringComparison.OrdinalIgnoreCase)
            || label.Contains("present yourself", StringComparison.OrdinalIgnoreCase)
            || label.Contains("undergo", StringComparison.OrdinalIgnoreCase))
            return true;

        // Broader fallback when localization differs but still names Fashion Report + present/submit.
        return label.Contains("fashion report", StringComparison.OrdinalIgnoreCase)
            && (label.Contains("present", StringComparison.OrdinalIgnoreCase)
                || label.Contains("submit", StringComparison.OrdinalIgnoreCase));
    }
}
