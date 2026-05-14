using System.IO;
using Rawr.Core.Services;

namespace Rawr.App.Services;

/// <summary>
/// Detects camera media cards (SD / CF / etc.) — removable drives that contain a
/// DCIM folder. The host window forwards WM_DEVICECHANGE messages via
/// <see cref="HandleDeviceChangeMessage"/>; on volume arrival we re-scan and raise
/// <see cref="CardInserted"/> for each newly seen card.
/// </summary>
public sealed class MediaCardWatcher
{
    public sealed record MediaCard(string DriveLetter, string VolumeLabel, string DcimPath, long EstimatedFileCount);

    // WM_DEVICECHANGE / DBT constants
    public const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x0002;

    public event EventHandler<MediaCard>? CardInserted;

    private readonly HashSet<string> _knownDriveLetters = new(StringComparer.OrdinalIgnoreCase);

    public MediaCardWatcher()
    {
        // Seed with already-attached cards so we don't fire for them on startup.
        foreach (var c in ScanNow())
            _knownDriveLetters.Add(c.DriveLetter);
    }

    /// <summary>
    /// Scans all currently mounted drives and returns those that look like a
    /// camera card: removable + readable + contains a DCIM folder.
    /// </summary>
    public static List<MediaCard> ScanNow()
    {
        var cards = new List<MediaCard>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return cards; }

        foreach (var d in drives)
        {
            if (TryAsMediaCard(d, out var card))
                cards.Add(card);
        }
        return cards;
    }

    /// <summary>
    /// Called from the host window's HwndSource hook. Returns true if the message
    /// was a device-change we recognized (caller can still let WPF process it).
    /// </summary>
    public void HandleDeviceChangeMessage(IntPtr wParam, IntPtr lParam)
    {
        var evt = wParam.ToInt32();
        if (evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE) return;
        if (lParam == IntPtr.Zero) return;

        // DEV_BROADCAST_HDR: dbch_size (4), dbch_devicetype (4), dbch_reserved (4)
        int deviceType = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
        if (deviceType != DBT_DEVTYP_VOLUME) return;

        // DEV_BROADCAST_VOLUME: + dbcv_unitmask (4) + dbcv_flags (2)
        int unitMask = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 12);

        if (evt == DBT_DEVICEREMOVECOMPLETE)
        {
            foreach (var letter in LettersFromMask(unitMask))
                _knownDriveLetters.Remove(letter);
            return;
        }

        // DBT_DEVICEARRIVAL. Volume isn't always immediately readable; the OS sends
        // the message before the filesystem is fully ready on some readers, so do
        // a short retry loop on a background task.
        foreach (var letter in LettersFromMask(unitMask))
        {
            var rootPath = letter + @":\";
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    try
                    {
                        var di = new DriveInfo(letter);
                        if (TryAsMediaCard(di, out var card))
                        {
                            if (_knownDriveLetters.Add(card.DriveLetter))
                                CardInserted?.Invoke(this, card);
                            return;
                        }
                    }
                    catch { /* not ready yet */ }
                    await Task.Delay(500);
                }
            });
        }
    }

    private static IEnumerable<string> LettersFromMask(int mask)
    {
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1 << i)) != 0)
                yield return ((char)('A' + i)).ToString();
        }
    }

    private static bool TryAsMediaCard(DriveInfo d, out MediaCard card)
    {
        card = null!;
        try
        {
            if (!d.IsReady) return false;
            // USB card readers (especially CF) frequently advertise as Fixed, not
            // Removable, so we don't filter on DriveType. The DCIM folder is the
            // real signal. We only exclude the system drive and CD/DVDs to avoid
            // false positives.
            if (d.DriveType == DriveType.CDRom) return false;
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
            if (string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            var dcim = Path.Combine(d.RootDirectory.FullName, "DCIM");
            if (!Directory.Exists(dcim)) return false;

            long count = 0;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dcim))
                {
                    foreach (var f in Directory.EnumerateFiles(sub))
                    {
                        if (FolderScanner.IsSupported(f))
                            count++;
                    }
                }
                // Some cameras drop media at the DCIM root itself.
                foreach (var f in Directory.EnumerateFiles(dcim))
                {
                    if (FolderScanner.IsSupported(f))
                        count++;
                }
            }
            catch { /* leave count as best-effort */ }

            var letter = d.Name.TrimEnd(Path.DirectorySeparatorChar, ':');
            string label;
            try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "(no label)" : d.VolumeLabel; }
            catch { label = "(no label)"; }

            card = new MediaCard(letter, label, dcim, count);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort safe eject. Uses Win32 device ioctl to dismount the volume so
    /// Windows shows the standard "safe to remove" notification. Returns true on
    /// success.
    /// </summary>
    public static bool TryEject(string driveLetter)
    {
        return VolumeDismount.TryEject(driveLetter);
    }
}
