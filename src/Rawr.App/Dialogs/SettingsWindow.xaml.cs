using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rawr.App.Shortcuts;
using Rawr.App.ViewModels;

namespace Rawr.App.Dialogs;

public partial class SettingsWindow : Window
{
    private static readonly DateTime PreviewDate = new(2026, 4, 29, 14, 35, 52);

    private static readonly (string Label, SortField Value)[] SortOptions =
    [
        ("File name",   SortField.FileName),
        ("Rating",      SortField.Rating),
        ("Date",        SortField.CaptureDate),
        ("Color",       SortField.ColorLabel),
        ("Flag",        SortField.Flag),
        ("Burst",       SortField.Burst),
        ("Image type",  SortField.ImageType),
    ];

    public AppSettings? Result { get; private set; }

    // Per-action working copy of the override map.
    //   key missing  → use default
    //   value ""     → explicitly unbound
    //   value "X+Y"  → custom binding
    private readonly Dictionary<string, string> _editedBindings;
    private readonly Dictionary<string, Button> _bindingButtons = new();

    private string? _recordingActionId;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        _editedBindings = new Dictionary<string, string>(current.KeyBindings);

        // Populate sort combo
        foreach (var (label, _) in SortOptions)
            SortFieldBox.Items.Add(label);

        // Load current values into controls
        GapSlider.Value = Math.Clamp(current.BurstMaxGapSeconds, 1, 30);
        FocusPeakingStrictnessSlider.Value = Math.Clamp(current.FocusPeakingThreshold, (byte)10, (byte)100);
        ThumbHighestRated.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.HighestRated;
        ThumbFirstChronological.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.FirstChronological;
        CollapseOnOpen.IsChecked = current.CollapseBurstsOnOpen;
        DateFormatBox.Text = current.DateFormat;

        var sortIdx = Array.FindIndex(SortOptions, o => o.Value == current.DefaultSortField);
        SortFieldBox.SelectedIndex = sortIdx >= 0 ? sortIdx : 0;

        UpdateDatePreview(current.DateFormat);
        BuildShortcutsUi();

        PreviewKeyDown += OnPreviewKeyDownCapture;
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

    // ── Keyboard shortcuts ──

    private void BuildShortcutsUi()
    {
        ShortcutsHost.Children.Clear();
        _bindingButtons.Clear();

        string? currentCategory = null;
        foreach (var action in ShortcutRegistry.All)
        {
            if (action.Category != currentCategory)
            {
                currentCategory = action.Category;
                var header = new TextBlock
                {
                    Text = currentCategory.ToUpperInvariant(),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextDimBrush"),
                    Margin = new Thickness(0, 8, 0, 4),
                };
                ShortcutsHost.Children.Add(header);
            }

            ShortcutsHost.Children.Add(BuildRow(action));
        }
    }

    private Grid BuildRow(ShortcutAction action)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = action.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var bindButton = new Button
        {
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Tag = action.Id,
            ToolTip = "Click to record a new key combination",
        };
        bindButton.Click += BindButton_Click;
        Grid.SetColumn(bindButton, 1);
        grid.Children.Add(bindButton);
        _bindingButtons[action.Id] = bindButton;

        var resetButton = new Button
        {
            Content = "↺",
            Padding = new Thickness(6, 3, 6, 3),
            Tag = action.Id,
            ToolTip = "Reset to default",
        };
        resetButton.Click += ResetButton_Click;
        Grid.SetColumn(resetButton, 2);
        grid.Children.Add(resetButton);

        UpdateBindingButton(action);
        return grid;
    }

    private void UpdateBindingButton(ShortcutAction action)
    {
        if (!_bindingButtons.TryGetValue(action.Id, out var btn)) return;

        if (_recordingActionId == action.Id)
        {
            btn.Content = "Press a key…";
            return;
        }

        var (spec, unbound) = ShortcutBinder.ResolveBinding(SettingsSnapshot(), action);
        btn.Content = unbound ? "(unbound)" : (spec?.ToString() ?? "(unbound)");
    }

    private AppSettings SettingsSnapshot() => new()
    {
        KeyBindings = new Dictionary<string, string>(_editedBindings),
    };

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        // If we were already recording for another row, stop that one first.
        if (_recordingActionId is { } prev && prev != id)
        {
            var prevAction = ShortcutRegistry.All.FirstOrDefault(a => a.Id == prev);
            _recordingActionId = null;
            if (prevAction is not null) UpdateBindingButton(prevAction);
        }

        _recordingActionId = id;
        var action = ShortcutRegistry.All.FirstOrDefault(a => a.Id == id);
        if (action is not null) UpdateBindingButton(action);
        Keyboard.Focus(btn);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var action = ShortcutRegistry.All.FirstOrDefault(a => a.Id == id);
        if (action is null) return;

        _editedBindings.Remove(id);
        if (_recordingActionId == id) _recordingActionId = null;
        UpdateBindingButton(action);
    }

    private void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _editedBindings.Clear();
        _recordingActionId = null;
        foreach (var a in ShortcutRegistry.All) UpdateBindingButton(a);
    }

    private void OnPreviewKeyDownCapture(object sender, KeyEventArgs e)
    {
        if (_recordingActionId is null) return;

        // Ignore standalone modifier keys; wait for the actual character/function key.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeySpec.IsModifierKey(key)) return;

        var actionId = _recordingActionId;
        var action = ShortcutRegistry.All.FirstOrDefault(a => a.Id == actionId);
        if (action is null)
        {
            _recordingActionId = null;
            return;
        }

        if (key == Key.Escape)
        {
            // Cancel recording, leave existing binding alone.
            _recordingActionId = null;
            UpdateBindingButton(action);
            e.Handled = true;
            return;
        }

        if (key == Key.Back)
        {
            // Clear the binding (explicit unbound).
            _editedBindings[actionId] = string.Empty;
            _recordingActionId = null;
            UpdateBindingButton(action);
            e.Handled = true;
            return;
        }

        var mods = Keyboard.Modifiers;
        var spec = new KeySpec(key, mods);

        // If this matches the action's default exactly, drop the override entirely.
        if (spec == action.DefaultBinding)
            _editedBindings.Remove(actionId);
        else
            _editedBindings[actionId] = spec.ToString();

        _recordingActionId = null;
        UpdateBindingButton(action);

        // Refresh other rows in case the binding text would change for any reason
        // (e.g. shared collision indicators in the future).
        e.Handled = true;
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
            FocusPeakingThreshold = (byte)FocusPeakingStrictnessSlider.Value,
            KeyBindings         = new Dictionary<string, string>(_editedBindings),
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void RootScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // WPF forwards trackpad scroll events at full native delta which feels too fast.
        // Scale it down to ~25 % of the raw delta (still responds to large swipes).
        const double scale = 0.25;
        RootScrollViewer.ScrollToVerticalOffset(RootScrollViewer.VerticalOffset - e.Delta * scale);
        e.Handled = true;
    }
}
