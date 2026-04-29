using System.Windows;
using System.Windows.Controls;
using Rawr.App.ViewModels;

namespace Rawr.App.Dialogs;

public partial class SettingsWindow : Window
{
    private static readonly DateTime PreviewDate = new(2026, 4, 29, 14, 35, 52);

    private static readonly (string Label, SortField Value)[] SortOptions =
    [
        ("File name", SortField.FileName),
        ("Rating",    SortField.Rating),
        ("Date",      SortField.CaptureDate),
        ("Color",     SortField.ColorLabel),
        ("Flag",      SortField.Flag),
        ("Burst",     SortField.Burst),
    ];

    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        // Populate sort combo
        foreach (var (label, _) in SortOptions)
            SortFieldBox.Items.Add(label);

        // Load current values into controls
        GapSlider.Value = Math.Clamp(current.BurstMaxGapSeconds, 1, 30);
        ThumbHighestRated.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.HighestRated;
        ThumbFirstChronological.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.FirstChronological;
        CollapseOnOpen.IsChecked = current.CollapseBurstsOnOpen;
        DateFormatBox.Text = current.DateFormat;

        var sortIdx = Array.FindIndex(SortOptions, o => o.Value == current.DefaultSortField);
        SortFieldBox.SelectedIndex = sortIdx >= 0 ? sortIdx : 0;

        UpdateDatePreview(current.DateFormat);
    }

    private void DateFormatBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateDatePreview(DateFormatBox.Text);

    private void UpdateDatePreview(string format)
    {
        try { DatePreview.Text = PreviewDate.ToString(format); }
        catch { DatePreview.Text = "(invalid format)"; }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string fmt)
            DateFormatBox.Text = fmt;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var sortIdx = Math.Max(0, SortFieldBox.SelectedIndex);
        Result = new AppSettings
        {
            BurstMaxGapSeconds  = (int)GapSlider.Value,
            BurstThumbnailMode  = ThumbFirstChronological.IsChecked == true
                                    ? BurstThumbnailMode.FirstChronological
                                    : BurstThumbnailMode.HighestRated,
            DateFormat          = string.IsNullOrWhiteSpace(DateFormatBox.Text)
                                    ? "dd-MM-yyyy  HH:mm:ss"
                                    : DateFormatBox.Text,
            CollapseBurstsOnOpen = CollapseOnOpen.IsChecked == true,
            DefaultSortField    = SortOptions[sortIdx].Value,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
