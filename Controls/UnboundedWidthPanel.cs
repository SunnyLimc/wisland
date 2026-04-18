using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace wisland.Controls
{
    /// <summary>
    /// A single-child panel that measures its child with double.PositiveInfinity
    /// for width so the child's ActualWidth reflects its natural (unclamped)
    /// width. Used by <see cref="MarqueeText"/> so the inner TextBlock can
    /// overflow horizontally beyond the viewport for marquee scrolling.
    /// </summary>
    /// <remarks>
    /// Unlike a horizontal StackPanel, this panel reports ONLY the child's
    /// measured height as its own DesiredSize (width is reported as child's
    /// desired width, which the parent may clip). Unlike Canvas, this panel
    /// propagates the child's DesiredSize upward so vertical layout works
    /// correctly in surrounding StackPanels or auto-row Grids.
    /// </remarks>
    public sealed class UnboundedWidthPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double maxWidth = 0;
            double maxHeight = 0;
            var childAvailable = new Size(double.PositiveInfinity, availableSize.Height);
            foreach (UIElement child in Children)
            {
                child.Measure(childAvailable);
                if (child.DesiredSize.Width > maxWidth) maxWidth = child.DesiredSize.Width;
                if (child.DesiredSize.Height > maxHeight) maxHeight = child.DesiredSize.Height;
            }
            return new Size(maxWidth, maxHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in Children)
            {
                // Arrange the child at max(naturalDesired, finalSize). When the
                // child fits inside the panel the inner TextAlignment decides
                // placement (left/center/right) within the column. When the
                // child is wider than the panel it overflows to the right and
                // the parent's clip hides the overflow so the marquee can scroll.
                double w = Math.Max(child.DesiredSize.Width, finalSize.Width);
                double h = finalSize.Height > 0 ? finalSize.Height : child.DesiredSize.Height;
                child.Arrange(new Rect(0, 0, w, h));
            }
            return finalSize;
        }
    }
}
