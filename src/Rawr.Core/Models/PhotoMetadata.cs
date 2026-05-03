namespace Rawr.Core.Models;

/// <summary>
/// EXIF and camera metadata extracted from a RAW file.
/// Populated during background indexing via LibRaw's imgdata.other fields.
/// </summary>
public sealed class PhotoMetadata
{
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public string CameraMake { get; init; } = "";
    public string CameraModel { get; init; } = "";
    public string LensModel { get; init; } = "";
    public float ISO { get; init; }
    public float Aperture { get; init; }
    public float ShutterSpeed { get; init; }
    public float FocalLength { get; init; }
    public DateTime? CaptureTime { get; init; }
    public long FileSizeBytes { get; init; }

    public string ShutterSpeedFormatted =>
        ShutterSpeed >= 1 ? $"{ShutterSpeed:F1}s"
        : ShutterSpeed > 0 ? $"1/{1.0 / ShutterSpeed:F0}s"
        : "";

    public string ApertureFormatted =>
        Aperture > 0 ? $"f/{Aperture:F1}" : "";

    public string FocalLengthFormatted =>
        FocalLength > 0 ? $"{FocalLength:F0}mm" : "";

    public string CaptureDateFormatted =>
        CaptureTime.HasValue ? CaptureTime.Value.ToString("dd-MM-yyyy  HH:mm:ss") : "";

    public string ISOFormatted =>
        ISO > 0 ? $"ISO {ISO:F0}" : "";

    /// <summary>
    /// Make + Model with the brand prefix de-duplicated. Canon writes Make="Canon"
    /// and Model="EOS R5", but some cameras embed the brand in Model already.
    /// </summary>
    public string CameraFormatted
    {
        get
        {
            var make  = CameraMake.Trim();
            var model = CameraModel.Trim();
            if (string.IsNullOrEmpty(make))  return model;
            if (string.IsNullOrEmpty(model)) return make;
            if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model;
            return $"{make} {model}";
        }
    }

    public string DimensionsFormatted =>
        WidthPx > 0 && HeightPx > 0 ? $"{WidthPx} × {HeightPx}" : "";

    public string FileSizeFormatted
    {
        get
        {
            if (FileSizeBytes <= 0) return "";
            const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
            return FileSizeBytes switch
            {
                >= (long)GB => $"{FileSizeBytes / GB:F2} GB",
                >= (long)MB => $"{FileSizeBytes / MB:F1} MB",
                >= (long)KB => $"{FileSizeBytes / KB:F0} KB",
                _           => $"{FileSizeBytes} B",
            };
        }
    }
}
