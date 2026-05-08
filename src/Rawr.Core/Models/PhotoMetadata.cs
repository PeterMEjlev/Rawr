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

    // EXIF ExposureBiasValue (tag 0x9204), in stops. Null when the tag is absent
    // — common for non-bracketed shots; cameras only stamp this when the user
    // dialed in compensation or auto-bracketing offset the frame.
    public float? ExposureBias { get; init; }
    public DateTime? CaptureTime { get; init; }
    public long FileSizeBytes { get; init; }
    public double? GpsLatitude { get; init; }
    public double? GpsLongitude { get; init; }
    public double? GpsAltitude { get; init; }

    /// <summary>
    /// Composite exposure score in stops, used by HDR detection to compare frames in a burst.
    /// Returns ExposureBias when stamped (auto-bracket cameras write this directly), otherwise
    /// derives EV from aperture, shutter, and ISO so manual brackets are still detectable.
    /// Null when no exposure data is available at all.
    /// </summary>
    public float? ExposureScore
    {
        get
        {
            if (ExposureBias.HasValue) return ExposureBias.Value;
            if (Aperture > 0 && ShutterSpeed > 0 && ISO > 0)
            {
                double ev = Math.Log2((Aperture * Aperture) / ShutterSpeed * (100.0 / ISO));
                return (float)ev;
            }
            return null;
        }
    }

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

    public string? GpsLatFormatted =>
        GpsLatitude.HasValue
            ? $"{Math.Abs(GpsLatitude.Value):F5}° {(GpsLatitude.Value >= 0 ? "N" : "S")}"
            : null;

    public string? GpsLonFormatted =>
        GpsLongitude.HasValue
            ? $"{Math.Abs(GpsLongitude.Value):F5}° {(GpsLongitude.Value >= 0 ? "E" : "W")}"
            : null;

    public string? GpsAltFormatted =>
        GpsAltitude.HasValue ? $"{GpsAltitude.Value:F0} m" : null;
}
