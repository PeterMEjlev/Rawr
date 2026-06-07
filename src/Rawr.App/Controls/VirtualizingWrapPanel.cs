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
/// Items implementing <see cref="IGridFullWidthItem"/> take a full row instead
/// of a single cell — used to render date separators between photo clusters.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    // Backed by AppSettings (Performance tab) so power users can trade RAM for
    // scroll smoothness. Defaults match the former constants. Clamped to >= 0 so a
    // stray negative in settings.json can't invert the materialization window.
    private static int CacheRowsBefore => Math.Max(0, AppSettings.Current.GridCacheRowsBefore);
    private static int CacheRowsAfter => Math.Max(0, AppSettings.Current.GridCacheRowsAfter);
    private static int PreloadRowsBefore => Math.Max(0, AppSettings.Current.GridPreloadRowsBefore);
    private static int PreloadRowsAfter => Math.Max(0, AppSettings.Current.GridPreloadRowsAfter);

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

    public static readonly DependencyProperty FullWidthItemHeightProperty =
        DependencyProperty.Register(
            nameof(FullWidthItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

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

    // Per-item layout maps, recomputed when item count / columns / membership change.
    // _rowTop[i] is the y of item i's row; _isFullWidth[i] tells the panel to span
    // the row. _rowBoundaries holds the cumulative y at each row break, so we can
    // binary-search which rows fall inside the viewport without scanning all items.
    private int[] _rowOfItem = [];
    private int[] _colOfItem = [];
    private bool[] _isFullWidth = [];
    private double[] _rowTop = [];
    private double[] _rowHeight = [];
    private int _totalRows;
    private bool _layoutDirty = true;

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

    public double FullWidthItemHeight
    {
        get => (double)GetValue(FullWidthItemHeightProperty);
        set => SetValue(FullWidthItemHeightProperty, value);
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
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? _viewport.Width : availableSize.Width;
        if (width <= 0)
            width = ItemWidth;

        var itemWidth = Math.Max(1.0, ItemWidth);
        var itemHeight = Math.Max(1.0, ItemHeight);
        var fullWidthHeight = Math.Max(1.0, FullWidthItemHeight);
        var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));

        if (_layoutDirty || columns != _lastColumns || itemCount != _lastItemCount
            || Math.Abs(itemHeight - _lastItemHeight) > 0.1)
        {
            RebuildLayout(owner, itemCount, columns, itemHeight, fullWidthHeight);
        }

        _lastColumns = columns;
        _lastItemCount = itemCount;
        _lastItemHeight = itemHeight;

        var totalHeight = _totalRows == 0 ? 0 : _rowTop[_totalRows - 1] + _rowHeight[_totalRows - 1];

        SetViewport(availableSize);
        SetExtent(new Size(width, totalHeight));
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

        var (firstIndex, lastIndex) = CalculateMaterializedRange(VerticalOffset, ViewportHeight, itemCount);

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

                if (newlyRealized || VisualTreeHelper.GetParent(child) != this)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.SetValue(RealizedIndexProperty, itemIndex);
                var w = _isFullWidth[itemIndex] ? (columns * itemWidth) : itemWidth;
                var h = _rowHeight[_rowOfItem[itemIndex]];
                child.Measure(new Size(w, h));
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
        var columns = Math.Max(1, _lastColumns);

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = (int)child.GetValue(RealizedIndexProperty);
            if (itemIndex < 0 || itemIndex >= _rowOfItem.Length)
                continue;

            var row = _rowOfItem[itemIndex];
            var top = _rowTop[row] - VerticalOffset;
            var height = _rowHeight[row];

            if (_isFullWidth[itemIndex])
            {
                child.Arrange(new Rect(0, top, columns * itemWidth, height));
            }
            else
            {
                var column = _colOfItem[itemIndex];
                child.Arrange(new Rect(column * itemWidth, top, itemWidth, height));
            }
        }

        return finalSize;
    }

    private void RebuildLayout(ItemsControl? owner, int itemCount, int columns, double tileHeight, double fullWidthHeight)
    {
        if (_rowOfItem.Length < itemCount)
        {
            _rowOfItem = new int[itemCount];
            _colOfItem = new int[itemCount];
            _isFullWidth = new bool[itemCount];
        }
        // Sized to itemCount upper-bound; in the worst case (every item is full-width)
        // we have itemCount rows. We grow once and reuse.
        if (_rowTop.Length < itemCount + 1)
        {
            _rowTop = new double[itemCount + 1];
            _rowHeight = new double[itemCount + 1];
        }

        int row = 0;
        int col = 0;
        double y = 0;
        double currentRowHeight = tileHeight;

        for (int i = 0; i < itemCount; i++)
        {
            var item = owner?.Items[i];
            var fullWidth = item is IGridFullWidthItem;
            _isFullWidth[i] = fullWidth;

            if (fullWidth)
            {
                // Flush any in-progress tile row: a header always starts a fresh row.
                if (col > 0)
                {
                    _rowTop[row] = y;
                    _rowHeight[row] = currentRowHeight;
                    y += currentRowHeight;
                    row++;
                    col = 0;
                }

                _rowOfItem[i] = row;
                _colOfItem[i] = 0;
                _rowTop[row] = y;
                _rowHeight[row] = fullWidthHeight;
                y += fullWidthHeight;
                row++;
                currentRowHeight = tileHeight;
            }
            else
            {
                if (col == 0)
                    currentRowHeight = tileHeight;

                _rowOfItem[i] = row;
                _colOfItem[i] = col;
                col++;

                if (col >= columns)
                {
                    _rowTop[row] = y;
                    _rowHeight[row] = currentRowHeight;
                    y += currentRowHeight;
                    row++;
                    col = 0;
                }
            }
        }

        // Close any trailing partial row.
        if (col > 0)
        {
            _rowTop[row] = y;
            _rowHeight[row] = currentRowHeight;
            row++;
        }

        _totalRows = row;
        _layoutDirty = false;
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _rowOfItem.Length)
            return;

        var row = _rowOfItem[index];
        var rowTop = _rowTop[row];
        var rowBottom = rowTop + _rowHeight[row];

        if (rowTop < VerticalOffset)
            SetVerticalOffset(rowTop);
        else if (rowBottom > VerticalOffset + ViewportHeight)
            SetVerticalOffset(rowBottom - ViewportHeight);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

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
        _layoutDirty = true;
        _preloadCts?.Cancel();
    }

    private void CleanupChildren(int firstIndex, int lastIndex)
    {
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

        var range = CalculateMaterializedRange(offset, ViewportHeight, _lastItemCount);
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

        var parallelism = AppSettings.CappedParallelism(Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2)));
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

    private (int FirstIndex, int LastIndex) CalculateMaterializedRange(
        double verticalOffset,
        double viewportHeight,
        int itemCount)
    {
        if (itemCount <= 0 || _totalRows <= 0)
            return (0, -1);

        // Binary search rows that intersect [verticalOffset, verticalOffset+viewportHeight].
        int firstRow = FindRowAtOrAbove(verticalOffset);
        int lastRow = FindRowAtOrAbove(verticalOffset + viewportHeight);
        if (lastRow >= _totalRows) lastRow = _totalRows - 1;

        firstRow = Math.Max(0, firstRow - CacheRowsBefore);
        lastRow = Math.Min(_totalRows - 1, lastRow + CacheRowsAfter);

        // Translate row range to item index range via a linear scan of _rowOfItem.
        // _rowOfItem is monotonically non-decreasing, so we can short-circuit once
        // we pass lastRow.
        int firstIndex = -1;
        int lastIndex = -1;
        for (int i = 0; i < itemCount; i++)
        {
            var r = _rowOfItem[i];
            if (r < firstRow) continue;
            if (r > lastRow) break;
            if (firstIndex < 0) firstIndex = i;
            lastIndex = i;
        }

        if (firstIndex < 0)
            return (0, -1);
        return (firstIndex, lastIndex);
    }

    private int FindRowAtOrAbove(double y)
    {
        if (_totalRows == 0) return 0;
        int lo = 0, hi = _totalRows - 1, result = _totalRows;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var rowBottom = _rowTop[mid] + _rowHeight[mid];
            if (rowBottom > y)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    private static bool AreClose(Size left, Size right) =>
        Math.Abs(left.Width - right.Width) < 0.1
        && Math.Abs(left.Height - right.Height) < 0.1;
}
