using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rawr.App.Converters;
using Rawr.Core.Models;

namespace Rawr.App.Controls;

/// <summary>
/// Pixel-scrolling wrap panel for fixed-size tiles. Only the rows visible in the
/// viewport are materialized, which keeps large thumbnail grids responsive.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private const int CacheRowsBefore = 3;
    private const int CacheRowsAfter = 6;
    private const int PreloadRowsBefore = 4;
    private const int PreloadRowsAfter = 12;

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
    private int _lastColumns = 1;
    private int _lastItemCount;
    private double _lastItemHeight = 1.0;
    private int _realizedFirstIndex = -1;
    private int _realizedLastIndex = -1;
    private int _lastPreloadFirstIndex = -1;
    private int _lastPreloadLastIndex = -1;
    private CancellationTokenSource? _preloadCts;

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

        _lastColumns = columns;
        _lastItemCount = itemCount;
        _lastItemHeight = itemHeight;

        SetViewport(availableSize);
        SetExtent(new Size(width, rowCount * itemHeight));
        SetVerticalOffset(VerticalOffset);

        if (itemCount == 0)
        {
            if (InternalChildren.Count > 0)
                RemoveInternalChildRange(0, InternalChildren.Count);

            _realizedFirstIndex = -1;
            _realizedLastIndex = -1;
            _lastPreloadFirstIndex = -1;
            _lastPreloadLastIndex = -1;
            return availableSize;
        }

        var (firstIndex, lastIndex) = CalculateMaterializedRange(VerticalOffset, ViewportHeight, itemHeight, columns, itemCount);

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

                // newlyRealized = container is fresh (or pulled from the recycle pool
                // and rebound to a new item). In both cases the container is detached
                // from our visual tree and must be re-inserted. Already-realized
                // containers staying in place return newlyRealized=false AND remain
                // in InternalChildren — skip the visual-tree mutation for those.
                if (newlyRealized || VisualTreeHelper.GetParent(child) != this)
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

        _realizedFirstIndex = firstIndex;
        _realizedLastIndex = lastIndex;
        QueueThumbnailPreload(firstIndex, lastIndex, itemCount, columns);

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

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        // Generator hands us notifications for items it has already updated; we
        // still need to drop the matching visual children so the next measure
        // pass rebuilds (or pulls from the recycle pool) instead of stranding
        // a container bound to a now-stale item.
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                if (args.ItemUICount > 0)
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0)
                    RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0)
                    RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }

        _realizedFirstIndex = -1;
        _realizedLastIndex = -1;
        _lastPreloadFirstIndex = -1;
        _lastPreloadLastIndex = -1;
        _preloadCts?.Cancel();
    }

    private void CleanupChildren(int firstIndex, int lastIndex)
    {
        // Recycle (don't dispose) containers that scrolled out of range so the
        // generator can hand them back on the next realize pass — saves a full
        // template rebuild per cell. Falls back to Remove when the host ListBox
        // hasn't opted into VirtualizationMode=Recycling.
        var generator = (IRecyclingItemContainerGenerator)ItemContainerGenerator;
        var recycle = GetIsVirtualizing(this) && GetVirtualizationMode(this) == VirtualizationMode.Recycling;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
                continue;

            var position = new GeneratorPosition(i, 0);
            if (recycle)
                generator.Recycle(position, 1);
            else
                generator.Remove(position, 1);

            child.ClearValue(RealizedIndexProperty);
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
        if (CanArrangeOnlyForOffset(clamped))
            InvalidateArrange();
        else
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

    private bool CanArrangeOnlyForOffset(double offset)
    {
        if (_realizedFirstIndex < 0 || _lastItemCount <= 0)
            return false;

        var range = CalculateMaterializedRange(offset, ViewportHeight, _lastItemHeight, _lastColumns, _lastItemCount);
        return range.LastIndex >= range.FirstIndex
            && range.FirstIndex >= _realizedFirstIndex
            && range.LastIndex <= _realizedLastIndex;
    }

    private void QueueThumbnailPreload(int firstIndex, int lastIndex, int itemCount, int columns)
    {
        if (lastIndex < firstIndex || itemCount <= 0)
            return;

        var warmFirst = Math.Max(0, firstIndex - (columns * PreloadRowsBefore));
        var warmLast = Math.Min(itemCount - 1, lastIndex + (columns * PreloadRowsAfter));
        if (warmFirst >= _lastPreloadFirstIndex && warmLast <= _lastPreloadLastIndex)
            return;

        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null)
            return;

        var bytes = new List<byte[]>(Math.Min(256, warmLast - warmFirst + 1));
        for (var itemIndex = warmFirst; itemIndex <= warmLast && itemIndex < owner.Items.Count; itemIndex++)
        {
            if (owner.Items[itemIndex] is PhotoItem { ThumbnailJpeg: { Length: > 0 } thumbnailBytes })
                bytes.Add(thumbnailBytes);
        }

        if (bytes.Count == 0)
            return;

        _lastPreloadFirstIndex = warmFirst;
        _lastPreloadLastIndex = warmLast;
        _preloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _preloadCts = cts;

        // Decode in parallel: a single-threaded warmer can't keep up with a
        // fast wheel scroll on big folders, so hot rows still hit the UI
        // thread converter on a cache miss. Cap parallelism to leave the UI
        // thread headroom for layout + render.
        var parallelism = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2));
        _ = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(
                    bytes,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cts.Token },
                    static b => JpegBytesToImageConverter.Preload(b));
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private static (int FirstIndex, int LastIndex) CalculateMaterializedRange(
        double verticalOffset,
        double viewportHeight,
        double itemHeight,
        int columns,
        int itemCount)
    {
        if (itemCount <= 0)
            return (0, -1);

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(verticalOffset / itemHeight));
        var visibleRows = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemHeight));
        var firstMaterializedRow = Math.Max(0, firstVisibleRow - CacheRowsBefore);
        var lastMaterializedRow = firstVisibleRow + visibleRows + CacheRowsAfter;
        var firstIndex = Math.Max(0, firstMaterializedRow * columns);
        var lastIndex = Math.Min(itemCount - 1, ((lastMaterializedRow + 1) * columns) - 1);
        return (firstIndex, lastIndex);
    }

    private static bool AreClose(Size left, Size right) =>
        Math.Abs(left.Width - right.Width) < 0.1
        && Math.Abs(left.Height - right.Height) < 0.1;
}
