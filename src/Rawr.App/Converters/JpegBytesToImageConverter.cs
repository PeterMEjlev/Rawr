using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Rawr.App.Converters;

/// <summary>
/// Converts cached JPEG thumbnail bytes into frozen BitmapSources for binding.
/// Keeps a bounded decoded-image cache so virtualized grids can recycle cells
/// without repeatedly decoding the same thumbnail on the UI thread.
/// </summary>
public sealed class JpegBytesToImageConverter : IValueConverter
{
    /// <summary>Target decode width in pixels. 0 = full resolution.</summary>
    public int DecodePixelWidth { get; set; } = 240;

    /// <summary>Maximum decoded images kept hot for virtualized thumbnail views.</summary>
    public int MaxCachedImages { get; set; } = 512;

    private readonly Dictionary<byte[], LinkedListNode<CacheEntry>> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _gate = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
            return null;

        if (TryGetCached(bytes, out var cached))
            return cached;

        try
        {
            var image = Decode(bytes);
            AddCached(bytes, image);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (DecodePixelWidth > 0)
            image.DecodePixelWidth = DecodePixelWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private bool TryGetCached(byte[] bytes, out BitmapSource? image)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(bytes, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private void AddCached(byte[] bytes, BitmapSource image)
    {
        if (MaxCachedImages <= 0) return;

        lock (_gate)
        {
            if (_cache.ContainsKey(bytes)) return;

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(bytes, image));
            _lru.AddFirst(node);
            _cache[bytes] = node;

            while (_cache.Count > MaxCachedImages && _lru.Last != null)
            {
                var victim = _lru.Last;
                _lru.RemoveLast();
                _cache.Remove(victim.Value.Bytes);
            }
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private sealed record CacheEntry(byte[] Bytes, BitmapSource Image);
}
