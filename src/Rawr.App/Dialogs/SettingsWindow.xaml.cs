using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rawr.App.Controls;
using Rawr.App.Shortcuts;
using Rawr.App.ViewModels;
using Rawr.Core.Models;
using Rawr.Core.Services;

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

    // Working copy of macros — mutated in place by per-row event handlers, copied
    // back into Result on Save.
    private readonly List<KeyboardMacro> _editedMacros;

    // Working copy of LOG profile overrides — slider events mutate the preset in
    // place, Save_Click serializes back into Result.LogProfileOverrides.
    private readonly Dictionary<LogProfile, LogProfilePreset> _editedLogProfiles = new();
    private readonly Dictionary<string, Button> _macroKeyButtons = new();
    private string? _recordingMacroId;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        _editedBindings = new Dictionary<string, string>(current.KeyBindings);
        _editedMacros = current.Macros.Select(m => new KeyboardMacro
        {
            Id = m.Id,
            Name = m.Name,
            KeyBinding = m.KeyBinding,
            SetFlag = m.SetFlag,
            SetRating = m.SetRating,
            SetColorLabel = m.SetColorLabel,
            TagName = m.TagName,
        }).ToList();

        // Populate sort combo
        foreach (var (label, _) in SortOptions)
            SortFieldBox.Items.Add(label);

        // Load current values into controls
        GapSlider.Value = Math.Clamp(current.BurstMaxGapSeconds, 1, 30);
        SimilaritySlider.Value = Math.Clamp(current.BurstSimilarityStrictness, 0, 100);
        FocusPeakingStrictnessSlider.Value = Math.Clamp(current.FocusPeakingThreshold, (byte)10, (byte)100);
        ClippingThresholdSlider.Value = Math.Clamp(current.ClippingThreshold, (byte)90, (byte)100);
        ClippedAreaThresholdSlider.Value = Math.Clamp(current.ClippedAreaThreshold, (byte)1, (byte)50);
        ClosedEyeThresholdSlider.Value = Math.Clamp(current.ClosedEyeThreshold, (byte)10, (byte)90);
        ClippingModeHighlights.IsChecked = current.ClippingMode == ClippingMode.Highlights;
        ClippingModeShadows.IsChecked = current.ClippingMode == ClippingMode.Shadows;
        ClippingModeBoth.IsChecked = current.ClippingMode == ClippingMode.Both;
        ThumbHighestRated.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.HighestRated;
        ThumbFirstChronological.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.FirstChronological;
        CollapseOnOpen.IsChecked = current.CollapseBurstsOnOpen;
        DateFormatBox.Text = current.DateFormat;
        DoubleClickZoomSlider.Value = Math.Clamp(current.DoubleClickZoom, 1.5, 16.0);
        ScrollSpeedSlider.Value = Math.Clamp(current.ScrollSpeedPercent, ScrollSpeed.MinPercent, ScrollSpeed.MaxPercent);
        ReverseFilmstripScrollCheck.IsChecked = current.ReverseFilmstripScroll;
        VideoSeekStepSlider.Value = Math.Clamp(current.VideoSeekStepSeconds, 1, 30);
        AutoPlayVideoCheck.IsChecked = current.AutoPlayVideo;
        UseEmbeddedJpegOnlyCheck.IsChecked = current.UseEmbeddedJpegOnly;

        // HDR detection
        HdrEnabledCheck.IsChecked = current.HdrDetectionEnabled;
        HdrMinBracketSizeSlider.Value = Math.Clamp(current.HdrMinBracketSize, 2, 7);
        HdrExposureSpreadSlider.Value = Math.Clamp(current.HdrMinExposureSpread, 0.3f, 3.0f);

        // Panorama detection
        PanoEnabledCheck.IsChecked = current.PanoramaDetectionEnabled;
        PanoMinChainSlider.Value = Math.Clamp(current.PanoramaMinChainSize, 2, 7);
        PanoGapSlider.Value = Math.Clamp(current.PanoramaMaxGapSeconds, 1, 60);
        PanoMinOverlapSlider.Value = Math.Clamp(current.PanoramaMinOverlapPct, 5, 50);
        PanoMaxOverlapSlider.Value = Math.Clamp(current.PanoramaMaxOverlapPct, 50, 95);
        PanoDirectionSlider.Value = Math.Clamp(current.PanoramaDirectionToleranceDeg, 5, 90);

        // Keep min-overlap < max-overlap so the user can't invert the range.
        PanoMinOverlapSlider.ValueChanged += (_, _) =>
        {
            if (PanoMinOverlapSlider.Value >= PanoMaxOverlapSlider.Value)
                PanoMaxOverlapSlider.Value = Math.Min(95, PanoMinOverlapSlider.Value + 5);
        };
        PanoMaxOverlapSlider.ValueChanged += (_, _) =>
        {
            if (PanoMaxOverlapSlider.Value <= PanoMinOverlapSlider.Value)
                PanoMinOverlapSlider.Value = Math.Max(5, PanoMaxOverlapSlider.Value - 5);
        };

        var sortIdx = Array.FindIndex(SortOptions, o => o.Value == current.DefaultSortField);
        SortFieldBox.SelectedIndex = sortIdx >= 0 ? sortIdx : 0;

        // Seed LOG profile working copy from the current settings (or defaults).
        foreach (var p in Enum.GetValues<LogProfile>())
        {
            if (p == LogProfile.None) continue;
            _editedLogProfiles[p] = current.GetLogProfilePreset(p);
        }

        UpdateDatePreview(current.DateFormat);
        BuildShortcutsUi();
        BuildMacrosUi();
        BuildLogProfilesUi();

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
        btn.Content = unbound ? "(unbound)" : (spec?.FormatForDisplay() ?? "(unbound)");
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
        if (_recordingActionId is not null)
        {
            KeySpec.LogKeyDiagnostic($"shortcut:{_recordingActionId}", e);
            HandleShortcutKeyRecording(e);
        }
        else if (_recordingMacroId is not null)
        {
            KeySpec.LogKeyDiagnostic($"macro:{_recordingMacroId}", e);
            HandleMacroKeyRecording(e);
        }
    }

    private void HandleShortcutKeyRecording(KeyEventArgs e)
    {
        // Ignore standalone modifier keys; wait for the actual character/function key.
        var key = KeySpec.ResolveKey(e);
        if (KeySpec.IsModifierKey(key) || key == Key.None) return;

        var actionId = _recordingActionId!;
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
            BurstSimilarityStrictness = (int)SimilaritySlider.Value,
            BurstThumbnailMode  = ThumbFirstChronological.IsChecked == true
                                    ? BurstThumbnailMode.FirstChronological
                                    : BurstThumbnailMode.HighestRated,
            DateFormat          = string.IsNullOrWhiteSpace(DateFormatBox.Text)
                                    ? "dd-MM-yyyy  HH:mm:ss"
                                    : DateFormatBox.Text,
            CollapseBurstsOnOpen = CollapseOnOpen.IsChecked == true,
            DefaultSortField    = SortOptions[sortIdx].Value,
            FocusPeakingThreshold = (byte)FocusPeakingStrictnessSlider.Value,
            ClippingMode        = ClippingModeShadows.IsChecked == true ? ClippingMode.Shadows
                                : ClippingModeBoth.IsChecked    == true ? ClippingMode.Both
                                : ClippingMode.Highlights,
            ClippingThreshold   = (byte)ClippingThresholdSlider.Value,
            ClippedAreaThreshold = (byte)ClippedAreaThresholdSlider.Value,
            ClosedEyeThreshold = (byte)ClosedEyeThresholdSlider.Value,
            DoubleClickZoom     = DoubleClickZoomSlider.Value,
            ScrollSpeedPercent  = (int)ScrollSpeedSlider.Value,
            ReverseFilmstripScroll = ReverseFilmstripScrollCheck.IsChecked == true,
            VideoSeekStepSeconds = (int)VideoSeekStepSlider.Value,
            AutoPlayVideo       = AutoPlayVideoCheck.IsChecked == true,
            UseEmbeddedJpegOnly = UseEmbeddedJpegOnlyCheck.IsChecked == true,
            HdrDetectionEnabled = HdrEnabledCheck.IsChecked == true,
            HdrMinBracketSize   = (int)HdrMinBracketSizeSlider.Value,
            HdrMinExposureSpread = (float)HdrExposureSpreadSlider.Value,
            PanoramaDetectionEnabled = PanoEnabledCheck.IsChecked == true,
            PanoramaMinChainSize = (int)PanoMinChainSlider.Value,
            PanoramaMaxGapSeconds = (int)PanoGapSlider.Value,
            PanoramaMinOverlapPct = (int)PanoMinOverlapSlider.Value,
            PanoramaMaxOverlapPct = (int)PanoMaxOverlapSlider.Value,
            PanoramaDirectionToleranceDeg = (int)PanoDirectionSlider.Value,
            KeyBindings         = new Dictionary<string, string>(_editedBindings),
            LogProfileOverrides = BuildLogProfileOverrides(),
            Macros              = _editedMacros.Select(m => new KeyboardMacro
            {
                Id = m.Id,
                Name = m.Name,
                KeyBinding = m.KeyBinding,
                SetFlag = m.SetFlag,
                SetRating = m.SetRating,
                SetColorLabel = m.SetColorLabel,
                TagName = m.TagName,
            }).ToList(),
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ── Macros ──

    private static readonly (string Label, CullFlag? Value)[] MacroFlagOptions =
    [
        ("(no change)",    null),
        ("Pick",           CullFlag.Pick),
        ("Reject",         CullFlag.Reject),
        ("Unflag (clear)", CullFlag.Unflagged),
    ];

    private static readonly (string Label, int? Value)[] MacroRatingOptions =
    [
        ("(no change)", null),
        ("0 (clear)",   0),
        ("1",           1),
        ("2",           2),
        ("3",           3),
        ("4",           4),
        ("5",           5),
    ];

    private static readonly (string Label, ColorLabel? Value)[] MacroColorOptions =
    [
        ("(no change)",   null),
        ("None (clear)",  ColorLabel.None),
        ("Red",           ColorLabel.Red),
        ("Yellow",        ColorLabel.Yellow),
        ("Green",         ColorLabel.Green),
        ("Blue",          ColorLabel.Blue),
        ("Purple",        ColorLabel.Purple),
    ];

    private void BuildMacrosUi()
    {
        MacrosHost.Children.Clear();
        _macroKeyButtons.Clear();

        if (_editedMacros.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No macros yet. Click \"+ Add macro\" below.",
                Foreground = (Brush)FindResource("TextDimBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
            };
            MacrosHost.Children.Add(empty);
            return;
        }

        foreach (var macro in _editedMacros)
            MacrosHost.Children.Add(BuildMacroCard(macro));
    }

    private Border BuildMacroCard(KeyboardMacro macro)
    {
        var border = new Border
        {
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — Name + Key + Delete
        var row0 = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameLbl = new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(nameLbl, 0);
        row0.Children.Add(nameLbl);

        var nameBox = new TextBox { Text = macro.Name, Margin = new Thickness(0, 0, 12, 0) };
        nameBox.TextChanged += (_, _) => macro.Name = nameBox.Text;
        Grid.SetColumn(nameBox, 1);
        row0.Children.Add(nameBox);

        var keyLbl = new TextBlock { Text = "Key", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(keyLbl, 2);
        row0.Children.Add(keyLbl);

        var keyButton = new Button
        {
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Tag = macro.Id,
            ToolTip = "Click to record a new key combination — Esc cancels, Backspace clears",
        };
        keyButton.Click += MacroKeyButton_Click;
        Grid.SetColumn(keyButton, 3);
        row0.Children.Add(keyButton);
        _macroKeyButtons[macro.Id] = keyButton;
        UpdateMacroKeyButton(macro);

        var deleteButton = new Button
        {
            Content = "✕",
            Padding = new Thickness(6, 3, 6, 3),
            Tag = macro.Id,
            ToolTip = "Delete this macro",
        };
        deleteButton.Click += DeleteMacro_Click;
        Grid.SetColumn(deleteButton, 4);
        row0.Children.Add(deleteButton);

        Grid.SetRow(row0, 0);
        grid.Children.Add(row0);

        // Row 1 — Flag, Rating, Color
        var row1 = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var flagLbl = new TextBlock { Text = "Flag", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(flagLbl, 0);
        row1.Children.Add(flagLbl);

        var flagCombo = new ComboBox { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var (label, _) in MacroFlagOptions) flagCombo.Items.Add(label);
        flagCombo.SelectedIndex = Math.Max(0, Array.FindIndex(MacroFlagOptions, o => o.Value == macro.SetFlag));
        flagCombo.SelectionChanged += (_, _) =>
            macro.SetFlag = MacroFlagOptions[Math.Max(0, flagCombo.SelectedIndex)].Value;
        Grid.SetColumn(flagCombo, 1);
        row1.Children.Add(flagCombo);

        var ratingLbl = new TextBlock { Text = "Rating", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(ratingLbl, 2);
        row1.Children.Add(ratingLbl);

        var ratingCombo = new ComboBox { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var (label, _) in MacroRatingOptions) ratingCombo.Items.Add(label);
        ratingCombo.SelectedIndex = Math.Max(0, Array.FindIndex(MacroRatingOptions, o => o.Value == macro.SetRating));
        ratingCombo.SelectionChanged += (_, _) =>
            macro.SetRating = MacroRatingOptions[Math.Max(0, ratingCombo.SelectedIndex)].Value;
        Grid.SetColumn(ratingCombo, 3);
        row1.Children.Add(ratingCombo);

        var colorLbl = new TextBlock { Text = "Color", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(colorLbl, 4);
        row1.Children.Add(colorLbl);

        var colorCombo = new ComboBox();
        foreach (var (label, _) in MacroColorOptions) colorCombo.Items.Add(label);
        colorCombo.SelectedIndex = Math.Max(0, Array.FindIndex(MacroColorOptions, o => o.Value == macro.SetColorLabel));
        colorCombo.SelectionChanged += (_, _) =>
            macro.SetColorLabel = MacroColorOptions[Math.Max(0, colorCombo.SelectedIndex)].Value;
        Grid.SetColumn(colorCombo, 5);
        row1.Children.Add(colorCombo);

        Grid.SetRow(row1, 1);
        grid.Children.Add(row1);

        // Row 2 — Tag (auto-create if missing)
        var row2 = new Grid();
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tagLbl = new TextBlock { Text = "Tag", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(tagLbl, 0);
        row2.Children.Add(tagLbl);

        var tagBox = new TextBox
        {
            Text = macro.TagName ?? "",
            ToolTip = "Tag name to apply. Leave blank to skip. Created automatically if it doesn't exist.",
        };
        tagBox.TextChanged += (_, _) => macro.TagName = string.IsNullOrWhiteSpace(tagBox.Text) ? null : tagBox.Text;
        Grid.SetColumn(tagBox, 1);
        row2.Children.Add(tagBox);

        Grid.SetRow(row2, 2);
        grid.Children.Add(row2);

        border.Child = grid;
        return border;
    }

    private void UpdateMacroKeyButton(KeyboardMacro macro)
    {
        if (!_macroKeyButtons.TryGetValue(macro.Id, out var btn)) return;

        if (_recordingMacroId == macro.Id)
        {
            btn.Content = "Press a key…";
            return;
        }

        if (string.IsNullOrWhiteSpace(macro.KeyBinding))
        {
            btn.Content = "(unbound)";
            return;
        }

        var spec = KeySpec.TryParse(macro.KeyBinding);
        btn.Content = spec?.FormatForDisplay() ?? "(unbound)";
    }

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        _editedMacros.Add(new KeyboardMacro());
        BuildMacrosUi();
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        _editedMacros.RemoveAll(m => m.Id == id);
        if (_recordingMacroId == id) _recordingMacroId = null;
        BuildMacrosUi();
    }

    private void MacroKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        // Stop any other recording in progress.
        if (_recordingActionId is { } prevAction)
        {
            var prev = ShortcutRegistry.All.FirstOrDefault(a => a.Id == prevAction);
            _recordingActionId = null;
            if (prev is not null) UpdateBindingButton(prev);
        }
        if (_recordingMacroId is { } prevMacro && prevMacro != id)
        {
            var prev = _editedMacros.FirstOrDefault(m => m.Id == prevMacro);
            _recordingMacroId = null;
            if (prev is not null) UpdateMacroKeyButton(prev);
        }

        _recordingMacroId = id;
        var macro = _editedMacros.FirstOrDefault(m => m.Id == id);
        if (macro is not null) UpdateMacroKeyButton(macro);
        Keyboard.Focus(btn);
    }

    private void HandleMacroKeyRecording(KeyEventArgs e)
    {
        var key = KeySpec.ResolveKey(e);
        if (KeySpec.IsModifierKey(key) || key == Key.None) return;

        var macroId = _recordingMacroId!;
        var macro = _editedMacros.FirstOrDefault(m => m.Id == macroId);
        if (macro is null)
        {
            _recordingMacroId = null;
            return;
        }

        if (key == Key.Escape)
        {
            _recordingMacroId = null;
            UpdateMacroKeyButton(macro);
            e.Handled = true;
            return;
        }

        if (key == Key.Back)
        {
            macro.KeyBinding = "";
            _recordingMacroId = null;
            UpdateMacroKeyButton(macro);
            e.Handled = true;
            return;
        }

        var mods = Keyboard.Modifiers;
        var spec = new KeySpec(key, mods);

        var conflict = FindKeyConflict(spec, macro.Id);
        if (conflict is not null)
        {
            _recordingMacroId = null;
            UpdateMacroKeyButton(macro);
            MessageBox.Show(this,
                $"{spec} is already used by {conflict}.\n\nPick a different key or clear the conflicting binding first.",
                "Key already in use",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }

        macro.KeyBinding = spec.ToString();
        _recordingMacroId = null;
        UpdateMacroKeyButton(macro);
        e.Handled = true;
    }

    /// Returns a human-readable description of what already uses this key, or null
    /// if it's free. Checks every shortcut action's effective binding (including
    /// in-progress overrides) and every other macro's binding.
    private string? FindKeyConflict(KeySpec spec, string excludeMacroId)
    {
        var snapshot = SettingsSnapshot();
        foreach (var action in ShortcutRegistry.All)
        {
            var (s, _) = ShortcutBinder.ResolveBinding(snapshot, action);
            if (s is not null && s.Key == spec.Key && s.Modifiers == spec.Modifiers)
                return $"shortcut “{action.DisplayName}”";
        }
        foreach (var m in _editedMacros)
        {
            if (m.Id == excludeMacroId) continue;
            var s = KeySpec.TryParse(m.KeyBinding);
            if (s is not null && s.Key == spec.Key && s.Modifiers == spec.Modifiers)
                return $"macro “{(string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name)}”";
        }
        return null;
    }

    // ── LOG profile presets ──

    // Only persist profiles that differ from the built-in defaults; keeps
    // settings.json clean and lets us add or change defaults without churning
    // every saved file.
    private Dictionary<string, LogProfilePreset> BuildLogProfileOverrides()
    {
        var result = new Dictionary<string, LogProfilePreset>();
        foreach (var (profile, preset) in _editedLogProfiles)
        {
            var def = LogProfilePreset.For(profile);
            if (Math.Abs(preset.Contrast   - def.Contrast)   > 0.001f ||
                Math.Abs(preset.Saturation - def.Saturation) > 0.001f ||
                Math.Abs(preset.Gamma      - def.Gamma)      > 0.001f ||
                Math.Abs(preset.Brightness - def.Brightness) > 0.001f)
            {
                result[profile.ToString()] = preset.Clone();
            }
        }
        return result;
    }

    private void BuildLogProfilesUi()
    {
        LogProfilesHost.Children.Clear();
        foreach (var p in Enum.GetValues<LogProfile>())
        {
            if (p == LogProfile.None) continue;
            LogProfilesHost.Children.Add(BuildLogProfileCard(p));
        }
    }

    private Border BuildLogProfileCard(LogProfile profile)
    {
        var preset = _editedLogProfiles[profile];
        var border = new Border
        {
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var stack = new StackPanel();

        // Header: name + reset button
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var resetBtn = new Button
        {
            Content = "↺ Reset to default",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        resetBtn.Click += (_, _) =>
        {
            _editedLogProfiles[profile] = LogProfilePreset.For(profile);
            BuildLogProfilesUi();
        };
        DockPanel.SetDock(resetBtn, Dock.Right);
        headerRow.Children.Add(resetBtn);

        var name = new TextBlock
        {
            Text = LogProfileDetector.DisplayName(profile),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerRow.Children.Add(name);
        stack.Children.Add(headerRow);

        // Sliders
        stack.Children.Add(BuildAdjustSlider("Contrast",   0.0, 2.0, preset.Contrast,   v => preset.Contrast = v));
        stack.Children.Add(BuildAdjustSlider("Saturation", 0.0, 3.0, preset.Saturation, v => preset.Saturation = v));
        stack.Children.Add(BuildAdjustSlider("Gamma",      0.50, 1.50, preset.Gamma,    v => preset.Gamma = v));
        stack.Children.Add(BuildAdjustSlider("Brightness", 0.0, 2.0, preset.Brightness, v => preset.Brightness = v));

        border.Child = stack;
        return border;
    }

    private static Grid BuildAdjustSlider(string label, double min, double max, double initial, Action<float> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = initial,
            SmallChange = 0.01,
            LargeChange = 0.1,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        var valueText = new TextBlock
        {
            Text = initial.ToString("F2"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Consolas"),
        };
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);

        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = e.NewValue.ToString("F2");
            onChange((float)e.NewValue);
        };

        return grid;
    }

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        ScrollSpeed.ScrollVertical(sv, e);
        e.Handled = true;
    }
}
