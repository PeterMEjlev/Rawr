# Roadmap

The current scaffold is enough to verify the architecture and exercise the
hot path end-to-end. The items below are the difference between "scaffold"
and "tool you'd reach for instead of Lightroom".

## Near-term (correctness & polish)

- [ ] **Wire up real LibRaw EXIF.** `LibRawExtractor.ExtractMetadata`
      currently returns `FileInfo` only because mapping `libraw_data_t` over
      P/Invoke is fragile (struct layout is version-dependent). Two paths:
      (a) build a tiny C wrapper DLL exposing accessor functions, or
      (b) adopt the `Sdcb.LibRaw` NuGet which already does the struct
      mapping. **Decision pending.**
- [ ] **Indexed thumb extraction (`libraw_unpack_thumb_ex`).** Today the
      LibRaw extractor unpacks the *default* (largest) embedded JPEG for
      every tier. We should fetch the THMB box for thumbnails and PRVW for
      the selected preview to cut decode time and cache size.
- [ ] **UTF-16 file paths on Windows.** `libraw_open_file` is ANSI; a path
      with non-Latin characters will fail. Switch to `libraw_open_file_w`
      via a dedicated `[LibraryImport]` declaration on Windows.
- [ ] **Robust `libraw_processed_image_t` parsing.** The current code reads
      struct offsets directly. Replace with the C wrapper or
      `Sdcb.LibRaw` approach so we don't break across LibRaw versions.

## Performance

- [ ] **Parallel thumbnail extraction.** Today the second-pass extraction
      loop is single-threaded. CR3 thumb extraction is I/O- and decode-bound
      and parallelises well; a `Parallel.ForEach` with bounded concurrency
      (≈ `Environment.ProcessorCount / 2`) should roughly halve cold-folder
      time on SSDs.
- [ ] **In-memory LRU on top of disk cache.** `PreviewCache` always reads
      from disk. Caching the most recent ~100 thumbnails in memory removes
      a dispatcher round-trip during fast scrolling.
- [ ] **Pre-fetch on selection.** When the user lands on photo *N*, also
      kick off preview extraction for *N+1* and *N-1*.
- [ ] **Decode hints.** `BitmapImage.DecodePixelWidth` is set on the
      filmstrip already. Re-confirm it's also wired for the metadata-panel
      dot-renders and the main preview when zoomed.

## Culling features

- [ ] **Groups.** `PhotoItem` already has `GroupId` and `IsBestInGroup`. UI
      to assign / pick group winners is not built yet. Likely shortcut:
      `G` to add to / remove from current group, `B` to mark best.
- [ ] **Multi-select + bulk operations.** Set rating / flag for the whole
      filmstrip selection at once.
- [ ] **Sort modes.** Filename (default), capture time, rating, flag,
      camera. Capture-time sort blocks on EXIF being available — see above.
- [ ] **Compare view.** Two-up or four-up to A/B between similar frames
      before picking a winner.
- [ ] **Zoom to 1:1.** Today the preview is always uniform-fit. Click or
      space to toggle 1:1, with a hand-drag pan.

## Lightroom interop

- [ ] **XMP side-car export.** Write Lightroom-compatible XMP next to each
      RAW so picks/ratings survive the hand-off.
- [ ] **Optional: rename-on-copy templates.** "Copy picks to folder X with
      pattern `{date}_{originalname}`."

## Packaging

- [ ] **MSIX / single-file installer.** `dotnet publish --self-contained`
      with one of: `dotnet publish -p:PublishSingleFile=true`, an MSIX
      package via the Windows App SDK, or a small Inno Setup script.
- [ ] **Auto-update.** Squirrel.Windows or Velopack — both are mature.
      Defer until there's a v0.1 worth shipping.

## Future format support

- [ ] **NEF, ARW, RAF.** Already in the scanner's allow-list, but the
      extractor path needs validation per-format. Expect mostly free with
      LibRaw; WIC requires the appropriate Raw Image Extension.
- [ ] **Sidecar JPEG/HEIF pairing.** When a `.JPG` sits next to a `.CR3`,
      offer a switch between them in the preview.

## Things explicitly out of scope (probably forever)

- Full editing pipeline (exposure, WB, masks). Lightroom does this fine.
- Library-style catalog spanning multiple folders. RAWR is per-folder by
  design.
- Cloud sync. The whole appeal is a local, fast, no-friction tool.
