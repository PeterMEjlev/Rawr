using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rawr.App.Controls;
using Rawr.App.Services;
using Rawr.App.Shortcuts;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

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

    /// <summary>
    /// True when the user clicked "Re-run face analysis" — the caller should
    /// clear FaceCount/ClosedEyeCount/MinEyeOpenScore on every photo and
    /// re-run the analyzer with the (possibly updated) threshold.
    /// </summary>
    public bool RequestRerunFaceAnalysis { get; private set; }

    /// <summary>
    /// True when the user clicked "Re-run subject classification" — the caller
    /// should clear SubjectTags on every photo and re-run the classifier.
    /// </summary>
    public bool RequestRerunSubjectClassification { get; private set; }

    // The settings the dialog opened with. Save_Click starts from a clone of
    // this so fields the dialog doesn't expose (import routing, last import
    // destination, linear-RAW cache budget, the subfolder toggle) survive a
    // save instead of silently resetting to their defaults.
    private readonly AppSettings _original;

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

    private readonly Dictionary<string, Button> _macroKeyButtons = new();
    private string? _recordingMacroId;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        _original = current;
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
        SetClosedEyeMode(current.ClosedEyeDetectionMode);
        SetSubjectMode(current.SubjectClassificationMode);
        SubjectThresholdPersonSlider.Value       = Math.Clamp(current.GetSubjectGroupThreshold(SubjectTag.Person),       (byte)5, (byte)50);
        SubjectThresholdAnimalSlider.Value       = Math.Clamp(current.GetSubjectGroupThreshold(SubjectTag.Animal),       (byte)5, (byte)50);
        SubjectThresholdVehicleSlider.Value      = Math.Clamp(current.GetSubjectGroupThreshold(SubjectTag.Vehicle),      (byte)5, (byte)50);
        SubjectThresholdNatureSlider.Value       = Math.Clamp(current.GetSubjectGroupThreshold(SubjectTag.Nature),       (byte)5, (byte)50);
        SubjectThresholdArchitectureSlider.Value = Math.Clamp(current.GetSubjectGroupThreshold(SubjectTag.Architecture), (byte)5, (byte)50);
        SubjectThresholdFoodSlider.Value         = Math.Clamp(current.GetSubjectGroupThreshold(SubjectTag.Food),         (byte)5, (byte)50);
        ClippingModeHighlights.IsChecked = current.ClippingMode == ClippingMode.Highlights;
        ClippingModeShadows.IsChecked = current.ClippingMode == ClippingMode.Shadows;
        ClippingModeBoth.IsChecked = current.ClippingMode == ClippingMode.Both;
        ThumbHighestRated.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.HighestRated;
        ThumbFirstChronological.IsChecked = current.BurstThumbnailMode == BurstThumbnailMode.FirstChronological;
        CollapseOnOpen.IsChecked = current.CollapseBurstsOnOpen;
        BurstLabelColorBox.Text = current.BurstLabelColor;  // TextChanged updates the swatch
        DateFormatBox.Text = current.DateFormat;
        DoubleClickZoomSlider.Value = Math.Clamp(current.DoubleClickZoom, 1.5, 16.0);
        ScrollSpeedSlider.Value = Math.Clamp(current.ScrollSpeedPercent, ScrollSpeed.MinPercent, ScrollSpeed.MaxPercent);
        ReverseFilmstripScrollCheck.IsChecked = current.ReverseFilmstripScroll;
        VideoSeekStepSlider.Value = Math.Clamp(current.VideoSeekStepSeconds, 1, 30);
        AutoPlayVideoCheck.IsChecked = current.AutoPlayVideo;
        UseEmbeddedJpegOnlyCheck.IsChecked = current.UseEmbeddedJpegOnly;
        ShowSubjectScoresCheck.IsChecked = current.ShowSubjectClassifierScores;

        // Zoom / exposure (General tab)
        LoadValueCombo(MaxZoomCombo, current.MaxZoom, MaxZoomOptions);
        ZoomStepSlider.Value = Math.Clamp(current.ZoomStep, 1.05, 2.0);
        SetExposureStep(current.ExposureStepEv);

        // Face detection confidence (Classification tab)
        FaceConfidenceSlider.Value = Math.Clamp((int)current.FaceDetectionConfidence, 20, 90);

        // Video proxy (Video tab)
        LoadValueCombo(VideoProxyMaxWidthCombo, current.VideoProxyMaxWidth, ProxyMaxWidthOptions);
        LoadValueCombo(VideoProxyFpsCombo, current.VideoProxyFps, ProxyFpsOptions);
        VideoProxyCrfSlider.Value = Math.Clamp(current.VideoProxyCrf, 18, 40);

        // Performance tab — cache & preview
        CacheJpegQualitySlider.Value = Math.Clamp((int)current.CacheJpegQuality, 50, 100);
        LoadValueCombo(PreviewDecodeWidthCombo, current.PreviewDecodeWidth, PreviewWidthOptions);
        LoadValueCombo(LinearRawPreviewWidthCombo, current.LinearRawPreviewWidth, LinearRawWidthOptions);
        LoadValueCombo(ThumbnailDecodeWidthCombo, current.ThumbnailDecodeWidth, ThumbnailWidthOptions);
        LoadValueCombo(GridThumbnailRenderWidthCombo, current.GridThumbnailRenderWidth, GridRenderWidthOptions);
        // Performance tab — memory
        UndoHistoryDepthSlider.Value = Math.Clamp(current.UndoHistoryDepth, 10, 500);
        PreviewRetentionRadiusSlider.Value = Math.Clamp(current.PreviewRetentionRadius, 0, 10);
        GridCacheRowsBeforeSlider.Value = Math.Clamp(current.GridCacheRowsBefore, 0, 20);
        GridCacheRowsAfterSlider.Value = Math.Clamp(current.GridCacheRowsAfter, 0, 40);
        GridPreloadRowsBeforeSlider.Value = Math.Clamp(current.GridPreloadRowsBefore, 0, 20);
        GridPreloadRowsAfterSlider.Value = Math.Clamp(current.GridPreloadRowsAfter, 0, 60);
        // Performance tab — responsiveness
        CachedRawDecodeSettleDelaySlider.Value = Math.Clamp(current.CachedRawDecodeSettleDelayMs, 0, 500);
        RawDecodeSettleDelaySlider.Value = Math.Clamp(current.RawDecodeSettleDelayMs, 0, 1000);
        FullJpegPreloadSettleDelaySlider.Value = Math.Clamp(current.FullJpegPreloadSettleDelayMs, 0, 2000);
        RawPrefetchSettleDelaySlider.Value = Math.Clamp(current.RawPrefetchSettleDelayMs, 0, 3000);
        VideoProxyPrefetchSettleDelaySlider.Value = Math.Clamp(current.VideoProxyPrefetchSettleDelayMs, 0, 3000);
        SessionSaveDebounceSlider.Value = Math.Clamp(current.SessionSaveDebounceMs, 100, 3000);
        // Performance tab — background work
        LoadValueCombo(MaxBackgroundThreadsCombo, current.MaxBackgroundThreads, ThreadsOptions);

        // Performance presets: build the combos, select the preset matching the
        // values just loaded into the advanced sliders (or "Custom"), and wire the
        // two-way sync. Must run after the advanced sliders are populated.
        InitPerformancePresets();

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

        UpdateDatePreview(current.DateFormat);
        BuildShortcutsUi();
        BuildMacrosUi();

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

    // ── Burst label colour ──

    private void BurstLabelColorBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateBurstLabelPreview(BurstLabelColorBox.Text);

    private void UpdateBurstLabelPreview(string hex)
    {
        // Leave the last valid swatch showing while a half-typed hex is invalid.
        if (ThemeColors.TryParseColor(hex, out var c))
            BurstLabelPreviewSwatch.Background = new SolidColorBrush(c);
    }

    private void BurstLabelPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
            BurstLabelColorBox.Text = hex;
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

        // Start from a clone of the settings we opened with so anything the
        // dialog doesn't surface (import routing, last destination, cache
        // budget, subfolder toggle, …) is preserved; then overwrite only the
        // fields this dialog actually edits.
        var s = _original.Clone();

        s.BurstMaxGapSeconds  = (int)GapSlider.Value;
        s.BurstSimilarityStrictness = (int)SimilaritySlider.Value;
        s.BurstThumbnailMode  = ThumbFirstChronological.IsChecked == true
                                    ? BurstThumbnailMode.FirstChronological
                                    : BurstThumbnailMode.HighestRated;
        s.DateFormat          = string.IsNullOrWhiteSpace(DateFormatBox.Text)
                                    ? "dd-MM-yyyy  HH:mm:ss"
                                    : DateFormatBox.Text;
        s.CollapseBurstsOnOpen = CollapseOnOpen.IsChecked == true;
        // Keep the previous colour if the box holds an unparseable hex.
        s.BurstLabelColor     = ThemeColors.TryParseColor(BurstLabelColorBox.Text, out _)
                                    ? BurstLabelColorBox.Text.Trim()
                                    : _original.BurstLabelColor;
        s.DefaultSortField    = SortOptions[sortIdx].Value;
        s.FocusPeakingThreshold = (byte)FocusPeakingStrictnessSlider.Value;
        s.ClippingMode        = ClippingModeShadows.IsChecked == true ? ClippingMode.Shadows
                              : ClippingModeBoth.IsChecked    == true ? ClippingMode.Both
                              : ClippingMode.Highlights;
        s.ClippingThreshold   = (byte)ClippingThresholdSlider.Value;
        s.ClippedAreaThreshold = (byte)ClippedAreaThresholdSlider.Value;
        s.ClosedEyeThreshold = (byte)ClosedEyeThresholdSlider.Value;
        s.ClosedEyeDetectionMode = ReadClosedEyeMode();
        s.SubjectClassificationMode = ReadSubjectMode();
        s.SubjectGroupThresholds[nameof(SubjectTag.Person)]       = (byte)SubjectThresholdPersonSlider.Value;
        s.SubjectGroupThresholds[nameof(SubjectTag.Animal)]       = (byte)SubjectThresholdAnimalSlider.Value;
        s.SubjectGroupThresholds[nameof(SubjectTag.Vehicle)]      = (byte)SubjectThresholdVehicleSlider.Value;
        s.SubjectGroupThresholds[nameof(SubjectTag.Nature)]       = (byte)SubjectThresholdNatureSlider.Value;
        s.SubjectGroupThresholds[nameof(SubjectTag.Architecture)] = (byte)SubjectThresholdArchitectureSlider.Value;
        s.SubjectGroupThresholds[nameof(SubjectTag.Food)]         = (byte)SubjectThresholdFoodSlider.Value;
        s.DoubleClickZoom     = DoubleClickZoomSlider.Value;
        s.ScrollSpeedPercent  = (int)ScrollSpeedSlider.Value;
        s.ReverseFilmstripScroll = ReverseFilmstripScrollCheck.IsChecked == true;
        s.VideoSeekStepSeconds = (int)VideoSeekStepSlider.Value;
        s.AutoPlayVideo       = AutoPlayVideoCheck.IsChecked == true;
        s.UseEmbeddedJpegOnly = UseEmbeddedJpegOnlyCheck.IsChecked == true;
        s.ShowSubjectClassifierScores = ShowSubjectScoresCheck.IsChecked == true;
        s.HdrDetectionEnabled = HdrEnabledCheck.IsChecked == true;
        s.HdrMinBracketSize   = (int)HdrMinBracketSizeSlider.Value;
        s.HdrMinExposureSpread = (float)HdrExposureSpreadSlider.Value;
        s.PanoramaDetectionEnabled = PanoEnabledCheck.IsChecked == true;
        s.PanoramaMinChainSize = (int)PanoMinChainSlider.Value;
        s.PanoramaMaxGapSeconds = (int)PanoGapSlider.Value;
        s.PanoramaMinOverlapPct = (int)PanoMinOverlapSlider.Value;
        s.PanoramaMaxOverlapPct = (int)PanoMaxOverlapSlider.Value;
        s.PanoramaDirectionToleranceDeg = (int)PanoDirectionSlider.Value;
        s.KeyBindings         = new Dictionary<string, string>(_editedBindings);

        // Zoom / exposure
        s.MaxZoom             = ReadValueCombo(MaxZoomCombo, _original.MaxZoom);
        s.ZoomStep            = ZoomStepSlider.Value;
        s.ExposureStepEv      = ReadExposureStep();
        // Face detection
        s.FaceDetectionConfidence = (byte)FaceConfidenceSlider.Value;
        // Video proxy
        s.VideoProxyMaxWidth  = (int)ReadValueCombo(VideoProxyMaxWidthCombo, _original.VideoProxyMaxWidth);
        s.VideoProxyFps       = (int)ReadValueCombo(VideoProxyFpsCombo, _original.VideoProxyFps);
        s.VideoProxyCrf       = (int)VideoProxyCrfSlider.Value;
        // Performance — cache & preview
        s.CacheJpegQuality    = (byte)CacheJpegQualitySlider.Value;
        s.PreviewDecodeWidth  = (int)ReadValueCombo(PreviewDecodeWidthCombo, _original.PreviewDecodeWidth);
        s.LinearRawPreviewWidth = (int)ReadValueCombo(LinearRawPreviewWidthCombo, _original.LinearRawPreviewWidth);
        s.ThumbnailDecodeWidth = (int)ReadValueCombo(ThumbnailDecodeWidthCombo, _original.ThumbnailDecodeWidth);
        s.GridThumbnailRenderWidth = (int)ReadValueCombo(GridThumbnailRenderWidthCombo, _original.GridThumbnailRenderWidth);
        // Performance — memory
        s.UndoHistoryDepth    = (int)UndoHistoryDepthSlider.Value;
        s.PreviewRetentionRadius = (int)PreviewRetentionRadiusSlider.Value;
        s.GridCacheRowsBefore = (int)GridCacheRowsBeforeSlider.Value;
        s.GridCacheRowsAfter  = (int)GridCacheRowsAfterSlider.Value;
        s.GridPreloadRowsBefore = (int)GridPreloadRowsBeforeSlider.Value;
        s.GridPreloadRowsAfter = (int)GridPreloadRowsAfterSlider.Value;
        // Performance — responsiveness
        s.CachedRawDecodeSettleDelayMs = (int)CachedRawDecodeSettleDelaySlider.Value;
        s.RawDecodeSettleDelayMs = (int)RawDecodeSettleDelaySlider.Value;
        s.FullJpegPreloadSettleDelayMs = (int)FullJpegPreloadSettleDelaySlider.Value;
        s.RawPrefetchSettleDelayMs = (int)RawPrefetchSettleDelaySlider.Value;
        s.VideoProxyPrefetchSettleDelayMs = (int)VideoProxyPrefetchSettleDelaySlider.Value;
        s.SessionSaveDebounceMs = (int)SessionSaveDebounceSlider.Value;
        // Performance — background work
        s.MaxBackgroundThreads = (int)ReadValueCombo(MaxBackgroundThreadsCombo, _original.MaxBackgroundThreads);

        s.Macros              = _editedMacros.Select(m => new KeyboardMacro
        {
            Id = m.Id,
            Name = m.Name,
            KeyBinding = m.KeyBinding,
            SetFlag = m.SetFlag,
            SetRating = m.SetRating,
            SetColorLabel = m.SetColorLabel,
            TagName = m.TagName,
        }).ToList();

        Result = s;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Per-setting "revert to default" dispatcher. The XAML reset buttons set
    /// <c>Button.Tag</c> to a key identifying which control(s) to reset; this
    /// switch pulls the factory default from a fresh <see cref="AppSettings"/>
    /// instance and applies it. Keeps all per-setting reset logic in one
    /// place — adding a new setting only means a new <c>case</c> here plus an
    /// inline ↺ button in the XAML.
    /// </summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        var d = new AppSettings();
        switch (key)
        {
            // Display
            case "DateFormat":               DateFormatBox.Text = d.DateFormat; break;
            // Zoom
            case "DoubleClickZoom":          DoubleClickZoomSlider.Value = d.DoubleClickZoom; break;
            // Scrolling
            case "ScrollSpeedPercent":       ScrollSpeedSlider.Value = d.ScrollSpeedPercent; break;
            case "ReverseFilmstripScroll":   ReverseFilmstripScrollCheck.IsChecked = d.ReverseFilmstripScroll; break;
            // Preview
            case "UseEmbeddedJpegOnly":      UseEmbeddedJpegOnlyCheck.IsChecked = d.UseEmbeddedJpegOnly; break;
            // Debug
            case "ShowSubjectClassifierScores": ShowSubjectScoresCheck.IsChecked = d.ShowSubjectClassifierScores; break;
            // Sorting
            case "DefaultSortField":
                SortFieldBox.SelectedIndex = Math.Max(0, Array.FindIndex(SortOptions, o => o.Value == d.DefaultSortField));
                break;
            // Bursts
            case "BurstMaxGapSeconds":          GapSlider.Value = d.BurstMaxGapSeconds; break;
            case "BurstSimilarityStrictness":   SimilaritySlider.Value = d.BurstSimilarityStrictness; break;
            case "BurstThumbnailMode":
                ThumbHighestRated.IsChecked      = d.BurstThumbnailMode == BurstThumbnailMode.HighestRated;
                ThumbFirstChronological.IsChecked = d.BurstThumbnailMode == BurstThumbnailMode.FirstChronological;
                break;
            case "CollapseBurstsOnOpen":     CollapseOnOpen.IsChecked = d.CollapseBurstsOnOpen; break;
            // Colours
            case "BurstLabelColor":          BurstLabelColorBox.Text = d.BurstLabelColor; break;
            // HDR
            case "HdrDetectionEnabled":      HdrEnabledCheck.IsChecked = d.HdrDetectionEnabled; break;
            case "HdrMinBracketSize":        HdrMinBracketSizeSlider.Value = d.HdrMinBracketSize; break;
            case "HdrMinExposureSpread":     HdrExposureSpreadSlider.Value = d.HdrMinExposureSpread; break;
            // Panorama
            case "PanoramaDetectionEnabled":      PanoEnabledCheck.IsChecked = d.PanoramaDetectionEnabled; break;
            case "PanoramaMinChainSize":          PanoMinChainSlider.Value = d.PanoramaMinChainSize; break;
            case "PanoramaMaxGapSeconds":         PanoGapSlider.Value = d.PanoramaMaxGapSeconds; break;
            case "PanoramaMinOverlapPct":         PanoMinOverlapSlider.Value = d.PanoramaMinOverlapPct; break;
            case "PanoramaMaxOverlapPct":         PanoMaxOverlapSlider.Value = d.PanoramaMaxOverlapPct; break;
            case "PanoramaDirectionToleranceDeg": PanoDirectionSlider.Value = d.PanoramaDirectionToleranceDeg; break;
            // Clipping / exposure
            case "ClippingMode":
                ClippingModeHighlights.IsChecked = d.ClippingMode == ClippingMode.Highlights;
                ClippingModeShadows.IsChecked    = d.ClippingMode == ClippingMode.Shadows;
                ClippingModeBoth.IsChecked       = d.ClippingMode == ClippingMode.Both;
                break;
            case "ClippingThreshold":        ClippingThresholdSlider.Value = d.ClippingThreshold; break;
            case "ClippedAreaThreshold":     ClippedAreaThresholdSlider.Value = d.ClippedAreaThreshold; break;
            // Classification — faces
            case "ClosedEyeThreshold":       ClosedEyeThresholdSlider.Value = d.ClosedEyeThreshold; break;
            case "ClosedEyeDetectionMode":   SetClosedEyeMode(d.ClosedEyeDetectionMode); break;
            // Classification — subjects
            case "SubjectClassificationMode": SetSubjectMode(d.SubjectClassificationMode); break;
            case "SubjectThresholdPerson":       SubjectThresholdPersonSlider.Value       = d.GetSubjectGroupThreshold(SubjectTag.Person); break;
            case "SubjectThresholdAnimal":       SubjectThresholdAnimalSlider.Value       = d.GetSubjectGroupThreshold(SubjectTag.Animal); break;
            case "SubjectThresholdVehicle":      SubjectThresholdVehicleSlider.Value      = d.GetSubjectGroupThreshold(SubjectTag.Vehicle); break;
            case "SubjectThresholdNature":       SubjectThresholdNatureSlider.Value       = d.GetSubjectGroupThreshold(SubjectTag.Nature); break;
            case "SubjectThresholdArchitecture": SubjectThresholdArchitectureSlider.Value = d.GetSubjectGroupThreshold(SubjectTag.Architecture); break;
            case "SubjectThresholdFood":         SubjectThresholdFoodSlider.Value         = d.GetSubjectGroupThreshold(SubjectTag.Food); break;
            // Focus peaking
            case "FocusPeakingThreshold":    FocusPeakingStrictnessSlider.Value = d.FocusPeakingThreshold; break;
            // Video
            case "VideoSeekStepSeconds":     VideoSeekStepSlider.Value = d.VideoSeekStepSeconds; break;
            case "AutoPlayVideo":            AutoPlayVideoCheck.IsChecked = d.AutoPlayVideo; break;
            // Zoom / exposure
            case "MaxZoom":                  LoadValueCombo(MaxZoomCombo, d.MaxZoom, MaxZoomOptions); break;
            case "ZoomStep":                 ZoomStepSlider.Value = d.ZoomStep; break;
            case "ExposureStepEv":           SetExposureStep(d.ExposureStepEv); break;
            // Faces
            case "FaceDetectionConfidence":  FaceConfidenceSlider.Value = d.FaceDetectionConfidence; break;
            // Video proxy
            case "VideoProxyMaxWidth":       LoadValueCombo(VideoProxyMaxWidthCombo, d.VideoProxyMaxWidth, ProxyMaxWidthOptions); break;
            case "VideoProxyFps":            LoadValueCombo(VideoProxyFpsCombo, d.VideoProxyFps, ProxyFpsOptions); break;
            case "VideoProxyCrf":            VideoProxyCrfSlider.Value = d.VideoProxyCrf; break;
            // Performance — cache & preview
            case "CacheJpegQuality":         CacheJpegQualitySlider.Value = d.CacheJpegQuality; break;
            case "PreviewDecodeWidth":       LoadValueCombo(PreviewDecodeWidthCombo, d.PreviewDecodeWidth, PreviewWidthOptions); break;
            case "LinearRawPreviewWidth":    LoadValueCombo(LinearRawPreviewWidthCombo, d.LinearRawPreviewWidth, LinearRawWidthOptions); break;
            case "ThumbnailDecodeWidth":     LoadValueCombo(ThumbnailDecodeWidthCombo, d.ThumbnailDecodeWidth, ThumbnailWidthOptions); break;
            case "GridThumbnailRenderWidth": LoadValueCombo(GridThumbnailRenderWidthCombo, d.GridThumbnailRenderWidth, GridRenderWidthOptions); break;
            // Performance — memory
            case "UndoHistoryDepth":         UndoHistoryDepthSlider.Value = d.UndoHistoryDepth; break;
            case "PreviewRetentionRadius":   PreviewRetentionRadiusSlider.Value = d.PreviewRetentionRadius; break;
            case "GridCacheRowsBefore":      GridCacheRowsBeforeSlider.Value = d.GridCacheRowsBefore; break;
            case "GridCacheRowsAfter":       GridCacheRowsAfterSlider.Value = d.GridCacheRowsAfter; break;
            case "GridPreloadRowsBefore":    GridPreloadRowsBeforeSlider.Value = d.GridPreloadRowsBefore; break;
            case "GridPreloadRowsAfter":     GridPreloadRowsAfterSlider.Value = d.GridPreloadRowsAfter; break;
            // Performance — responsiveness
            case "CachedRawDecodeSettleDelayMs":   CachedRawDecodeSettleDelaySlider.Value = d.CachedRawDecodeSettleDelayMs; break;
            case "RawDecodeSettleDelayMs":         RawDecodeSettleDelaySlider.Value = d.RawDecodeSettleDelayMs; break;
            case "FullJpegPreloadSettleDelayMs":   FullJpegPreloadSettleDelaySlider.Value = d.FullJpegPreloadSettleDelayMs; break;
            case "RawPrefetchSettleDelayMs":       RawPrefetchSettleDelaySlider.Value = d.RawPrefetchSettleDelayMs; break;
            case "VideoProxyPrefetchSettleDelayMs": VideoProxyPrefetchSettleDelaySlider.Value = d.VideoProxyPrefetchSettleDelayMs; break;
            case "SessionSaveDebounceMs":          SessionSaveDebounceSlider.Value = d.SessionSaveDebounceMs; break;
            // Performance — background work
            case "MaxBackgroundThreads":     LoadValueCombo(MaxBackgroundThreadsCombo, d.MaxBackgroundThreads, ThreadsOptions); break;
        }
    }

    // ── Classification run-mode radio groups ──

    private void SetSubjectMode(ClassificationRunMode mode)
    {
        SubjectModeAuto.IsChecked   = mode == ClassificationRunMode.Auto;
        SubjectModeManual.IsChecked = mode == ClassificationRunMode.Manual;
        SubjectModeOff.IsChecked    = mode == ClassificationRunMode.Off;
    }

    private ClassificationRunMode ReadSubjectMode() =>
        SubjectModeManual.IsChecked == true ? ClassificationRunMode.Manual
      : SubjectModeOff.IsChecked == true    ? ClassificationRunMode.Off
      : ClassificationRunMode.Auto;

    private void SetClosedEyeMode(ClassificationRunMode mode)
    {
        ClosedEyeModeAuto.IsChecked   = mode == ClassificationRunMode.Auto;
        ClosedEyeModeManual.IsChecked = mode == ClassificationRunMode.Manual;
        ClosedEyeModeOff.IsChecked    = mode == ClassificationRunMode.Off;
    }

    private ClassificationRunMode ReadClosedEyeMode() =>
        ClosedEyeModeManual.IsChecked == true ? ClassificationRunMode.Manual
      : ClosedEyeModeOff.IsChecked == true    ? ClassificationRunMode.Off
      : ClassificationRunMode.Auto;

    // ── Exposure-step radio group (⅓ EV vs ½ EV) ──

    private void SetExposureStep(double ev)
    {
        // Snap whatever's persisted to the nearest of the two offered choices.
        bool half = Math.Abs(ev - 0.5) < Math.Abs(ev - (1.0 / 3.0));
        ExposureStepHalf.IsChecked = half;
        ExposureStepThird.IsChecked = !half;
    }

    private double ReadExposureStep() =>
        ExposureStepHalf.IsChecked == true ? 0.5 : 1.0 / 3.0;

    // ── Value combos (discrete / preset numeric settings) ──
    // Numeric value is stored in each ComboBoxItem.Tag (double) so selection maps
    // straight back to a number. A current value that isn't one of the presets
    // (e.g. hand-edited settings.json) is preserved as a synthesised first item so
    // loading never silently rounds it.

    private static readonly (string Label, double Value)[] MaxZoomOptions =
        [("8×", 8), ("16×", 16), ("32×", 32), ("64×", 64), ("128×", 128), ("256×", 256)];
    private static readonly (string Label, double Value)[] ProxyMaxWidthOptions =
        [("640 (360p)", 640), ("854 (480p)", 854), ("960 (540p)", 960), ("1280 (720p)", 1280), ("1600", 1600), ("1920 (1080p)", 1920)];
    private static readonly (string Label, double Value)[] ProxyFpsOptions =
        [("24", 24), ("25", 25), ("30", 30), ("48", 48), ("50", 50), ("60", 60)];
    private static readonly (string Label, double Value)[] ThreadsOptions =
        [("Auto", 0), ("1", 1), ("2", 2), ("3", 3), ("4", 4), ("6", 6), ("8", 8), ("12", 12), ("16", 16), ("24", 24), ("32", 32)];
    private static readonly (string Label, double Value)[] PreviewWidthOptions =
        [("1280 (720p)", 1280), ("1920 (1080p)", 1920), ("2560 (1440p)", 2560), ("3840 (4K)", 3840)];
    private static readonly (string Label, double Value)[] LinearRawWidthOptions =
        [("1600", 1600), ("2000", 2000), ("2400", 2400), ("3000", 3000), ("4000", 4000)];
    private static readonly (string Label, double Value)[] ThumbnailWidthOptions =
        [("160", 160), ("256", 256), ("320", 320), ("512", 512), ("640", 640)];
    private static readonly (string Label, double Value)[] GridRenderWidthOptions =
        [("160", 160), ("240", 240), ("320", 320), ("480", 480)];

    private static void LoadValueCombo(ComboBox combo, double value, (string Label, double Value)[] options)
    {
        combo.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var (label, v) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = v };
            combo.Items.Add(item);
            if (Math.Abs(v - value) < 0.0001) selected = item;
        }
        if (selected == null)
        {
            selected = new ComboBoxItem
            {
                Content = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                Tag = value,
            };
            combo.Items.Insert(0, selected);
        }
        combo.SelectedItem = selected;
    }

    private static double ReadValueCombo(ComboBox combo, double fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: double d } ? d : fallback;

    // ── Performance presets (one combo drives a group of advanced sliders) ──
    // The advanced sliders remain the source of truth (Save reads them); the preset
    // combo is a convenience that writes into them, and any manual slider edit flips
    // the combo to "Custom".

    private const string CustomPresetLabel = "Custom";

    // (label, cached, raw, fullJpeg, rawPrefetch, videoProxy, session) — milliseconds.
    // Labels name the concrete goal so the direction is unambiguous; ordered
    // low→high resource use (longest delays / least CPU → shortest delays / snappiest).
    private static readonly (string Label, int Cached, int Raw, int FullJpeg, int RawPrefetch, int VideoProxy, int Session)[] ResponsivenessPresets =
    [
        ("Lower CPU use",   90, 350, 700, 1200, 1300, 1000),
        ("Balanced",        45, 180, 350,  650,  700,  600),
        ("Faster previews", 20,  90, 180,  350,  400,  400),
    ];

    // (label, cacheBefore, cacheAfter, preloadBefore, preloadAfter) — rows.
    // Ordered low→high resource use (least preloading / least RAM → most preloading).
    private static readonly (string Label, int CacheB, int CacheA, int PreB, int PreA)[] PreloadPresets =
    [
        ("Lower memory use",    1,  2,  2,  4),
        ("Balanced",            3,  6,  4, 12),
        ("Smoother scrolling",  5, 10,  8, 24),
        ("Smoothest scrolling", 8, 16, 12, 40),
    ];

    private bool _syncingPerfPresets;

    private void InitPerformancePresets()
    {
        foreach (var p in ResponsivenessPresets) ResponsivenessPresetCombo.Items.Add(p.Label);
        ResponsivenessPresetCombo.Items.Add(CustomPresetLabel);
        foreach (var p in PreloadPresets) PreloadPresetCombo.Items.Add(p.Label);
        PreloadPresetCombo.Items.Add(CustomPresetLabel);

        SyncResponsivenessPresetSelection();
        SyncPreloadPresetSelection();

        // Wire change handlers only after the initial selection so the slider loads
        // above don't spuriously flip the combo to "Custom".
        ResponsivenessPresetCombo.SelectionChanged += ResponsivenessPreset_SelectionChanged;
        PreloadPresetCombo.SelectionChanged += PreloadPreset_SelectionChanged;

        foreach (var slider in new[]
                 {
                     CachedRawDecodeSettleDelaySlider, RawDecodeSettleDelaySlider,
                     FullJpegPreloadSettleDelaySlider, RawPrefetchSettleDelaySlider,
                     VideoProxyPrefetchSettleDelaySlider, SessionSaveDebounceSlider,
                 })
            slider.ValueChanged += (_, _) => { if (!_syncingPerfPresets) SelectCustom(ResponsivenessPresetCombo); };

        foreach (var slider in new[]
                 {
                     GridCacheRowsBeforeSlider, GridCacheRowsAfterSlider,
                     GridPreloadRowsBeforeSlider, GridPreloadRowsAfterSlider,
                 })
            slider.ValueChanged += (_, _) => { if (!_syncingPerfPresets) SelectCustom(PreloadPresetCombo); };

        // Start expanded only when the saved values don't map to a named preset, so
        // a "Custom" configuration is visible without the user hunting for it.
        ResponsivenessAdvancedToggle.IsChecked = (string?)ResponsivenessPresetCombo.SelectedItem == CustomPresetLabel;
        PreloadAdvancedToggle.IsChecked = (string?)PreloadPresetCombo.SelectedItem == CustomPresetLabel;
    }

    private static void SelectCustom(ComboBox combo)
    {
        if ((string?)combo.SelectedItem != CustomPresetLabel)
            combo.SelectedItem = CustomPresetLabel;
    }

    private void SyncResponsivenessPresetSelection()
    {
        string label = CustomPresetLabel;
        foreach (var p in ResponsivenessPresets)
        {
            if ((int)CachedRawDecodeSettleDelaySlider.Value == p.Cached
                && (int)RawDecodeSettleDelaySlider.Value == p.Raw
                && (int)FullJpegPreloadSettleDelaySlider.Value == p.FullJpeg
                && (int)RawPrefetchSettleDelaySlider.Value == p.RawPrefetch
                && (int)VideoProxyPrefetchSettleDelaySlider.Value == p.VideoProxy
                && (int)SessionSaveDebounceSlider.Value == p.Session)
            {
                label = p.Label;
                break;
            }
        }
        ResponsivenessPresetCombo.SelectedItem = label;
    }

    private void SyncPreloadPresetSelection()
    {
        string label = CustomPresetLabel;
        foreach (var p in PreloadPresets)
        {
            if ((int)GridCacheRowsBeforeSlider.Value == p.CacheB
                && (int)GridCacheRowsAfterSlider.Value == p.CacheA
                && (int)GridPreloadRowsBeforeSlider.Value == p.PreB
                && (int)GridPreloadRowsAfterSlider.Value == p.PreA)
            {
                label = p.Label;
                break;
            }
        }
        PreloadPresetCombo.SelectedItem = label;
    }

    private void ResponsivenessPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPerfPresets) return;
        if (ResponsivenessPresetCombo.SelectedItem is not string label) return;
        var p = Array.Find(ResponsivenessPresets, x => x.Label == label);
        if (p.Label is null) return; // "Custom" — leave the sliders untouched.

        _syncingPerfPresets = true;
        CachedRawDecodeSettleDelaySlider.Value = p.Cached;
        RawDecodeSettleDelaySlider.Value = p.Raw;
        FullJpegPreloadSettleDelaySlider.Value = p.FullJpeg;
        RawPrefetchSettleDelaySlider.Value = p.RawPrefetch;
        VideoProxyPrefetchSettleDelaySlider.Value = p.VideoProxy;
        SessionSaveDebounceSlider.Value = p.Session;
        _syncingPerfPresets = false;
    }

    private void PreloadPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPerfPresets) return;
        if (PreloadPresetCombo.SelectedItem is not string label) return;
        var p = Array.Find(PreloadPresets, x => x.Label == label);
        if (p.Label is null) return;

        _syncingPerfPresets = true;
        GridCacheRowsBeforeSlider.Value = p.CacheB;
        GridCacheRowsAfterSlider.Value = p.CacheA;
        GridPreloadRowsBeforeSlider.Value = p.PreB;
        GridPreloadRowsAfterSlider.Value = p.PreA;
        _syncingPerfPresets = false;
    }

    private void ResponsivenessAdvancedToggle_Changed(object sender, RoutedEventArgs e) =>
        ResponsivenessAdvancedPanel.Visibility =
            ResponsivenessAdvancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void PreloadAdvancedToggle_Changed(object sender, RoutedEventArgs e) =>
        PreloadAdvancedPanel.Visibility =
            PreloadAdvancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // The two re-run buttons reuse Save_Click so the latest threshold (and
    // any other settings edits in the dialog) get persisted before the
    // analyser fires — otherwise the user would have to Save first, then
    // re-open Settings to re-run, which defeats the point of the button.
    private void RerunFaceAnalysis_Click(object sender, RoutedEventArgs e)
    {
        RequestRerunFaceAnalysis = true;
        Save_Click(sender, e);
    }

    private void RerunSubjectClassification_Click(object sender, RoutedEventArgs e)
    {
        RequestRerunSubjectClassification = true;
        Save_Click(sender, e);
    }

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
            ToolTip = "Click to record a new key combination - Esc cancels, Backspace clears",
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

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        ScrollSpeed.ScrollVertical(sv, e);
        e.Handled = true;
    }

    // Enter commits the typed value to the slider immediately; otherwise the
    // binding only updates on LostFocus and the user has to tab away to see
    // the slider move. Escape reverts to the slider's current value.
    private void SliderValueBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var expr = tb.GetBindingExpression(TextBox.TextProperty);
        if (expr == null) return;

        if (e.Key == Key.Enter)
        {
            expr.UpdateSource();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            expr.UpdateTarget();
            e.Handled = true;
        }
    }
}
