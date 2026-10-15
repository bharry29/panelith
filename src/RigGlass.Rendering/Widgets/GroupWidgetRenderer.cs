using RigGlass.Themes;
using SkiaSharp;

namespace RigGlass.Rendering.Widgets;

/// <summary>Renders nested widget groups with local origin, clip and opacity.</summary>
internal static class GroupWidgetRenderer
{
    public static void Render(
        GroupWidget widget,
        ThemeRect rect,
        WidgetRenderContext ctx,
        Action<ThemeWidget, ThemeRect, WidgetRenderContext> renderChild)
    {
        if (widget.Opacity <= 0d)
        {
            return;
        }

        ctx.Canvas.Save();
        var previousOpacity = ctx.Opacity;
        ctx.Opacity = Math.Clamp(previousOpacity * widget.Opacity, 0d, 1d);

        if (widget.Clip && !rect.IsEmpty)
        {
            ctx.Canvas.ClipRect(new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom));
        }

        // Children live in the group's design coordinate space. When the group
        // itself was stretched by anchors, that stretch becomes the children's
        // local scale so nested layouts stay coherent.
        var designW = widget.Width > 0d ? widget.Width : rect.Width;
        var designH = widget.Height > 0d ? widget.Height : rect.Height;
        var scaleX = designW > 0d ? rect.Width / designW : 1d;
        var scaleY = designH > 0d ? rect.Height / designH : 1d;

        var items = widget.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var child = items[i];
            var childRect = MapChild(rect, designW, designH, scaleX, scaleY, child);
            renderChild(child, childRect, ctx);
        }

        ctx.Opacity = previousOpacity;
        ctx.Canvas.Restore();
    }

    private static ThemeRect MapChild(
        ThemeRect groupRect,
        double designW,
        double designH,
        double scaleX,
        double scaleY,
        ThemeWidget child)
    {
        var design = child.Bounds;
        if (child.Anchor == AnchorEdges.None)
        {
            return new ThemeRect(
                groupRect.X + (design.X * scaleX),
                groupRect.Y + (design.Y * scaleY),
                design.Width * scaleX,
                design.Height * scaleY);
        }

        var left = groupRect.X + (design.X * scaleX);
        var top = groupRect.Y + (design.Y * scaleY);
        var right = groupRect.X + (design.Right * scaleX);
        var bottom = groupRect.Y + (design.Bottom * scaleY);

        var distLeft = design.X;
        var distTop = design.Y;
        var distRight = designW - design.Right;
        var distBottom = designH - design.Bottom;

        if ((child.Anchor & AnchorEdges.Left) != 0)
        {
            left = groupRect.X + distLeft;
        }

        if ((child.Anchor & AnchorEdges.Top) != 0)
        {
            top = groupRect.Y + distTop;
        }

        if ((child.Anchor & AnchorEdges.Right) != 0)
        {
            right = groupRect.Right - distRight;
        }

        if ((child.Anchor & AnchorEdges.Bottom) != 0)
        {
            bottom = groupRect.Bottom - distBottom;
        }

        if ((child.Anchor & AnchorEdges.LeftRight) == AnchorEdges.Left)
        {
            right = left + (design.Width * scaleX);
        }
        else if ((child.Anchor & AnchorEdges.LeftRight) == AnchorEdges.Right)
        {
            left = right - (design.Width * scaleX);
        }

        if ((child.Anchor & AnchorEdges.TopBottom) == AnchorEdges.Top)
        {
            bottom = top + (design.Height * scaleY);
        }
        else if ((child.Anchor & AnchorEdges.TopBottom) == AnchorEdges.Bottom)
        {
            top = bottom - (design.Height * scaleY);
        }

        return new ThemeRect(left, top, right - left, bottom - top);
    }
}
