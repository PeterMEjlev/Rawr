using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Rawr.Raw;

/// <summary>
/// Detects whether a DNG carries Adobe Camera Raw (Lightroom / ACR) develop
/// edits in its embedded XMP packet.
///
/// Why it matters: when a DNG is edited in Lightroom/ACR, the develop settings
/// are written into the file's XMP *and* the embedded preview is re-rendered to
/// reflect them. RAWR's own linear-RAW render is deliberately neutral and knows
/// nothing about those edits, so overriding the embedded preview with it would
/// throw the user's edits away on screen (the "edited → raw" flash). For an
/// edited DNG we keep the embedded preview instead.
///
/// Proprietary RAWs (CR2/NEF/ARW…) don't need this: Lightroom stores their edits
/// in a sidecar .xmp and never updates the in-file embedded preview, so that
/// preview is always as-shot — same neutral baseline as our render.
///
/// The read is bounded: we parse the classic-TIFF IFD0 for the XMP tag (700) and
/// read only those bytes (a few KB), never the whole multi-MB file.
/// </summary>
public static class DngEditDetector
{
    // TIFF tag 0x02BC (700) — the XMP packet. DNG (a TIFF) stores ACR develop
    // settings here.
    private const ushort XmpTag = 700;

    /// <summary>
    /// True when the DNG at <paramref name="filePath"/> has ACR develop settings
    /// baked into its XMP. Best-effort: any IO/parse failure returns false, so an
    /// unreadable header just falls back to the normal (raw-render) path.
    /// </summary>
    public static bool HasCameraRawEdits(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var xmp = ReadXmpPacket(fs);
            if (xmp == null) return false;

            // Lightroom/ACR write develop settings as attributes on the
            // rdf:Description node; HasSettings="True" is Adobe's canonical
            // "this file carries Camera Raw adjustments" marker. Matching the
            // local-name (rather than requiring the conventional crs: prefix)
            // keeps it robust to non-standard namespace prefixes.
            return Regex.IsMatch(xmp, "HasSettings\\s*=\\s*\"True\"", RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadXmpPacket(Stream s)
    {
        Span<byte> hdr = stackalloc byte[8];
        if (!ReadExact(s, hdr)) return null;

        bool little;
        if (hdr[0] == 0x49 && hdr[1] == 0x49) little = true;        // 'II' little-endian
        else if (hdr[0] == 0x4D && hdr[1] == 0x4D) little = false;  // 'MM' big-endian
        else return null;

        if (ToU16(hdr.Slice(2, 2), little) != 42) return null; // classic TIFF only (DNG isn't BigTIFF)
        uint ifdOffset = ToU32(hdr.Slice(4, 4), little);
        if (ifdOffset < 8) return null;

        s.Seek(ifdOffset, SeekOrigin.Begin);
        Span<byte> cnt = stackalloc byte[2];
        if (!ReadExact(s, cnt)) return null;
        int entries = ToU16(cnt, little);

        Span<byte> entry = stackalloc byte[12];
        for (int i = 0; i < entries; i++)
        {
            if (!ReadExact(s, entry)) return null;
            if (ToU16(entry.Slice(0, 2), little) != XmpTag) continue;

            // type is BYTE(1)/UNDEFINED(7), 1 byte each, so count == byte length.
            uint count = ToU32(entry.Slice(4, 4), little);
            if (count == 0 || count > 8_000_000) return null; // sanity cap

            var data = new byte[count];
            if (count <= 4)
            {
                entry.Slice(8, (int)count).CopyTo(data);
            }
            else
            {
                s.Seek(ToU32(entry.Slice(8, 4), little), SeekOrigin.Begin);
                if (!ReadExact(s, data)) return null;
            }
            return Encoding.UTF8.GetString(data);
        }
        return null;
    }

    private static bool ReadExact(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf.Slice(read));
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static ushort ToU16(ReadOnlySpan<byte> b, bool little) =>
        little ? (ushort)(b[0] | b[1] << 8)
               : (ushort)(b[1] | b[0] << 8);

    private static uint ToU32(ReadOnlySpan<byte> b, bool little) =>
        little ? (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24)
               : (uint)(b[3] | b[2] << 8 | b[1] << 16 | b[0] << 24);
}
