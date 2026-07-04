using System.Text.Json;
using Rawr.Core.Models;

namespace Rawr.Core.Data;

/// <summary>
/// Compact JSON (de)serialization of <see cref="PhotoMetadata"/> for the culling
/// database. Only the source fields are stored — the many computed/formatted
/// properties are re-derived on load — and the property names are abbreviated so
/// the per-photo row stays small across a large shoot.
///
/// CaptureTime is persisted as raw ticks and reconstructed as
/// <see cref="DateTimeKind.Unspecified"/>: EXIF wall-clock has no timezone, and
/// only the tick value feeds sorting/formatting, so the Kind is irrelevant.
/// </summary>
public static class PhotoMetadataSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Omit nulls so absent optional tags (bias/GPS/capture time) cost nothing.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PhotoMetadata m) =>
        JsonSerializer.Serialize(ToDto(m), Options);

    public static PhotoMetadata? Deserialize(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(json, Options);
            return d == null ? null : FromDto(d);
        }
        catch
        {
            return null;
        }
    }

    private static Dto ToDto(PhotoMetadata m) => new()
    {
        W = m.WidthPx,
        H = m.HeightPx,
        Mk = m.CameraMake,
        Md = m.CameraModel,
        Ln = m.LensModel,
        Iso = m.ISO,
        Ap = m.Aperture,
        Sh = m.ShutterSpeed,
        Fl = m.FocalLength,
        Eb = m.ExposureBias,
        Ct = m.CaptureTime?.Ticks,
        Fs = m.FileSizeBytes,
        La = m.GpsLatitude,
        Lo = m.GpsLongitude,
        Al = m.GpsAltitude,
    };

    private static PhotoMetadata FromDto(Dto d) => new()
    {
        WidthPx = d.W,
        HeightPx = d.H,
        CameraMake = d.Mk ?? "",
        CameraModel = d.Md ?? "",
        LensModel = d.Ln ?? "",
        ISO = d.Iso,
        Aperture = d.Ap,
        ShutterSpeed = d.Sh,
        FocalLength = d.Fl,
        ExposureBias = d.Eb,
        CaptureTime = d.Ct.HasValue ? new DateTime(d.Ct.Value, DateTimeKind.Unspecified) : null,
        FileSizeBytes = d.Fs,
        GpsLatitude = d.La,
        GpsLongitude = d.Lo,
        GpsAltitude = d.Al,
    };

    private sealed record Dto
    {
        public int W { get; init; }
        public int H { get; init; }
        public string Mk { get; init; } = "";
        public string Md { get; init; } = "";
        public string Ln { get; init; } = "";
        public float Iso { get; init; }
        public float Ap { get; init; }
        public float Sh { get; init; }
        public float Fl { get; init; }
        public float? Eb { get; init; }
        public long? Ct { get; init; }
        public long Fs { get; init; }
        public double? La { get; init; }
        public double? Lo { get; init; }
        public double? Al { get; init; }
    }
}
