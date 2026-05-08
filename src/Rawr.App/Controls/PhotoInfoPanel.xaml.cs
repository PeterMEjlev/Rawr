using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App.Controls;

public partial class PhotoInfoPanel : UserControl
{
    public static readonly DependencyProperty PhotoProperty = DependencyProperty.Register(
        nameof(Photo), typeof(PhotoItem), typeof(PhotoInfoPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty CaptureDateTextProperty = DependencyProperty.Register(
        nameof(CaptureDateText), typeof(string), typeof(PhotoInfoPanel), new PropertyMetadata(""));

    public static readonly DependencyProperty HistogramDataProperty = DependencyProperty.Register(
        nameof(HistogramData), typeof(HistogramData), typeof(PhotoInfoPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty HistogramModeProperty = DependencyProperty.Register(
        nameof(HistogramMode), typeof(HistogramMode), typeof(PhotoInfoPanel), new PropertyMetadata(HistogramMode.Rgb));

    public static readonly DependencyProperty SidePanelViewProperty = DependencyProperty.Register(
        nameof(SidePanelView), typeof(SidePanelView), typeof(PhotoInfoPanel), new PropertyMetadata(SidePanelView.Histogram));

    public static readonly DependencyProperty SetHistogramModeCommandProperty = DependencyProperty.Register(
        nameof(SetHistogramModeCommand), typeof(ICommand), typeof(PhotoInfoPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty SetSidePanelViewCommandProperty = DependencyProperty.Register(
        nameof(SetSidePanelViewCommand), typeof(ICommand), typeof(PhotoInfoPanel), new PropertyMetadata(null));

    public PhotoInfoPanel()
    {
        InitializeComponent();
    }

    public PixelPeekView PixelPeekView => PixelPeekViewControl;

    public PhotoItem? Photo
    {
        get => (PhotoItem?)GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    public string CaptureDateText
    {
        get => (string)GetValue(CaptureDateTextProperty);
        set => SetValue(CaptureDateTextProperty, value);
    }

    public HistogramData? HistogramData
    {
        get => (HistogramData?)GetValue(HistogramDataProperty);
        set => SetValue(HistogramDataProperty, value);
    }

    public HistogramMode HistogramMode
    {
        get => (HistogramMode)GetValue(HistogramModeProperty);
        set => SetValue(HistogramModeProperty, value);
    }

    public SidePanelView SidePanelView
    {
        get => (SidePanelView)GetValue(SidePanelViewProperty);
        set => SetValue(SidePanelViewProperty, value);
    }

    public ICommand? SetHistogramModeCommand
    {
        get => (ICommand?)GetValue(SetHistogramModeCommandProperty);
        set => SetValue(SetHistogramModeCommandProperty, value);
    }

    public ICommand? SetSidePanelViewCommand
    {
        get => (ICommand?)GetValue(SetSidePanelViewCommandProperty);
        set => SetValue(SetSidePanelViewCommandProperty, value);
    }
}
