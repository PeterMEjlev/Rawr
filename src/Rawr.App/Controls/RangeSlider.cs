using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Rawr.App.Controls;

/// <summary>
/// Two-handle slider that exposes <see cref="LowerValue"/> and <see cref="UpperValue"/>.
/// Used by the time-of-day filter where the user picks a start/end time by either
/// dragging the thumbs or typing into the bound text boxes alongside.
/// </summary>
public sealed class RangeSlider : Control
{
    static RangeSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RangeSlider),
            new FrameworkPropertyMetadata(typeof(RangeSlider)));
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(1.0, OnRangeChanged));

    public static readonly DependencyProperty LowerValueProperty = DependencyProperty.Register(
        nameof(LowerValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnRangeChanged,
            CoerceLower));

    public static readonly DependencyProperty UpperValueProperty = DependencyProperty.Register(
        nameof(UpperValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(
            1.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnRangeChanged,
            CoerceUpper));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0.0));

    public double Minimum    { get => (double)GetValue(MinimumProperty);    set => SetValue(MinimumProperty, value); }
    public double Maximum    { get => (double)GetValue(MaximumProperty);    set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => (double)GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => (double)GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }
    public double Step       { get => (double)GetValue(StepProperty);       set => SetValue(StepProperty, value); }

    private Thumb? _lowerThumb;
    private Thumb? _upperThumb;
    private Rectangle? _selectedRange;
    private FrameworkElement? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_lowerThumb != null) _lowerThumb.DragDelta -= OnLowerDragDelta;
        if (_upperThumb != null) _upperThumb.DragDelta -= OnUpperDragDelta;

        _track = GetTemplateChild("PART_Track") as FrameworkElement;
        _lowerThumb = GetTemplateChild("PART_LowerThumb") as Thumb;
        _upperThumb = GetTemplateChild("PART_UpperThumb") as Thumb;
        _selectedRange = GetTemplateChild("PART_SelectedRange") as Rectangle;

        if (_lowerThumb != null) _lowerThumb.DragDelta += OnLowerDragDelta;
        if (_upperThumb != null) _upperThumb.DragDelta += OnUpperDragDelta;

        UpdateLayoutPositions();
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var s = base.ArrangeOverride(arrangeBounds);
        UpdateLayoutPositions();
        return s;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_track == null) return;
        // Click-on-track snaps the nearer thumb to the click position.
        var x = e.GetPosition(_track).X;
        var v = PositionToValue(x);
        var midpoint = (LowerValue + UpperValue) / 2;
        if (v < midpoint) LowerValue = v;
        else UpperValue = v;
    }

    private void OnLowerDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_track == null || _track.ActualWidth <= 0) return;
        double range = Maximum - Minimum;
        if (range <= 0) return;
        double dv = e.HorizontalChange / _track.ActualWidth * range;
        LowerValue = Snap(LowerValue + dv);
    }

    private void OnUpperDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_track == null || _track.ActualWidth <= 0) return;
        double range = Maximum - Minimum;
        if (range <= 0) return;
        double dv = e.HorizontalChange / _track.ActualWidth * range;
        UpperValue = Snap(UpperValue + dv);
    }

    private double Snap(double v)
    {
        if (Step > 0)
        {
            v = Minimum + Math.Round((v - Minimum) / Step) * Step;
        }
        return v;
    }

    private double PositionToValue(double x)
    {
        if (_track == null || _track.ActualWidth <= 0) return Minimum;
        double range = Maximum - Minimum;
        var v = Minimum + (x / _track.ActualWidth) * range;
        return Math.Max(Minimum, Math.Min(Maximum, Snap(v)));
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var rs = (RangeSlider)d;
        // Re-coerce both bounds whenever any of the four anchor properties moves.
        rs.CoerceValue(LowerValueProperty);
        rs.CoerceValue(UpperValueProperty);
        rs.UpdateLayoutPositions();
    }

    private static object CoerceLower(DependencyObject d, object baseValue)
    {
        var rs = (RangeSlider)d;
        var v = (double)baseValue;
        if (v < rs.Minimum) v = rs.Minimum;
        if (v > rs.UpperValue) v = rs.UpperValue;
        return v;
    }

    private static object CoerceUpper(DependencyObject d, object baseValue)
    {
        var rs = (RangeSlider)d;
        var v = (double)baseValue;
        if (v > rs.Maximum) v = rs.Maximum;
        if (v < rs.LowerValue) v = rs.LowerValue;
        return v;
    }

    private void UpdateLayoutPositions()
    {
        if (_track == null || _lowerThumb == null || _upperThumb == null) return;
        double w = _track.ActualWidth;
        if (w <= 0) return;
        double range = Maximum - Minimum;
        if (range <= 0) return;

        double lowerX = (LowerValue - Minimum) / range * w;
        double upperX = (UpperValue - Minimum) / range * w;

        // Centre each thumb on its value position.
        var lowerHalf = _lowerThumb.ActualWidth / 2;
        var upperHalf = _upperThumb.ActualWidth / 2;
        Canvas.SetLeft(_lowerThumb, lowerX - lowerHalf);
        Canvas.SetLeft(_upperThumb, upperX - upperHalf);

        if (_selectedRange != null)
        {
            Canvas.SetLeft(_selectedRange, lowerX);
            _selectedRange.Width = Math.Max(0, upperX - lowerX);
        }
    }
}
