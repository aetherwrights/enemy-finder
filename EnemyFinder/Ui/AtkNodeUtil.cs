using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EnemyFinder.Ui;

internal unsafe delegate void AtkNodeVisitor(AtkResNode* node);

internal static class AtkNodeUtil
{
    public static unsafe void ForEachNode(AtkUnitBase* addon, AtkNodeVisitor visit)
    {
        if (addon == null)
        {
            return;
        }

        ForEachNode(&addon->UldManager, visit, 0);
    }

    public static unsafe string? GetVisibleText(AtkResNode* node)
    {
        if (node == null)
        {
            return null;
        }

        if (node->Type == NodeType.Text)
        {
            return CleanText(((AtkTextNode*)node)->NodeText.ToString());
        }

        if ((ushort)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null)
            {
                var button = node->GetAsAtkComponentButton();
                if (button != null && button->ButtonTextNode != null)
                {
                    var buttonText = CleanText(button->ButtonTextNode->NodeText.ToString());
                    if (buttonText != null)
                    {
                        return buttonText;
                    }
                }

                string? nested = null;
                ForEachNode(&component->UldManager, child =>
                {
                    if (nested != null || child->Type != NodeType.Text)
                    {
                        return;
                    }

                    nested = CleanText(((AtkTextNode*)child)->NodeText.ToString());
                }, 0);
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        var parent = node->ParentNode;
        while (parent != null)
        {
            if (parent->Type == NodeType.Text)
            {
                return CleanText(((AtkTextNode*)parent)->NodeText.ToString());
            }

            parent = parent->ParentNode;
        }

        return null;
    }

    public static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        var slash = text.LastIndexOf('/');
        if (slash > 0)
        {
            var i = slash - 1;
            while (i >= 0 && (char.IsDigit(text[i]) || char.IsWhiteSpace(text[i])))
            {
                i--;
            }

            if (i >= 0 && char.IsWhiteSpace(text[i]))
            {
                text = text[..i].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static unsafe bool ContainsPoint(AtkResNode* node, int x, int y, int pad = 0)
    {
        if (node == null)
        {
            return false;
        }

        Bounds bounds;
        node->GetBounds(&bounds);
        return x >= bounds.Pos1.X - pad && x <= bounds.Pos2.X + pad &&
               y >= bounds.Pos1.Y - pad && y <= bounds.Pos2.Y + pad;
    }

    public static unsafe List<string> CollectTexts(AtkResNode* node, int parentHops = 2)
    {
        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = node;
        for (var hop = 0; current != null && hop <= parentHops; hop++)
        {
            var text = GetVisibleText(current);
            if (text != null && seen.Add(text))
            {
                texts.Add(text);
            }

            current = current->ParentNode;
        }

        return texts;
    }

    public static unsafe void MakeClickable(AtkResNode* node)
    {
        if (node == null)
        {
            return;
        }

        node->NodeFlags |= NodeFlags.EmitsEvents | NodeFlags.RespondToMouse | NodeFlags.HasCollision;
        node->IsClickableCursorOnHover = true;
    }

    private static unsafe void ForEachNode(AtkUldManager* uld, AtkNodeVisitor visit, int depth)
    {
        if (uld == null || uld->NodeList == null || depth > 8)
        {
            return;
        }

        var count = uld->NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = uld->NodeList[i];
            if (node == null)
            {
                continue;
            }

            visit(node);
            if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                {
                    ForEachNode(&component->UldManager, visit, depth + 1);
                }
            }
        }
    }
}
