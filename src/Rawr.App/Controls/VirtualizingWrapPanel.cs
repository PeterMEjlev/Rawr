using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Rawr.App.Controls;

/// <summary>
/// Pixel-scrolling wrap panel for fixed-size tiles. Only the rows visible in the
/// viewport are materialized, which keeps large thumbnail grids responsive.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private const int CacheRowsBefore = 2;
    private const int CacheRowsAfter = 3;

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyProperty RealizedIndexProperty =
        DependencyProperty.RegisterAttached(
            "RealizedIndex",
            typeof(int),
            typeof(VirtualizingWrapPanel),
            new PropertyMetadata(-1));

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? _viewport.Width : availableSize.Width;
        if (width <= 0)
            width = ItemWidth;

        var itemWidth = Math.Max(1.0, ItemWidth);
        var itemHeight = Math.Max(1.0, ItemHeight);
        var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));
        var rowCount = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);

        SetViewport(availableSize);
        SetExtent(new Size(width, rowCount * itemHeight));
        SetVerticalOffset(VerticalOffset);

        if (itemCount == 0)
        {
            if (InternalChildren.Count > 0)
                RemoveInternalChildRange(0, InternalChildren.Count);
            return availableSize;
        }

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(VerticalOffset / itemHeight));
        var visibleRows = Math.Max(1, (int)Math.Ceiling(ViewportHeight / itemHeight));
        var firstMaterializedRow = Math.Max(0, firstVisibleRow - CacheRowsBefore);
        var lastMaterializedRow = firstVisibleRow + visibleRows + CacheRowsAfter;
        var firstIndex = Math.Max(0, firstMaterializedRow * columns);
        var lastIndex = Math.Min(itemCount - 1, ((lastMaterializedRow + 1) * columns) - 1);

        CleanupChildren(firstIndex, lastIndex);

        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var child = generator.GenerateNext(out var newlyRealized) as UIElement;
                if (child == null)
                    continue;

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.SetValue(RealizedIndexProperty, itemIndex);
                child.Measure(new Size(itemWidth, itemHeight));
            }
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = Math.Max(1.0, ItemWidth);
        var itemHeight = Math.Max(1.0, ItemHeight);
        var columns = Math.Max(1, (int)Math.Floor(finalSize.Width / itemWidth));

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex < 0)
                continue;

            var row = itemIndex / columns;
            var column = itemIndex % columns;
            child.Arrange(new Rect(
                column * itemWidth,
                (row * itemHeight) - VerticalOffset,
                itemWidth,
                itemHeight));
        }

        return finalSize;
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
            return;

        var itemWidth = Math.Max(1.0, ItemWidth);
        var itemHeight = Math.Max(1.0, ItemHeight);
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(ViewportWidth, itemWidth) / itemWidth));
        var rowTop = (index / columns) * itemHeight;
        var rowBottom = rowTop + itemHeight;

        if (rowTop < VerticalOffset)
            SetVerticalOffset(rowTop);
        else if (rowBottom > VerticalOffset + ViewportHeight)
            SetVerticalOffset(rowBottom - ViewportHeight);
    }

    private void CleanupChildren(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
                continue;

            var position = new GeneratorPosition(i, 0);
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void SetViewport(Size viewport)
    {
        var normalized = new Size(
            double.IsInfinity(viewport.Width) ? 0 : viewport.Width,
            double.IsInfinity(viewport.Height) ? 0 : viewport.Height);

        if (AreClose(_viewport, normalized))
            return;

        _viewport = normalized;
        ScrollOwner?.InvalidateScrollInfo();
    }

    private void SetExtent(Size extent)
    {
        if (AreClose(_extent, extent))
            return;

        _extent = extent;
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 24);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 24);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ScrollSpeed.GridWheelStep);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ScrollSpeed.GridWheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var maxOffset = Math.Max(0.0, ExtentHeight - ViewportHeight);
        var clamped = Math.Clamp(offset, 0.0, maxOffset);
        if (Math.Abs(clamped - _offset.Y) < 0.1)
            return;

        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = FindRealizedChild(visual);
        if (child == null)
            return rectangle;

        var itemIndex = (int)child.GetValue(RealizedIndexProperty);
        if (itemIndex >= 0)
            BringIndexIntoView(itemIndex);

        return rectangle;
    }

    private UIElement? FindRealizedChild(DependencyObject visual)
    {
        while (visual != null && visual != this)
        {
            if (visual is UIElement element && InternalChildren.Contains(element))
                return element;

            visual = VisualTreeHelper.GetParent(visual);
        }

        return null;
    }

    private static bool AreClose(Size left, Size right) =>
        Math.Abs(left.Width - right.Width) < 0.1
        && Math.Abs(left.Height - right.Height) < 0.1;
}
