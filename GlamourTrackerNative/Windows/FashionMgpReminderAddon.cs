using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamourTracker.Services.FashionReport;
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
    private readonly Action onUseVip;
    private readonly Func<FashionMgpBuffView> getVipView;

    private IconImageNode? vipIconNode;
    private TextButtonNode? vipButton;
    private string lastVipLabel = string.Empty;
    private uint lastVipIconId;
    private bool lastVipEnabled = true;
    private bool resolved;

    public FashionMgpReminderAddon(
        Action onContinue,
        Action onCancel,
        Action onUseVip,
        Func<FashionMgpBuffView> getVipView)
    {
        this.onContinue = onContinue;
        this.onCancel = onCancel;
        this.onUseVip = onUseVip;
        this.getVipView = getVipView;
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        base.OnSetup(addon, atkValueSpan);
        resolved = false;
        lastVipLabel = string.Empty;
        lastVipIconId = 0;
        lastVipEnabled = true;

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

        // Compact left-packed row — VIP button sized to its label, not stretched.
        const float rowY = 56f;
        const float btnH = 28f;
        const float gap = 8f;
        const float continueW = 100f;
        const float cancelW = 90f;
        const float iconSize = 28f;
        const float vipBtnW = 148f;

        var x = origin.X;
        var y = origin.Y + rowY;

        var continueBtn = new TextButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(continueW, btnH),
            String = "Continue",
            OnClick = () => Resolve(FashionMgpReminderChoice.Continue),
        };
        continueBtn.AttachNode(this);
        x += continueW + gap;

        vipIconNode = new IconImageNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(iconSize, iconSize),
            TextureSize = new Vector2(iconSize, iconSize),
            ImageNodeFlags = ImageNodeFlags.AutoFit,
            IconId = 26173,
        };
        vipIconNode.AttachNode(this);
        x += iconSize + 4f;

        vipButton = new TextButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(vipBtnW, btnH),
            String = "Use VIP Card",
            OnClick = () => Resolve(FashionMgpReminderChoice.UseVip),
            TextTooltip = "Closes Masked Rose, then uses a Gold Saucer VIP Card (+15% MGP).",
        };
        vipButton.AttachNode(this);
        x += vipBtnW + gap;

        var cancelBtn = new TextButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(cancelW, btnH),
            String = "Cancel",
            OnClick = () => Resolve(FashionMgpReminderChoice.Cancel),
        };
        cancelBtn.AttachNode(this);

        RefreshVipChrome();
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);
        if (!resolved)
            RefreshVipChrome();
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        // Window X / Esc — treat as Cancel so SelectString is not left blocked.
        if (!resolved)
            Resolve(FashionMgpReminderChoice.Cancel);
        base.OnHide(addon);
    }

    private void RefreshVipChrome()
    {
        if (vipButton == null && vipIconNode == null)
            return;

        var view = getVipView();
        var iconId = view.IconId != 0 ? view.IconId : 26173u;

        if (vipIconNode != null && iconId != lastVipIconId)
        {
            vipIconNode.IconId = iconId;
            lastVipIconId = iconId;
        }

        if (vipIconNode != null)
        {
            vipIconNode.Color = view.CanUse
                ? Vector4.One
                : new Vector4(0.55f, 0.55f, 0.55f, 0.85f);
        }

        if (vipButton == null)
            return;

        var label = view.State switch
        {
            FashionMgpBuffState.VipActive =>
                view.CardCount > 0 ? $"VIP running ×{view.CardCount}" : "VIP running",
            FashionMgpBuffState.JackpotIiiActive =>
                view.CardCount > 0 ? $"Jackpot III ×{view.CardCount}" : "Jackpot III",
            FashionMgpBuffState.OutOfCards => "Out of VIP Cards",
            _ => view.CardCount > 0 ? $"Use VIP Card ×{view.CardCount}" : "Use VIP Card",
        };

        if (label != lastVipLabel)
        {
            vipButton.String = label;
            lastVipLabel = label;
        }

        vipButton.TextTooltip = view.CanUse
            ? "Closes Masked Rose, then uses a Gold Saucer VIP Card (+15% MGP)."
            : view.Tooltip;
        if (view.CanUse != lastVipEnabled || vipButton.IsEnabled != view.CanUse)
        {
            vipButton.IsEnabled = view.CanUse;
            lastVipEnabled = view.CanUse;
        }
    }

    private void Resolve(FashionMgpReminderChoice choice)
    {
        if (resolved)
            return;
        resolved = true;
        Close();
        switch (choice)
        {
            case FashionMgpReminderChoice.Continue:
                onContinue();
                break;
            case FashionMgpReminderChoice.UseVip:
                onUseVip();
                break;
            default:
                onCancel();
                break;
        }
    }

    private enum FashionMgpReminderChoice : byte
    {
        Cancel,
        Continue,
        UseVip,
    }
}
