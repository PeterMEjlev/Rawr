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

    public string ISOFormatted =>
        ISO > 0 ? $"ISO {ISO:F0}" : "";
}
