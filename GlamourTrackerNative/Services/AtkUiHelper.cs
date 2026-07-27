using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourTracker.Services;

internal static unsafe class AtkUiHelper
{
    public delegate void NodeVisitor(AtkResNode* node);

    public static readonly ByteColor OwnedTint = new() { A = 255, R = 80, G = 220, B = 90 };
    public static readonly ByteColor MissingTint = new() { A = 255, R = 230, G = 70, B = 70 };
    private static readonly ByteColor NeutralTint = new() { A = 255, R = 255, G = 255, B = 255 };

    public static void WalkNodes(AtkResNode* root, NodeVisitor visit)
    {
        if (root == null)
            return;

        for (var node = root; node != null; node = node->NextSiblingNode)
        {
            visit(node);
            WalkNodes(node->ChildNode, visit);
        }
    }

    public static void TintIconGroup(AtkResNode* group, bool owned)
    {
        if (group == null)
            return;

        WalkNodes(group, node =>
        {
            if (node->Type != NodeType.Image)
                return;

            var image = node->GetAsAtkImageNode();
            if (image == null)
                return;

            TintNode((AtkResNode*)image, owned ? OwnedTint : MissingTint);
        });
    }

    public static void RestoreIconGroup(AtkResNode* group)
    {
        if (group == null)
            return;

        WalkNodes(group, node =>
        {
            if (node->Type == NodeType.Image)
                ResetTint(node);
        });
    }

    public static void SetGroupVisible(AtkResNode* group, bool visible)
    {
        if (group == null)
            return;

        if (visible)
            group->NodeFlags |= NodeFlags.Visible;
        else
            group->NodeFlags &= ~NodeFlags.Visible;
    }

    public static void ResetTint(AtkResNode* node)
    {
        if (node == null)
            return;

        TintNode(node, NeutralTint);
    }

    public static void TintNode(AtkResNode* node, ByteColor tint)
    {
        if (node == null)
            return;

        node->Color = tint;
        node->MultiplyRed = tint.R;
        node->MultiplyGreen = tint.G;
        node->MultiplyBlue = tint.B;
        node->AddRed = 0;
        node->AddGreen = 0;
        node->AddBlue = 0;
    }

    public static AtkTextNode* FindRightmostTextNode(AtkResNode* root, bool requireVisible = true)
    {
        AtkTextNode* best = null;
        var bestX = float.MinValue;

        WalkNodes(root, node =>
        {
            if (node->Type != NodeType.Text)
                return;

            var text = node->GetAsAtkTextNode();
            if (text == null || (requireVisible && !node->IsVisible()))
                return;

            if (node->X > bestX)
            {
                bestX = node->X;
                best = text;
            }
        });

        return best;
    }

    public static Vector2 GetNodeScreenPosition(AtkResNode* node, float offsetX = 0, float offsetY = 0)
    {
        if (node == null)
            return Vector2.Zero;

        return new Vector2(node->ScreenX + offsetX, node->ScreenY + offsetY);
    }

    /// <summary>
    /// Top-left screen corner for a node. List item icons often anchor <see cref="AtkResNode.ScreenY"/> at the bottom edge.
    /// </summary>
    public static Vector2 GetNodeScreenTopLeft(AtkResNode* node)
    {
        if (node == null)
            return Vector2.Zero;

        var x = node->ScreenX;
        var y = node->ScreenY;
        var height = node->Height;

        if (height > 0 && UsesBottomScreenAnchor(node))
            y -= height;

        return new Vector2(x, y);
    }

    private static bool UsesBottomScreenAnchor(AtkResNode* node)
    {
        if (node->Type == NodeType.Image)
            return true;

        if (node->Type != NodeType.Component)
            return false;

        var componentNode = node->GetAsAtkComponentNode();
        return componentNode != null && componentNode->GetAsAtkComponentIcon() != null;
    }

    /// <summary>
    /// Resolves top-left screen position using the node itself or its offset from a row root with valid screen coords.
    /// </summary>
    public static Vector2? ResolveNodeScreenTopLeft(AtkResNode* rowRoot, AtkResNode* node)
    {
        if (node == null)
            return null;

        var direct = GetNodeScreenTopLeft(node);
        if (direct.X > 1f && direct.Y > 1f)
            return direct;

        if (rowRoot == null)
            return null;

        var rowTopLeft = GetNodeScreenTopLeft(rowRoot);
        if (rowTopLeft.X <= 1f || rowTopLeft.Y <= 1f)
            return null;

        var relative = GetOffsetWithinAncestor(rowRoot, node);
        return rowTopLeft + relative;
    }

    public static unsafe AtkResNode* GetComponentOwnerResNode(AtkComponentBase* component)
    {
        if (component == null)
            return null;

        if (component->OwnerNode != null)
            return (AtkResNode*)component->OwnerNode;

        return component->AtkResNode;
    }

    /// <summary>Icon inset from row left in GC expert delivery list layout.</summary>
    private const float GcRowIconPadLeft = 8f;
    /// <summary>Fine-tune after list-layout anchor (screen space).</summary>
    public const float GcMarkerCalibrateX = 550f;
    public const float GcMarkerCalibrateY = 0f;

    private static unsafe AtkResNode* GetRowLabelTextNode(AtkComponentListItemRenderer* renderer, AtkResNode* rowRoot)
    {
        if (renderer != null && renderer->ButtonTextNode != null)
            return (AtkResNode*)renderer->ButtonTextNode;

        return FindPrimaryRowLabelTextNode(rowRoot);
    }

    private static unsafe AtkResNode* GetItemIconDrawNode(AtkResNode* rowRoot)
    {
        var iconComponent = FindLeftmostItemGraphicNode(rowRoot);
        if (iconComponent == null)
            return null;

        AtkResNode* bestImage = null;
        var bestX = float.MaxValue;

        WalkNodes(iconComponent, node =>
        {
            if (node->Type != NodeType.Image)
                return;

            if (node->X < bestX)
            {
                bestX = node->X;
                bestImage = node;
            }
        });

        return bestImage != null ? bestImage : iconComponent;
    }

    /// <summary>Primary item name line in a list row (longest alphabetic text).</summary>
    public static unsafe AtkResNode* FindPrimaryRowLabelTextNode(AtkResNode* rowRoot)
    {
        if (rowRoot == null)
            return null;

        AtkResNode* bestNode = null;
        var bestLength = 0;

        WalkNodes(rowRoot, node =>
        {
            if (node->Type != NodeType.Text)
                return;

            var text = node->GetAsAtkTextNode();
            if (text == null)
                return;

            var value = text->NodeText.ToString();
            if (value.Length <= bestLength)
                return;

            if (!value.Any(char.IsLetter))
                return;

            bestLength = value.Length;
            bestNode = node;
        });

        return bestNode;
    }

    /// <summary>
    /// ATK node Position is local (unscaled). Convert a screen-space point using addon X/Y/Scale
    /// (same pattern as KamiToolKit DropDownNode).
    /// </summary>
    public static unsafe Vector2 ScreenToAddonLocal(AtkUnitBase* addon, Vector2 screen)
    {
        if (addon == null)
            return screen;

        var scale = Math.Max(addon->Scale, 0.01f);
        var origin = new Vector2(addon->X, addon->Y);
        return (screen - origin) / scale;
    }

    /// <summary>
    /// GC expert delivery draws item icons via the list, not inside row nodes — use list + renderer layout.
    /// <paramref name="uiScale"/> is the addon Scale; list ItemHeight/ScrollOffset/Left are local units.
    /// </summary>
    public static unsafe Vector2? TryGetListRowMarkerAnchor(
        AtkComponentList* list,
        AtkComponentListItemRenderer* renderer,
        int itemIndex,
        float gapBeforeIcon = 6f,
        float markerHeight = 20f,
        float markerWidth = 20f,
        float uiScale = 1f)
    {
        if (renderer == null || list == null)
            return null;

        var listNode = GetComponentOwnerResNode((AtkComponentBase*)list);
        if (listNode == null)
            return null;

        var listX = listNode->ScreenX;
        var listY = listNode->ScreenY;
        if (listX <= 1f || listY <= 1f)
            return null;

        var scale = Math.Max(uiScale, 0.01f);
        var itemHeight = list->ItemHeight > 0 ? list->ItemHeight : (short)40;
        var slot = Math.Max(0, itemIndex - list->FirstVisibleItemIndex);
        // ScreenY is scaled; ItemHeight/ScrollOffset/Left are local — multiply local deltas by scale.
        var rowTop = listY + (list->ScrollOffset + (slot * itemHeight)) * scale;
        var rowLeft = listX + (renderer->Left * scale);

        var x = rowLeft
            + (GcRowIconPadLeft - gapBeforeIcon - markerWidth + GcMarkerCalibrateX) * scale;

        var y = rowTop + MathF.Max(0f, (itemHeight - markerHeight) * 0.5f * scale);

        var rowNode = renderer->OwnerNode != null
            ? (AtkResNode*)renderer->OwnerNode
            : null;
        var textNode = GetRowLabelTextNode(renderer, rowNode);
        var textTopLeft = ResolveNodeScreenTopLeft(rowNode, textNode);
        if (textTopLeft != null && textNode != null)
        {
            var textHeight = textNode->Height > 0 ? (float)textNode->Height : (float)itemHeight;
            y = textTopLeft.Value.Y + MathF.Max(0f, (textHeight - markerHeight) * 0.5f * scale);
        }

        y += GcMarkerCalibrateY * scale;

        return new Vector2(x, y);
    }

    /// <summary>Leftmost item icon graphic in a row (component icon or square image node).</summary>
    public static unsafe AtkResNode* FindLeftmostItemGraphicNode(AtkResNode* rowRoot)
    {
        if (rowRoot == null)
            return null;

        AtkResNode* bestComponentIcon = null;
        AtkResNode* bestImage = null;
        var bestComponentX = float.MaxValue;
        var bestImageX = float.MaxValue;

        WalkNodes(rowRoot, node =>
        {
            if (node->Type == NodeType.Component)
            {
                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode != null && componentNode->GetAsAtkComponentIcon() != null && node->X < bestComponentX)
                {
                    bestComponentX = node->X;
                    bestComponentIcon = node;
                }

                return;
            }

            if (node->Type != NodeType.Image)
                return;

            if (node->Width is < 20 or > 56 || node->Height is < 20 or > 56)
                return;

            if (node->X < bestImageX)
            {
                bestImageX = node->X;
                bestImage = node;
            }
        });

        return bestComponentIcon != null ? bestComponentIcon : bestImage;
    }

    /// <summary>
    /// Resolves screen coordinates for a list row during ImGui draw (after ATK has updated ScreenX/Y).
    /// </summary>
    public static unsafe Vector2? TryGetListRowLabelPosition(
        AtkComponentList* list,
        AtkComponentListItemRenderer* renderer,
        AtkResNode* rowRoot,
        float offsetX,
        float offsetY)
    {
        if (renderer == null)
            return null;

        float screenX = 0;
        float screenY = 0;
        renderer->GetScreenPosition(&screenX, &screenY);
        if (screenX > 1f || screenY > 1f)
            return new Vector2(screenX + offsetX, screenY + offsetY);

        if (renderer->AtkResNode != null)
        {
            var componentPos = GetNodeScreenPosition(renderer->AtkResNode);
            if (componentPos.X > 1f || componentPos.Y > 1f)
                return componentPos + new Vector2(offsetX, offsetY);
        }

        if (rowRoot != null)
        {
            var rowPos = GetNodeScreenPosition(rowRoot);
            if (rowPos.X > 1f || rowPos.Y > 1f)
                return rowPos + new Vector2(offsetX, offsetY);
        }

        if (list == null)
            return null;

        var listNode = list->AtkResNode;
        if (listNode == null)
            return null;

        var listPos = GetNodeScreenPosition(listNode);
        return new Vector2(listPos.X + renderer->Left + offsetX, listPos.Y + renderer->Top + offsetY);
    }

    /// <summary>
    /// Left edge of the row's primary item icon in screen space (for markers placed to the left of the icon).
    /// </summary>
    public static unsafe Vector2? TryGetIconLeftScreenPosition(
        AtkComponentList* list,
        AtkComponentListItemRenderer* renderer,
        AtkResNode* rowRoot,
        AtkResNode* iconComponentNode,
        int itemIndex = -1)
    {
        if (renderer == null || iconComponentNode == null)
            return null;

        var iconOffsetX = iconComponentNode->X;
        var iconOffsetY = iconComponentNode->Y;

        if (list != null)
        {
            var listNode = GetComponentOwnerResNode((AtkComponentBase*)list);
            if (listNode != null)
            {
                var listPos = GetNodeScreenPosition(listNode);
                if (listPos.X > 1f && listPos.Y > 1f)
                {
                    var itemHeight = list->ItemHeight > 0 ? list->ItemHeight : (short)40;
                    var slot = itemIndex >= 0
                        ? Math.Max(0, itemIndex - list->FirstVisibleItemIndex)
                        : Math.Max(0, renderer->ListItemIndex - list->FirstVisibleItemIndex);

                    return new Vector2(
                        listPos.X + renderer->Left + iconOffsetX,
                        listPos.Y + list->ScrollOffset + (slot * itemHeight) + iconOffsetY);
                }
            }
        }

        float rowScreenX = 0;
        float rowScreenY = 0;
        renderer->GetScreenPosition(&rowScreenX, &rowScreenY);

        var relative = rowRoot != null
            ? GetOffsetWithinAncestor(rowRoot, iconComponentNode)
            : new Vector2(iconOffsetX, iconOffsetY);
        var leftX = rowScreenX + relative.X;
        var topY = rowScreenY + relative.Y;

        if (rowScreenX <= 1f && rowScreenY <= 1f)
        {
            var iconScreen = GetNodeScreenPosition(iconComponentNode);
            if (iconScreen.X > 1f || iconScreen.Y > 1f)
            {
                leftX = iconScreen.X;
                topY = iconScreen.Y;
            }
            else if (renderer->AtkResNode != null)
            {
                var rendererScreen = GetNodeScreenPosition(renderer->AtkResNode);
                leftX = rendererScreen.X + relative.X;
                topY = rendererScreen.Y + relative.Y;
            }
        }

        if (leftX <= 1f && topY <= 1f)
            return null;

        return new Vector2(leftX, topY);
    }

    /// <summary>
    /// Resolves screen-space coordinates for drawing on top of a list row item icon.
    /// </summary>
    public static unsafe Vector2? TryGetItemIconScreenPosition(
        AtkComponentListItemRenderer* renderer,
        AtkResNode* rowRoot,
        AtkResNode* iconComponentNode,
        AtkResNode* iconDrawNode)
    {
        if (iconDrawNode == null)
            return null;

        var drawScreen = GetNodeScreenPosition(iconDrawNode);
        if (drawScreen.X > 1f && drawScreen.Y > 1f)
            return drawScreen;

        if (renderer != null)
        {
            float rowScreenX = 0;
            float rowScreenY = 0;
            renderer->GetScreenPosition(&rowScreenX, &rowScreenY);

            if (rowScreenX > 1f || rowScreenY > 1f)
            {
                var relative = GetOffsetWithinAncestor(rowRoot, iconDrawNode);
                return new Vector2(rowScreenX + relative.X, rowScreenY + relative.Y);
            }
        }

        if (iconComponentNode != null)
        {
            var compScreen = GetNodeScreenPosition(iconComponentNode);
            if (compScreen.X > 1f && compScreen.Y > 1f)
            {
                var relative = GetOffsetWithinAncestor(iconComponentNode, iconDrawNode);
                return compScreen + relative;
            }
        }

        var accumulated = AccumulateToRoot(iconDrawNode);
        return accumulated.X > 0f || accumulated.Y > 0f ? accumulated : null;
    }

    public static unsafe Vector2 GetOffsetWithinAncestor(AtkResNode* ancestor, AtkResNode* node)
    {
        if (ancestor == null || node == null)
            return Vector2.Zero;

        var x = node->X;
        var y = node->Y;

        for (var parent = node->ParentNode; parent != null && parent != ancestor; parent = parent->ParentNode)
        {
            x += parent->X;
            y += parent->Y;
        }

        return new Vector2(x, y);
    }

    private static unsafe Vector2 AccumulateToRoot(AtkResNode* node)
    {
        if (node == null)
            return Vector2.Zero;

        var x = node->X;
        var y = node->Y;

        for (var parent = node->ParentNode; parent != null; parent = parent->ParentNode)
        {
            x += parent->X;
            y += parent->Y;

            if (parent->ScreenX > 1f && parent->ScreenY > 1f)
                return new Vector2(parent->ScreenX + x, parent->ScreenY + y);
        }

        return new Vector2(x, y);
    }
}
