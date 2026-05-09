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
    private const int DefaultDecodePixelWidth = 240;

    /// <summary>Target decode width in pixels. 0 = full resolution.</summary>
    public int DecodePixelWidth { get; set; } = DefaultDecodePixelWidth;

    /// <summary>Maximum decoded images kept hot for virtualized thumbnail views.</summary>
    public int MaxCachedImages
    {
        get
        {
            lock (Gate)
            {
                return _maxCachedImages;
            }
        }
        set
        {
            lock (Gate)
            {
                _maxCachedImages = Math.Max(0, value);
                TrimCache();
            }
        }
    }

    private static readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> Cache = new();
    private static readonly LinkedList<CacheEntry> Lru = new();
    private static readonly object Gate = new();
    private static int _maxCachedImages = 2048;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
            return null;

        return Preload(bytes, DecodePixelWidth);
    }

    public static BitmapSource? Preload(byte[]? bytes, int decodePixelWidth = DefaultDecodePixelWidth)
    {
        if (bytes is not { Length: > 0 })
            return null;

        var key = new CacheKey(bytes, Math.Max(0, decodePixelWidth));

        if (TryGetCached(key, out var cached))
            return cached;

        try
        {
            var image = Decode(key.Bytes, key.DecodePixelWidth);
            AddCached(key, image);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes, int decodePixelWidth)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0)
            image.DecodePixelWidth = decodePixelWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool TryGetCached(CacheKey key, out BitmapSource? image)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private static void AddCached(CacheKey key, BitmapSource image)
    {
        lock (Gate)
        {
            if (_maxCachedImages <= 0 || Cache.ContainsKey(key)) return;

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, image));
            Lru.AddFirst(node);
            Cache[key] = node;

            TrimCache();
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static void TrimCache()
    {
        while (Cache.Count > _maxCachedImages && Lru.Last != null)
        {
            var victim = Lru.Last;
            Lru.RemoveLast();
            Cache.Remove(victim.Value.Key);
        }
    }

    private readonly record struct CacheKey(byte[] Bytes, int DecodePixelWidth);

    private sealed record CacheEntry(CacheKey Key, BitmapSource Image);
}
