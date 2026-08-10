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
    private readonly Func<FashionMgpBuffView> getVipView;
    private readonly Action useVipCard;

    private IconImageNode? vipIconNode;
    private TextButtonNode? vipButton;
    private string lastVipLabel = string.Empty;
    private uint lastVipIconId;
    private bool lastVipEnabled = true;
    private bool resolved;

    public FashionMgpReminderAddon(
        Action onContinue,
        Action onCancel,
        Func<FashionMgpBuffView> getVipView,
        Action useVipCard)
    {
        this.onContinue = onContinue;
        this.onCancel = onCancel;
        this.getVipView = getVipView;
        this.useVipCard = useVipCard;
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

        const float rowY = 56f;
        const float btnH = 28f;
        const float gap = 8f;
        const float continueW = 110f;
        const float cancelW = 100f;
        const float iconSize = 28f;

        var continueBtn = new TextButtonNode
        {
            Position = new Vector2(origin.X, origin.Y + rowY),
            Size = new Vector2(continueW, btnH),
            String = "Continue",
            OnClick = () => Resolve(continueJudging: true),
        };
        continueBtn.AttachNode(this);

        var vipX = origin.X + continueW + gap;
        vipIconNode = new IconImageNode
        {
            Position = new Vector2(vipX, origin.Y + rowY),
            Size = new Vector2(iconSize, iconSize),
            TextureSize = new Vector2(iconSize, iconSize),
            ImageNodeFlags = ImageNodeFlags.AutoFit,
            IconId = 26173,
        };
        vipIconNode.AttachNode(this);

        var cancelX = origin.X + width - cancelW;
        var vipBtnX = vipX + iconSize + 4f;
        var vipBtnW = MathF.Max(140f, cancelX - gap - vipBtnX);

        vipButton = new TextButtonNode
        {
            Position = new Vector2(vipBtnX, origin.Y + rowY),
            Size = new Vector2(vipBtnW, btnH),
            String = "Use VIP Card",
            OnClick = () => useVipCard(),
            TextTooltip = "Use a Gold Saucer VIP Card for +15% MGP for 120 minutes.",
        };
        vipButton.AttachNode(this);

        var cancelBtn = new TextButtonNode
        {
            Position = new Vector2(cancelX, origin.Y + rowY),
            Size = new Vector2(cancelW, btnH),
            String = "Cancel",
            OnClick = () => Resolve(continueJudging: false),
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
            Resolve(continueJudging: false);
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

        // Compact label for this dialog; FR window keeps the longer wording.
        var label = view.State switch
        {
            FashionMgpBuffState.VipActive =>
                view.CardCount > 0 ? $"VIP Card running · ×{view.CardCount}" : "VIP Card running",
            FashionMgpBuffState.JackpotIiiActive =>
                view.CardCount > 0 ? $"Jackpot III · ×{view.CardCount}" : "Jackpot III applied",
            FashionMgpBuffState.OutOfCards => "Out of VIP Cards",
            _ => view.CardCount > 0 ? $"Use VIP Card · ×{view.CardCount}" : "Use VIP Card",
        };

        if (label != lastVipLabel)
        {
            vipButton.String = label;
            lastVipLabel = label;
        }

        vipButton.TextTooltip = view.Tooltip;
        if (view.CanUse != lastVipEnabled || vipButton.IsEnabled != view.CanUse)
        {
            vipButton.IsEnabled = view.CanUse;
            lastVipEnabled = view.CanUse;
        }
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
