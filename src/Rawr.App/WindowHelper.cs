using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Rawr.App;

internal static class WindowHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    internal static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    // ── Multi-monitor support ──
    //
    // EnumDisplayMonitors returns physical pixel rects; WPF window coordinates use
    // device-independent pixels. We pull the per-monitor DPI through GetDpiForMonitor
    // so we can convert correctly even when monitors run at different scaling.

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const int MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int MDT_EFFECTIVE_DPI = 0;

    public readonly record struct MonitorBounds(double Left, double Top, double Width, double Height, bool IsPrimary);

    public static List<MonitorBounds> GetMonitors()
    {
        var list = new List<MonitorBounds>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref info)) return true;

            // Convert physical pixels to DIPs using this monitor's effective DPI.
            double scale = 1.0;
            if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                scale = 96.0 / dpiX;

            var r = info.rcMonitor;
            list.Add(new MonitorBounds(
                r.Left * scale,
                r.Top * scale,
                (r.Right - r.Left) * scale,
                (r.Bottom - r.Top) * scale,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Pick a non-primary monitor if one exists, falling back to the primary.</summary>
    public static MonitorBounds? PickSecondaryMonitor()
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;
        var nonPrimary = monitors.FirstOrDefault(m => !m.IsPrimary);
        return nonPrimary.Width > 0 ? nonPrimary : monitors[0];
    }

}
