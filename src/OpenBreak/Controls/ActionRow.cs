using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OpenBreak;

// Measure native buttons at their actual text size so the same controls can move to a new row.
internal sealed class ActionRow : Panel
{
    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double rowWidth = 0, rowHeight = 0, blockWidth = 0, y = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            if (!arrange)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
            }
            var size = child.DesiredSize;
            var x = rowWidth == 0 ? 0 : rowWidth + SurfaceStyle.ControlGap;
            if (rowWidth > 0 && x + size.Width > width)
            {
                y += rowHeight + SurfaceStyle.ControlGap;
                rowHeight = 0;
                x = 0;
            }
            if (arrange)
            {
                child.Arrange(new Rect(x, y, Math.Min(size.Width, width), size.Height));
            }
            rowWidth = x + size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            blockWidth = Math.Max(blockWidth, rowWidth);
        }
        return new Size(blockWidth, y + rowHeight);
    }
}
