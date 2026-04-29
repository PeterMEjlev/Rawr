using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App.Dialogs;

/// <summary>
/// Modal viewer for a single burst. Shows a large preview of the active frame
/// and a horizontal strip of every member of the burst. Edits made here mutate
/// the shared PhotoItem instances and persist via the parent MainViewModel,
/// so changes are reflected immediately in the main grid on close.
/// </summary>
public partial class BurstFocusWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly List<PhotoItem> _photos;
    private int _currentIndex = -1;
    private CancellationTokenSource? _previewCts;

    public IRelayCommand CloseCommand        { get; }
    public IRelayCommand NextCommand         { get; }
    public IRelayCommand PrevCommand         { get; }
    public IRelayCommand TogglePickCommand   { get; }
    public IRelayCommand ToggleRejectCommand { get; }
    public IRelayCommand UnflagCommand       { get; }
    public IRelayCommand<int> SetRatingCommand { get; }
    public IRelayCommand<ColorLabel> SetColorLabelCommand { get; }

    public BurstFocusWindow(MainViewModel vm, List<PhotoItem> photos, int startIndex)
    {
        _vm = vm;
        _photos = photos;

        CloseCommand        = new RelayCommand(Close);
        NextCommand         = new RelayCommand(() => MoveTo(_currentIndex + 1));
        PrevCommand         = new RelayCommand(() => MoveTo(_currentIndex - 1));
        TogglePickCommand   = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Pick   ? CullFlag.Unflagged : CullFlag.Pick));
        ToggleRejectCommand = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Reject ? CullFlag.Unflagged : CullFlag.Reject));
        UnflagCommand       = new RelayCommand(() => MutateCurrent(p => p.Flag = CullFlag.Unflagged));
        SetRatingCommand    = new RelayCommand<int>(r => MutateCurrent(p => p.Rating = Math.Clamp(r, 0, 5)));
        SetColorLabelCommand = new RelayCommand<ColorLabel>(l => MutateCurrent(p => p.ColorLabel = p.ColorLabel == l ? ColorLabel.None : l));

        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        Strip.ItemsSource = _photos;
        HeaderText.Text = $"Burst — {_photos.Count} photos";

        Loaded += (_, _) => MoveTo(Math.Clamp(startIndex, 0, _photos.Count - 1));
        Closed += (_, _) => _previewCts?.Cancel();
    }

    private void MoveTo(int index)
    {
        if (index < 0 || index >= _photos.Count) return;
        _currentIndex = index;
        Strip.SelectedIndex = index;
        Strip.ScrollIntoView(_photos[index]);
        UpdateOverlays();
        _ = LoadPreviewAsync(_photos[index]);
    }

    private void MutateCurrent(Action<PhotoItem> mutate)
    {
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;
        var photo = _photos[_currentIndex];
        mutate(photo);
        _vm.PersistPhoto(photo);
        UpdateOverlays();
    }

    private void UpdateOverlays()
    {
        if (_currentIndex < 0) return;
        var photo = _photos[_currentIndex];
        RatingText.Text = new string('★', photo.Rating);
        if (photo.Flag == CullFlag.Unflagged)
        {
            FlagBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            FlagBadge.Visibility = Visibility.Visible;
            FlagText.Text = photo.Flag == CullFlag.Pick ? "PICK" : "REJECT";
        }
        Title = $"Burst — {photo.FileName}  ({_currentIndex + 1}/{_photos.Count})";
    }

    private async Task LoadPreviewAsync(PhotoItem photo)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        // Show whatever bytes are already resident first for instant feedback.
        var initial = photo.PreviewJpeg ?? photo.ThumbnailJpeg;
        if (initial != null)
            PreviewImageElement.Source = LoadBitmap(initial);

        if (photo.PreviewJpeg != null) return;

        try
        {
            var jpeg = await Task.Run(() => _vm.Extractor.ExtractPreview(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null) return;
            photo.PreviewJpeg = jpeg;
            if (_currentIndex >= 0 && _photos[_currentIndex] == photo)
                PreviewImageElement.Source = LoadBitmap(jpeg);
        }
        catch (OperationCanceledException) { }
    }

    private static BitmapSource? LoadBitmap(byte[] jpeg)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(jpeg);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 1920;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private void Strip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (lb.SelectedIndex < 0 || lb.SelectedIndex == _currentIndex) return;
        MoveTo(lb.SelectedIndex);
    }

    private void Strip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var sv = FindScrollViewer(lb);
        if (sv == null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(System.Windows.DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
