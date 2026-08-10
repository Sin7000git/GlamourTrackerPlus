using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Windows.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace GlamourTracker.Windows;

/// <summary>Yes/No prompt before Fashion Report judging without an MGP bonus buff.</summary>
internal sealed class FashionMgpReminderAddon : NativeAddon
{
    private readonly Action onContinue;
    private readonly Action onCancel;
    private bool resolved;

    public FashionMgpReminderAddon(Action onContinue, Action onCancel)
    {
        this.onContinue = onContinue;
        this.onCancel = onCancel;
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);
        resolved = false;

        var origin = ContentStartPosition;
        var content = ContentSize;
        var width = content.X;

        var line1 = new TextNode
        {
            Position = origin,
            Size = new Vector2(width, 20f),
            FontSize = 13,
            TextColor = TrackerNativeHelpers.ColorTitle,
            String = (ReadOnlySeString)"No MGP bonus is active (VIP Card or Jackpot III).",
            TextFlags = TextFlags.Ellipsis,
        };
        line1.AttachNode(this);

        var line2 = new TextNode
        {
            Position = new Vector2(origin.X, origin.Y + 22f),
            Size = new Vector2(width, 20f),
            FontSize = 13,
            TextColor = TrackerNativeHelpers.ColorTitle,
            String = (ReadOnlySeString)"Continue with Fashion Report anyway?",
            TextFlags = TextFlags.Ellipsis,
        };
        line2.AttachNode(this);

        var continueBtn = new TextButtonNode
        {
            Position = new Vector2(origin.X, origin.Y + 56f),
            Size = new Vector2(140f, 28f),
            String = "Continue",
            OnClick = () => Resolve(continueJudging: true),
        };
        continueBtn.AttachNode(this);

        var cancelBtn = new TextButtonNode
        {
            Position = new Vector2(origin.X + 152f, origin.Y + 56f),
            Size = new Vector2(120f, 28f),
            String = "Cancel",
            OnClick = () => Resolve(continueJudging: false),
        };
        cancelBtn.AttachNode(this);
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        // Window X / Esc — treat as Cancel so SelectString is not left blocked.
        if (!resolved)
            Resolve(continueJudging: false);
        base.OnHide(addon);
    }

    private void Resolve(bool continueJudging)
    {
        if (resolved)
            return;
        resolved = true;
        Close();
        if (continueJudging)
            onContinue();
        else
            onCancel();
    }
}
