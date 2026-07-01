using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rawr.App.Services;

/// <summary>
/// Best-effort safe-eject for a removable volume. Opens the raw \\.\X: device,
/// locks + dismounts it, and asks Windows to mark the medium as removable so the
/// OS shows the "safe to remove hardware" notification. Failure is non-fatal —
/// the user can still pull the card; they just won't get the OS confirmation.
/// </summary>
internal static class VolumeDismount
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint FSCTL_LOCK_VOLUME = 0x00090018;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    private const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    public static bool TryEject(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return false;
        var letter = driveLetter.TrimEnd(':', Path.DirectorySeparatorChar);
        if (letter.Length != 1) return false;

        try
        {
            using var handle = CreateFileW(
                $@"\\.\{letter}:",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) return false;

            // Locking fails if any handle to the volume is still open. Right after a
            // copy, Windows (and our own just-closed file streams) may take a moment
            // to release them, so retry briefly instead of giving up on the first try.
            if (!LockWithRetry(handle))
                return false;
            if (!DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return false;
            DeviceIoControl(handle, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ~1.5s total. Runs off the UI thread (see caller), so the sleeps are harmless.
    private static bool LockWithRetry(SafeFileHandle handle)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return true;
            Thread.Sleep(150);
        }
        return false;
    }
}
