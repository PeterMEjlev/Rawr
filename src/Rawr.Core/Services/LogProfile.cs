namespace Rawr.Core.Services;

public enum LogProfile
{
    None,
    SLog3,
    CLog3,
    VLog,
    NLog,
    FLog,
    Flat,
}

// Mutable so the settings UI can edit slider-bound values directly. The MainWindow
// pulls a preset per-video at apply time, so live edits don't bleed across videos.
public sealed class LogProfilePreset
{
    public float Contrast   { get; set; } = 1.0f;
    public float Saturation { get; set; } = 1.0f;
    public float Gamma      { get; set; } = 1.0f;
    public float Brightness { get; set; } = 1.0f;

    public static LogProfilePreset Identity() => new();

    public static LogProfilePreset For(LogProfile profile) => profile switch
    {
        LogProfile.SLog3 => new() { Contrast = 1.20f, Saturation = 1.30f, Gamma = 0.88f, Brightness = 1.0f },
        LogProfile.CLog3 => new() { Contrast = 1.18f, Saturation = 1.25f, Gamma = 0.90f, Brightness = 1.0f },
        LogProfile.VLog  => new() { Contrast = 1.20f, Saturation = 1.25f, Gamma = 0.90f, Brightness = 1.0f },
        LogProfile.NLog  => new() { Contrast = 1.18f, Saturation = 1.28f, Gamma = 0.90f, Brightness = 1.0f },
        LogProfile.FLog  => new() { Contrast = 1.15f, Saturation = 1.25f, Gamma = 0.92f, Brightness = 1.0f },
        LogProfile.Flat  => new() { Contrast = 1.10f, Saturation = 1.20f, Gamma = 0.95f, Brightness = 1.0f },
        _                => Identity(),
    };

    public LogProfilePreset Clone() => new()
    {
        Contrast = Contrast,
        Saturation = Saturation,
        Gamma = Gamma,
        Brightness = Brightness,
    };
}

public static class LogProfileDetector
{
    public static LogProfile Detect(string? make, string? model)
    {
        if (string.IsNullOrWhiteSpace(make)) return LogProfile.None;
        var m = make.Trim().ToUpperInvariant();
        if (m.Contains("SONY"))      return LogProfile.SLog3;
        if (m.Contains("CANON"))     return LogProfile.CLog3;
        if (m.Contains("PANASONIC")) return LogProfile.VLog;
        if (m.Contains("NIKON"))     return LogProfile.NLog;
        if (m.Contains("FUJI"))      return LogProfile.FLog;
        if (m.Contains("DJI") || m.Contains("GOPRO")) return LogProfile.Flat;
        return LogProfile.None;
    }

    public static string DisplayName(LogProfile profile) => profile switch
    {
        LogProfile.None  => "None",
        LogProfile.SLog3 => "S-Log3",
        LogProfile.CLog3 => "C-Log3",
        LogProfile.VLog  => "V-Log",
        LogProfile.NLog  => "N-Log",
        LogProfile.FLog  => "F-Log",
        LogProfile.Flat  => "Flat (GoPro/DJI)",
        _                => profile.ToString(),
    };
}
