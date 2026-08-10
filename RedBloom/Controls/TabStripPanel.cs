using System.Windows;
using System.Windows.Controls;

namespace RedBloom.Controls;

/// <summary>
/// Lays the tabs out in a row, shrinking them to fit rather than running off the edge.
/// </summary>
/// <remarks>
/// This replaces a scrolling strip. Scrolling was worse on both counts: the bar itself sat in
/// the title bar looking like it belonged to a document, and a horizontal <c>ScrollViewer</c>
/// measured with unbounded width — which is what an auto-sized column hands it — works out a
/// viewport that does not match what it draws, so dragging the bar swept the tabs off into
/// blank space. Sharing the width out has no such state: every tab is always on screen, and a
/// crowded strip reads at a glance the way a browser's does.
/// </remarks>
public sealed class TabStripPanel : Panel
{
    /// <summary>
    /// Narrow enough to still show the icon and the close button. Past this the strip gives up
    /// and overflows rather than shrinking tabs into a row of identical slivers.
    /// </summary>
    public static readonly DependencyProperty MinimumTabWidthProperty =
        DependencyProperty.Register(
            nameof(MinimumTabWidth),
            typeof(double),
            typeof(TabStripPanel),
            new FrameworkPropertyMetadata(72d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumTabWidth
    {
        get => (double)GetValue(MinimumTabWidthProperty);
        set => SetValue(MinimumTabWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            return default;
        }

        // What each tab would take if nothing were in its way. The template's own MaxWidth caps
        // it, so this is the natural width rather than an unbounded one.
        var natural = new double[children.Count];
        var total = 0d;
        var height = 0d;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            natural[i] = child.DesiredSize.Width;
            total += natural[i];
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // Unbounded width means nobody is constraining the strip — take the natural row.
        if (double.IsInfinity(availableSize.Width) || total <= availableSize.Width)
        {
            _widths = natural;
            return new Size(total, height);
        }

        var share = Math.Max(MinimumTabWidth, availableSize.Width / children.Count);
        var used = 0d;

        for (var i = 0; i < children.Count; i++)
        {
            // A tab narrower than its share keeps its own width; the rest are cut to the share,
            // so a short title never leaves a gap next to a truncated one.
            natural[i] = Math.Min(natural[i], share);
            children[i].Measure(new Size(natural[i], availableSize.Height));
            used += natural[i];
        }

        _widths = natural;
        return new Size(Math.Min(used, availableSize.Width), height);
    }

    private double[] _widths = [];

    protected override Size ArrangeOverride(Size finalSize)
    {
        var offset = 0d;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var width = i < _widths.Length ? _widths[i] : InternalChildren[i].DesiredSize.Width;
            InternalChildren[i].Arrange(new Rect(offset, 0, width, finalSize.Height));
            offset += width;
        }

        return finalSize;
    }
}
