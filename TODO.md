### **SONNET:



## OPUS:
- ## Allow user to manually add location metadata to selected photos (using the map function)

- ## If classification is set to automatic: if a loaded folder contains a large amount of files, prompt the user and ask if the classify subjects should run auto (since this will slow down browsing). include a dont ask me again toggle. Ask for both subject classification and face detection. include an estimated time for long each will take to help the user decide. 


- the row above the filmstrip just contains the "full grid" button. thats a lot of wasted space for a button thats also present on top of the grid view. remove that row. 

---

# Optimization / improvement backlog (from code audit)

Ordered by value. Correctness items first, then performance, then memory.

## Correctness & data safety

- [ ] **Preview cache collides for RAW+JPEG pairs** — `PreviewCache.cs:18-25`
  - All three cache paths key on `Path.GetFileNameWithoutExtension(fileName)`. Shooting RAW+JPEG produces `IMG_0001.CR3` and `IMG_0001.JPG`; both map to the same `IMG_0001_thumb.jpg` / `_preview.jpg`. Last writer wins — the other photo shows its sibling's pixels, and editing the JPG won't invalidate a CR3-written cache. Breaks visibly with `IMG_0001.MP4` + `IMG_0001.JPG` (poster frame vs. photo).
  - Fix: include the extension in the cache stem (`SafeStem` = name + "." + lowercased ext). Old-format files orphan and re-extract once; can sweep them with a `.jpegblurfix`-style sentinel. `Prune*LinearRaw` match `*_linearraw.bin` so they're unaffected.

- [ ] **Cancelled/crashed import leaves a corrupt half-file** — `ImportService.cs:111-119`
  - `CopyFileAsync` writes directly to the destination name. Cancel mid-file → truncated `IMG_0001.CR3` remains. Next import sees "same name, different size" and copies to `IMG_0001 (1).CR3`, so the partial is never repaired and gets scanned as a real photo (LibRaw failures, phantom entries).
  - Fix: write to a temp name + `File.Move` on success (same pattern as `PreviewCache.SaveLinearRaw`); delete temp on failure.
  - Also: manual stream copy loses source timestamps (unlike `File.Copy`) — card offloads date to "today". Add `File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src))` after move.

- [ ] **Exports can come out sideways + non-atomic save** — `PhotoExporter.cs:111-119`, `103-105`
  - Orientation carried only by the EXIF tag in `metadata`; `CreateFrame` falls back to *no metadata* on codec quirks → portrait shots export sideways silently. Fix: read orientation up front (share `ReadExifRotation`), bake into pixels with `TransformedBitmap(RotateTransform)` like `ProcessJpegForCache`, and strip/overwrite the orientation tag when metadata re-attaches (else double-rotate elsewhere).
  - `File.Create` + `encoder.Save` with no temp+rename and no `ct` check → failure mid-encode leaves truncated `.jpg`. Matters more now that Move recycles originals on a `true` return. Fix: temp+rename.
  - Also: `ExportAsync` silently overwrites destination name collisions (`File.Copy(..., overwrite: true)`); two source subfolders with the same filename clobber. Reuse `ImportService.NextAvailableName`.

## Performance

- [ ] **Persist `PhotoMetadata` in culling.db** — biggest folder-reopen win
  - Every folder open runs `ExtractMetadata` for every photo unconditionally (`MainViewModel.cs:2009-2011`); metadata is never loaded from the DB. For RAW that's opening each CR3 + WIC EXIF decode (`LibRawExtractor.cs:184-207`), and without the MS Raw Image Extension it extracts the embedded JPEG a *second* time per photo. Thousands of file opens on every reopen — dominant reopen cost once thumbnails are cached; also blocks burst detection + capture-date re-sort.
  - Fix: add metadata columns (or one JSON column) via `ColumnExists` + `ALTER TABLE` (`CullingDatabase.EnsureSchema`): capture time, camera make/model, lens, ISO, aperture, shutter, focal length, GPS, dimensions, + `file_size`/`mtime` for staleness. Only re-extract on size/mtime mismatch. `PhotoMetadata` is init-only so rebuilding from a row is clean; `CameraFormatted` memoisation still works.

- [ ] **SQLite runs on default (DELETE) journal mode** — `CullingDatabase.cs:30-42`
  - `Open` never sets a journal mode. Single-photo `Save` fires on every rating/flag keystroke (`MainViewModel.cs:6274`); on HDD/NAS each pays journal-create + 2 fsyncs + journal-delete — classic "rating feels sticky" stutter.
  - Fix: after `db.Open()`, `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;`. Crash-safe; degrades gracefully where WAL unsupported; `-wal`/`-shm` live in `.rawr/` so portability unaffected.
  - Secondary: `SaveBatch` → `Save` per photo builds+prepares a fresh `SqliteCommand` each time; on the 10k post-load sweep that's 10k preparations. Hoist one command, `Prepare()` once, reassign parameter values per photo.

- [ ] **Pool the exposure-render planes** (the CLAUDE.md item, now concrete) — `ExposureProcessor.cs:56-70`
  - Per `Render`: `bgr` + `luma`/`cb`/`cr` + two `tmp` planes in `BoxBlurSeparable`. ~130 MB LOH churn per slider tick at 2400px, hundreds of MB at full-res; slider drag fires continuously.
  - The blur loops are bounded by `w`/`h`; only `tmp` sizing uses `.Length`. Rent `luma/cb/cr` (float) and `bgr` (byte) from `ArrayPool`, rent oversized `tmp` inside the blur, return in `finally`. `BitmapSource.Create` copies pixels so returning `bgr` after is safe.
  - Optional: at `w > ~4000` the radius-2 chroma blur is near-invisible but costs 4 passes over ~11M-float planes — skip or scale radius with resolution.
  - Micro-fix: JPEG-fallback `Apply` (`:30-32`) does float-multiply + `Math.Clamp` per byte; gain is constant, so build a 256-entry `byte[]` LUT once per call → `pixels[i] = lut[pixels[i]]`.

- [ ] **Export transcodes decode at full resolution** — `PhotoExporter.cs:90-98`
  - Email/Web presets decode the full sensor JPEG (~260 MB for 45MP) then bilinear-shrink (slow, memory-heavy, aliases at 8:1). WIC can scale in DCT space nearly free with better quality.
  - Fix: when `MaxLongEdge > 0`, decode via `BitmapImage.DecodePixelWidth`/`Height`; read metadata separately with `BitmapDecoder` + `DelayCreation` (no pixel decode).
  - Also: export loop awaits one photo at a time — decode+encode is embarrassingly parallel. Bounded `Parallel.ForEachAsync` (`ProcessorCount/2`) for 3-4× on bulk export; progress via `Interlocked` counter.

- [ ] **Folder indexing decodes every thumbnail twice** — `MainViewModel.cs:2020-2038`
  - `PerceptualHash.ComputeWithStrip` decodes the thumbnail (64px) and `ClippingStatsComputer.Compute` decodes the same bytes again (512px). JPEG decode dominates this stage → paid twice per photo on first index.
  - Fix: one Bgr24 decode at 512px feeds clipping counters directly; gray box-downsample of that buffer (reuse `Resample`) yields the 9×8 hash grid + 32×24 strip.
  - Also: `GrayBuffer` is transient, so every *reopen* re-decodes every thumbnail just to rebuild a 768-byte strip (phash comes from DB). Persist the strip as a small BLOB column → near-zero thumbnail decodes on reopen. Pairs with the metadata-persistence item.

## Memory

- [ ] **Thumbnail bitmap cache survives folder switches** — `JpegBytesToImageConverter.cs:44-47`
  - Static LRU, up to 2048 entries, each pinning a decoded ~150 KB BitmapSource + the source `byte[]` → worst case ~300-400 MB. After a folder switch every old entry is unreachable but stays until slow scroll-eviction.
  - Fix: add `public static void ClearCache()` (take `Gate`, clear `Cache` + `Lru`) and call it during `LoadFolderAsync` teardown. Returns the previous shoot's memory immediately.
