# CLAUDE.md

Notes for agents working on this repo. README, `docs/architecture.md`, and
`docs/roadmap.md` cover the user-facing story; this file covers the gotchas
that aren't obvious from reading the code.

## Project at a glance

WPF desktop tool (Windows-only, .NET 9) for triaging RAW photos before
Lightroom. Three projects:

- `src/Rawr.Core/` — models, services, SQLite store. **No UI deps**; keep it
  that way so a CLI or different shell could host it.
- `src/Rawr.Raw/` — `IPreviewExtractor` + LibRaw P/Invoke + WIC fallback +
  Shell thumbnail (videos).
- `src/Rawr.App/` — WPF host (`MainWindow`, `MainViewModel`, dialogs,
  converters, services).

Solution file: `RAWR.sln`. The big view model is
`src/Rawr.App/ViewModels/MainViewModel.cs` (~6k LOC) — most cross-cutting
work threads through it.

## Build / run

PowerShell is the shell. `&&` is **not** available — chain with `;` and
`if ($?)`, or just issue separate calls.

```powershell
dotnet build RAWR.sln -c Debug
dotnet run --project src/Rawr.App -c Release
dotnet publish src/Rawr.App -c Release -r win-x64 --self-contained
```

There are no unit tests in the repo. Verification is "does it build" plus
manual exercise of the affected UI path. Say so explicitly if a change
can't be visually tested in the time available.

## Conventions you'll trip over

- **`CommunityToolkit.Mvvm` `[ObservableProperty]`** — the source generator
  emits `_field = value;` *after* `OnXxxChanging(value)` returns, so
  assigning to the field inside `OnXxxChanging` is dead code. If you need
  coercion (e.g. clamping), hand-roll the property with `SetProperty` and
  drop the `[ObservableProperty]` attribute. See `PhotoItem.Rating`.
- **`PhotoItem` derived properties** (`FileName`, `Extension`, `IsVideo`,
  `IsRaw`, `FileTypeBadge`) are precomputed in the `FilePath` init setter
  and held in backing fields — they're queried per-photo across `AllPhotos`
  in filters/sorts, so don't reintroduce on-the-fly `Path.GetExtension(...)
  .ToUpperInvariant()` work in their getters.
- **`PhotoMetadata` is immutable after construction** (`init`-only
  properties). `CameraFormatted` is memoised because the camera-filter and
  the available-cameras rebuild both walk `AllPhotos` calling it.
- **Linear-RAW cache versioning** — `PreviewCache.LinearRawVersion`. Bump
  it whenever the decode produces a different byte layout (WB fix,
  downsample fix, etc.). `LoadLinearRaw` self-deletes mismatched files;
  `PruneStaleLinearRaw` clears the rest on folder open.
- **GMap.NET `RectLatLng.Inflate(width, height)`** — first arg is
  longitude inflation, second is latitude. Easy to swap and the bug shows
  up as lopsided map framing, not a crash.
- **`PropVariantToFileTime` + `DateTime.FromFileTime`** — `PSTF_LOCAL`
  already converts UTC→local, and `FromFileTime` *also* does UTC→local.
  Use `FromFileTimeUtc` to take the wall-clock ticks verbatim (matches how
  `ExifHelper.ParseExifDate` treats the EXIF string).
- **Comments are sparse and explain *why*.** Match that style: skip
  comments that just paraphrase the next line, but do write one when the
  code reflects a specific past bug or constraint.

## Cache & state layout

Everything per-folder lives under `<photo-folder>/.rawr/`:

- `culling.db` — SQLite, schema in `CullingDatabase.EnsureSchema()`. Use
  `ColumnExists` + `ALTER TABLE` for new columns (no Migrations infra).
- `cache/*_thumb.jpg`, `*_preview.jpg` — embedded JPEGs.
- `cache/*_linearraw.bin` — 16-bit linear RGB decode, versioned (see
  above). Large; budgeted via `AppSettings.LinearRawCacheBudgetMb`.

User-level app settings: `%APPDATA%\RAWR\settings.json` (see
`AppSettings.cs`).

GMap tile cache: `%LocalAppData%\GMap.NET\` (managed by `MapWindow`).

## Where to look

- Folder open / scan pipeline → `MainViewModel.LoadFolderAsync`
- Selection-driven preview load → `MainViewModel.LoadPreviewForSelectedAsync`
- Extractor dispatch → `MainViewModel.ExtractorFor(photo)`; LibRaw for
  `IsRaw`, `ShellThumbnailExtractor` for `IsVideo`, WIC otherwise.
- Burst / HDR / Panorama grouping → `Rawr.Core/Services/` of the same name.
- Live exposure render → `Rawr.App/Services/ExposureProcessor.cs`.
- Undo/redo → `EditHistory` + the `ApplyBulkXxx` helpers in `MainViewModel`.
- Keyboard shortcuts (incl. user-customisable) → `ShortcutBinder` +
  `KeySpec` + `AppSettings.KeyBindings`.

## Performance hot spots

The three places where allocations / per-pixel work actually move the
needle:

1. Per-photo work during folder load runs inside a `Parallel.ForEach` in
   `LoadFolderAsync`. JPEG decode dominates `PerceptualHash` and
   `ClippingStatsComputer` — don't bother micro-optimising the pixel
   loops inside them.
2. `LinearRawImage.Downsample` is parallel per row. The decode (LibRaw)
   is the bottleneck for the live preview path, not the downsample.
3. `ExposureProcessor.Render` allocates three `float[w*h]` planes per
   call. Pooling them is a real win for slider-drag responsiveness but
   needs a length-aware `BoxBlurSeparable` refactor — not a drop-in
   change.

Avoid Reading the whole `MainViewModel.cs` at once; use Grep for the
specific concept (e.g. `pattern: "private void ApplyBulkRatingEdit"`).
