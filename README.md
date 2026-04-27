# RAWR

A fast Windows desktop RAW photo viewer and culling tool, optimised for Canon
**cRAW / CR3** files. Built to make pre-Lightroom culling — rating, flagging,
labelling, sorting and selecting — dramatically faster than doing it inside
Lightroom itself.

> **Status**: working scaffold. The end-to-end flow (open folder → preview →
> rate/flag/label → copy picks) is wired up. LibRaw native binary + EXIF parsing
> still need to be plugged in for production-quality preview extraction.

## Why a separate culling tool?

Lightroom is great at editing. It is slow at *deciding which photos to keep*:
catalogs to import, full-size renders to chew through, panels you don't need.
For a 2 000-frame shoot day, the bottleneck is the cull, not the edit.

RAWR's only job is to:

1. Point at a folder.
2. Get JPEG previews on screen as fast as possible.
3. Let you rate / pick / reject / label / group with the keyboard.
4. Hand the resulting selection (copy or path list) to Lightroom.

## How it stays fast

- **No demosaicing.** CR3 (and most modern RAW formats) embed JPEGs at three
  resolutions. RAWR extracts the embedded JPEG bytes directly — no debayer,
  no colour conversion, just `seek + read`.
- **Three-tier progressive loading.** Small thumbnail for the filmstrip,
  medium preview when a photo is selected, full-resolution JPEG only on zoom.
- **Codec-level scaling.** The thumbnail loader uses
  `BitmapImage.DecodePixelWidth`, so the JPEG decoder produces an
  already-small bitmap instead of decoding full-size and shrinking later.
- **Disk cache.** Extracted JPEGs are written to `.rawr/cache/` next to the
  photos, so re-opening a folder is near-instant.
- **Virtualised filmstrip.** The horizontal `ListBox` recycles containers so a
  10 000-photo folder still scrolls cleanly.
- **SQLite for culling state.** Ratings, flags, labels, and groups live in
  `.rawr/culling.db` inside the photo folder — portable with the folder, no
  catalog import required.

## Keyboard shortcuts

| Key             | Action                                |
| --------------- | ------------------------------------- |
| `Ctrl+O`        | Open folder                           |
| `←` / `→`       | Previous / next photo                 |
| `0`–`5`         | Set star rating (0 = clear)           |
| `6` / `7` / `8` / `9` | Color label: red / yellow / green / blue |
| `P`             | Toggle pick                           |
| `X`             | Toggle reject                         |
| `U`             | Unflag                                |
| `Shift+P`       | Pick *and advance to next*            |
| `Shift+X`       | Reject *and advance to next*          |
| `Ctrl+Shift+R`  | Cycle rating filter (0/1/2/3/4/5)     |
| `Ctrl+Shift+F`  | Cycle flag filter (none/pick/reject)  |
| `Ctrl+Shift+X`  | Clear all filters                     |
| `Ctrl+C`        | Copy picked photos to a folder        |
| `Ctrl+E`        | Export picked file paths as `.txt`    |

## Building from source

Requirements:
- .NET 9 SDK
- Windows 10/11

```bash
dotnet build RAWR.sln -c Release
dotnet run --project src/Rawr.App -c Release
```

To produce a standalone executable:

```bash
dotnet publish src/Rawr.App -c Release -r win-x64 --self-contained
```

## Optional: enabling LibRaw

Out of the box RAWR uses Windows Imaging Component (WIC) for preview
extraction. To decode CR3, WIC requires the free **Microsoft Raw Image
Extension** from the Microsoft Store.

For best performance and broadest CR3 support (including cRAW), drop a
LibRaw 0.21+ Windows DLL next to `RAWR.exe`:

1. Get a Windows build of LibRaw (vcpkg: `vcpkg install libraw:x64-windows`,
   or download from <https://www.libraw.org/download>).
2. Copy `libraw.dll` to the application output folder
   (`bin/Release/net9.0-windows/`).

RAWR auto-detects LibRaw at startup and falls back to WIC if it's missing.

## Layout

```
src/
  Rawr.Core/        # Models, services, SQLite culling DB — no UI deps
  Rawr.Raw/         # IPreviewExtractor + LibRaw P/Invoke + WIC fallback
  Rawr.App/         # WPF host: views, view-models, theme, converters
docs/
  architecture.md   # How the pieces fit together
  roadmap.md        # What's next
```

## License

Personal project. License TBD before any public release.
