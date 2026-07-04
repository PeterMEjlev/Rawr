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

## Performance & feel — second audit (2026-07-04)

Ordered by expected user-visible value within each group.

### Feel — UI-thread stalls

- [ ] **Filmstrip thumbnails decode JPEGs on the UI thread** — `JpegBytesToImageConverter.cs:49-55` + `MainWindow.xaml:3056-3070`
  - `IValueConverter.Convert` always runs on the dispatcher, and on a cache miss it does a full synchronous JPEG decode (`Decode`, `:83-95`). The grid is covered — `VirtualizingWrapPanel.QueueThumbnailPreload` (`VirtualizingWrapPanel.cs:451-493`) warms the converter's static cache off-thread ahead of the scroll direction. The filmstrip is a plain horizontal `VirtualizingStackPanel` with **no** preload: every newly realized cell during arrow-key navigation or a filmstrip scroll pays a ~1-3 ms decode on the UI thread, ~10-15 cells per viewport shift. Same on first folder open: each `FlushPendingPreviewUpdates` batch (`MainViewModel.cs:1946-1990`) sets up to 96 `ThumbnailJpeg` properties, and every one that lands on a realized cell decodes inline — visible micro-stutters while thumbnails stream in.
  - Fix (two independent halves):
    1. *Filmstrip preload.* In `Filmstrip_SelectionChanged` / `CenterFilmstripSelection` (`MainWindow.xaml.cs:969-1003`), after computing the target offset, collect `ThumbnailJpeg` bytes for indices in `[selected - viewportItems, selected + viewportItems]` and `Task.Run` a loop of `JpegBytesToImageConverter.Preload(bytes)` (mirror the panel's pattern incl. a `CancellationTokenSource` field cancelled on each new selection). The converter cache is keyed on the byte array reference, so the warmed entries are exactly what `Convert` will look up.
    2. *Load-time warm.* In `QueuePreviewUpdate`'s producer side (the `Parallel.ForEach` body in `GeneratePreviewsAsync`, `MainViewModel.cs:2014-2106`), call `JpegBytesToImageConverter.Preload(thumbBytes)` right after `SaveThumbnail` — the decode then happens on the worker thread that just produced the bytes, and the dispatcher flush finds a warm cache. Costs one decoded bitmap per photo in the LRU (bounded at 2048), no extra pixels vs. today.
  - Verify: open a 1k+ folder, hold Right arrow through the filmstrip while watching for dropped frames; scroll the filmstrip fast with the wheel. Before: visible hitching as new cells realize. After: smooth.

- [ ] **Burst/HDR/panorama detection runs wholesale on the UI thread** — `MainViewModel.cs:2135`, `6442-6482`
  - `RunBurstAndAutoTagDetection` is invoked via one `Dispatcher.InvokeAsync` after indexing. That single dispatcher callback does: phash Hamming comparisons across all neighbours (`BurstDetector.Detect`), HDR exposure-ladder analysis, panorama strip cross-correlation, *and* the system-tag DB writes inside `SyncSystemTagForFolder` (`:5980`) — SQLite transactions on the dispatcher. On a 5-10k folder this is a one-shot 100 ms-1 s freeze that lands exactly when the user has just started browsing. Re-running it from `ApplyBurstSettings` (settings dialog) freezes the same way.
  - Fix: split compute from apply. The detectors only *read* `Phash`/`GrayBuffer`/`Metadata.CaptureTime` (all stable once indexing is done) and *write* `GroupId`/`BurstBadge`/`IsHdr`/`IsPanorama` (observable). Refactor `BurstDetector.Detect` (and the HDR/pano helpers) to return a result list (`photo → groupId/badge/flags`) instead of mutating, computed inside the existing `Task.Run` that follows indexing; then one dispatcher hop applies the assignments (plain property sets, ~10k setter calls ≈ few ms) and a background `Task.Run` does the `SyncSystemTagForFolder` DB writes (they're already batched in transactions; nothing in them needs the UI thread — tag `ObservableCollection` adds excepted, keep those in the apply hop).
  - Gotcha: `ApplyFilter` runs after detection when sort/filter depends on bursts (`MainViewModel.cs:2156-2160`) — keep that ordering (apply-on-dispatcher, then filter).
  - Verify: open a large burst-heavy folder; the "N photos ready" status should appear without the ~0.5 s input freeze right before it.

- [ ] **Sidebar bucket refresh walks AllPhotos ~30 times** — `MainViewModel.cs:7497-7521`, `7577-7601`, `7802-7862`
  - The 17 count properties are each an O(N) LINQ `Count(...)`, and `RefreshSubjectChips` calls `CountSubject` (another full walk) per group *and* per leaf chip. One `RefreshFilterBuckets` ≈ 30 full scans ≈ 600k+ delegate calls on a 20k folder, on the UI thread. It fires on every filter click (via `ApplyFilter`) and repeatedly during background classification (`:4471`, `:4495` — every progress batch).
  - Fix: single-pass accumulator. Add a private `BucketCounts` struct (int fields for each rating/label/flag/exposure/eyes bucket + an `int[]` indexed by subject-chip position). One `foreach (var p in AllPhotos)` fills it; the 17 properties become field reads from a cached `_bucketCounts`, and `RefreshSubjectChips` reads the array. `RefreshFilterBuckets` = recompute struct (one scan) + the existing `OnPropertyChanged` volley. The tag-count loop (`:7850-7859`) already does this single-pass pattern — extend the same walk to fill the struct so the whole refresh is literally one pass.
  - Keep `RefreshAvailableCameras`' walk inside the same pass too (it's the 31st scan today).
  - Verify: with a big folder, spam-click sidebar buckets / watch during classification. Also just assert the counts match the old getters once on a test folder.

- [ ] **Sidebar counts go stale after rating/flag/label edits** — `MainViewModel.cs:4734`, `4792`, `4835`
  - The `ApplyBulkXxxEdit` helpers save + record undo but never notify the bucket-count properties, so "★★★ 12" in the sidebar doesn't tick when you rate a photo — it only corrects on the next filter click or classification batch. Feels broken even though the DB is right.
  - Fix: once the single-pass refresh above exists (cheap), call `RefreshFilterBuckets()` at the end of `ApplyBulkRatingEdit` / `ApplyBulkFlagEdit` / `ApplyBulkColorLabelEdit` (and in their undo/redo closures `ApplyAll`/`RevertAll`, so undo updates counts too). If you want it even cheaper for the single-photo case, adjust the struct incrementally (`counts.Rating[old]--; counts.Rating[new]++`) and raise only the two affected properties — but measure first; the one-pass scan is likely already < 5 ms.
  - Verify: rate/flag/label a photo with the sidebar visible; counts move immediately, and Ctrl+Z moves them back.

- [ ] **Per-navigation cache file I/O on the UI thread** — `MainViewModel.cs:3020`, `3043-3046`, `3104-3105`
  - `LoadPreviewForSelectedAsync` calls `_cache?.LoadPreview(...)` (a synchronous `File.Exists` + `File.ReadAllBytes` of a ~200-600 KB preview, `PreviewCache.cs:47-51`) directly on the dispatcher before the first `await Task.Run`. `CacheFor(photo).HasLinearRaw(...)` adds a `File.Exists`. The video path reads up to preview+thumbnail the same way. Warm NTFS cache hides it; on HDD/NAS folders or after a cache flush it's tens of ms of UI-thread block per arrow-key press.
  - Fix: fold the read into the existing background hop — replace `photo.PreviewJpeg ?? _cache?.LoadPreview(...)` + `Task.Run(() => LoadBitmapFromJpeg(cached))` with a single `await Task.Run(() => { var bytes = photo.PreviewJpeg ?? _cache?.LoadPreview(...); return (bytes, bytes == null ? null : LoadBitmapFromJpeg(bytes)); }, ct)`. Same shape for `HasLinearRaw` (compute it inside the same `Task.Run` and branch on the tuple afterwards) and for the video path. All post-await code already re-checks `SelectedPhoto == photo`, so no new races.
  - Verify: browse a folder living on a USB HDD / network share; arrow-key latency stops spiking on uncached photos.

- [ ] **Filmstrip rounded corners use a VisualBrush OpacityMask per item** — `MainWindow.xaml:3129-3138`
  - Every filmstrip cell carries `Border.OpacityMask` = `VisualBrush` wrapping a live `Border` whose size binds to the host (`ActualWidth`/`ActualHeight` bindings). VisualBrush masks force an intermediate composition surface per item and re-render it on size changes — one of the classic WPF render-thread killers, multiplied by every visible cell plus the burst stack backers. The grid template notably does *not* do this (it just lets corners be square under `UniformToFill` + `ClipToBounds`).
  - Fix, pick one:
    1. Cheapest: drop the mask and accept square thumbnail corners in the filmstrip (matches the grid's current look).
    2. Keep the look: replace `<Image>` + mask with painting the thumbnail as the `Border.Background` via `ImageBrush` (`<Border CornerRadius="..."><Border.Background><ImageBrush ImageSource="{Binding ThumbnailJpeg, Converter=...}" Stretch="Uniform"/></Border.Background></Border>`) — ImageBrush respects the border's rounded clip without any intermediate surface. Note `Stretch="Uniform"` on a brush letterboxes with transparent bars exactly like `Image` does.
    3. Alternative: `Border.Clip` with a `RectangleGeometry RadiusX/RadiusY` set from code-behind on `SizeChanged` (no bindings, no VisualBrush).
  - The two "stack-of-cards" backer `Border`s (`:3113-3124`) are plain borders — fine, leave them.
  - Verify: filmstrip scroll + window resize with a full strip; render-thread cost drops (visible in PerfView/GPU usage, or just smoother resize).

### Speed — folder open / reopen

- [ ] **RAW thumbnail stage extracts the big PRVW instead of the small THMB** — `LibRawExtractor.cs:37-39`, `209-239`
  - `ExtractThumbnail` == `ExtractPreview` == `ExtractDefaultThumb`, which unpacks the *default (largest)* embedded JPEG — for CR3 that's the ~1620×1080 PRVW (~400 KB) even though the file also carries a ~320×214 THMB (~20 KB). The first-open thumbnail pass therefore reads and IDCT-decodes ~20× more JPEG bytes per photo than the 320 px cache target needs. The interop for indexed extraction already exists (`UnpackThumbEx`, used by `ExtractLargestThumb` at `:245-310`).
  - Fix: add `ExtractSmallestThumbAtLeast(string path, int minWidth)` modelled on `ExtractLargestThumb`: iterate `UnpackThumbEx(handle, idx)` for idx 0..3, keep JPEG candidates (reuse `IsJpeg`), pick the smallest one whose decoded width ≥ `minWidth`; fall back to the default thumb when none qualifies or `unpack_thumb_ex` is missing. Call it from `ExtractThumbnail` with `AppSettings.Current.ThumbnailDecodeWidth`. **Width problem**: LibRaw leaves struct width 0 for JPEG thumbs (see comment at `:275-280`), so read dimensions from the JPEG bytes themselves — a tiny SOF0/SOF2 header scan (~30 lines, no decode) is enough.
  - Gotcha (the old blur bug): never accept a thumb *smaller* than `ThumbnailDecodeWidth` — upscaled soft thumbnails were exactly the `.jpegblurfix` migration's cause. THMB at 320 px passes the default 320 setting; users who raise the setting automatically fall through to PRVW.
  - `ExtractPreview` must keep returning the big preview — only change `ExtractThumbnail`.
  - Verify: delete a test folder's `.rawr/cache`, reopen, time the "Generating previews N/M" pass (status badge); expect a large cut on CR3-heavy folders. Then spot-check filmstrip/grid sharpness at default settings.

- [ ] **Post-load sweep rewrites every DB row on every open** — `MainViewModel.cs:2141-2149`, `CullingDatabase.cs:309-338`, `350-365`
  - After indexing, `SaveAllPhotosPerOwningDb(AllPhotos)` upserts *every* photo, and `BindPhoto` re-serialises `meta_json` (~1-2 KB of JSON via `PhotoMetadataSerializer`) plus the 768-byte `gray_strip` per row. On a fully-cached 10k reopen where nothing changed, that's 10k JSON serialisations + 10k upserts (~20-30 MB of WAL churn) for zero new information — every single open.
  - Fix: dirty-track against the state we just loaded. `LoadFolderCatalog` already has each photo's `PhotoState` in hand; keep a `Dictionary<string, PersistSnapshot>` (per context) where `PersistSnapshot` is a small struct of the persisted fields (rating, flag, label, groupId, isBest, phash, clip pcts, face fields, subjectTags, metaMtime — *not* the JSON itself; `meta_mtime` changing is what implies `meta_json` changed). In the sweep, skip photos whose current values equal the snapshot; update the snapshot after a successful `SaveBatch`. `Save`/`SaveBatch` calls from user edits stay unconditional (they're real changes).
  - Alternative if the snapshot map feels heavy: a `photo.PersistDirty` bool set by the indexing pass (fresh phash/clip/metadata), by `BurstDetector` result application (groupId actually changed), and by every user-edit path; sweep filters on it and clears it. Fewer moving parts, but easy to miss a setter — the snapshot compare is self-healing.
  - Verify: reopen a fully-indexed folder and watch `.rawr/culling.db-wal` size / process disk writes: should drop from ~MBs to ~0. Confirm burst regrouping after a strictness change still persists (those photos *do* differ from snapshot).

- [ ] **`GetBurstMembers` is an O(N) scan per call** — `MainViewModel.cs:6285-6290`
  - Every call filters + sorts all of `AllPhotos`. It's called per collapsed representative in `ExpandCollapsedBurstRepresentatives` (`:4711`) — i.e. per selected item on every bulk rating/flag/label edit — and per burst in the `BurstCollapsed` branch of `ApplyFilter` (via `SelectBurstRepresentative`'s callers building `membersByGroup`, which is fine, but selection expansion isn't). Select-all on a 20k folder with 500 bursts → 500 × 20k scans ≈ 10M predicate calls + 500 sorts before the edit even starts.
  - Fix: maintain `Dictionary<int, List<PhotoItem>> _burstMembers`, rebuilt in one pass wherever GroupIds are (re)assigned — end of `RunBurstAndAutoTagDetection` (both branches) and cleared in folder teardown. Sort each list once at build (capture time, then filename — same comparer as today). `GetBurstMembers` becomes a dictionary lookup returning the cached list (document "don't mutate"). The `ApplyFilter` collapse path can keep its local `membersByGroup` (it needs the *filtered* subset, which differs), but selection expansion and `AddToSelection`/`RemoveFromSelection` (`:2293`, `:2313`) switch to the index.
  - Verify: select-all + rate on a burst-heavy folder; the beat before the stars appear disappears.

### Speed — interaction paths

- [ ] **Full-res exposure render pays an invisible chroma blur** — `ExposureProcessor.cs` (`Render`'s blur calls), `MainViewModel.cs:2707-2717`
  - `Render` box-blurs the Cb/Cr planes (radius 2, two separable passes each = 4 parallel sweeps over `w*h` floats × 2 planes). At the ~2400 px preview this is the point (kills chroma speckle). At the full-res render that `ApplyExposureAsync` fires after the downsampled one (when `_fullRawImage` is resident post-zoom, ~4100 px wide half-size decode ≈ 11M px), radius-2 chroma blur is imperceptible at any zoom that shows the full frame — but costs ~8 full-plane sweeps, the bulk of the second render's latency during slider drags at zoom.
  - Fix: gate the blur on resolution: `if (w <= ChromaBlurMaxWidth) { BoxBlurSeparable(cb...); BoxBlurSeparable(cr...); }` with the constant ≈ 3000, or scale the radius (`radius = w > 3000 ? 1 : 2`) if dropping it entirely shows speckle on noisy high-ISO shots — test with an ISO 6400+ file at 100% zoom before choosing. Keep the preview-width path exactly as is.
  - Verify: zoom into a RAW, drag the EV slider; the full-res follow-up render lands noticeably sooner (log timestamps around the second `Render` call if needed).

- [ ] **Video-proxy prefetch snapshots the whole filtered list every keystroke** — `MainViewModel.cs:3649-3658`
  - `QueueVideoProxyPrefetch` runs on every selection change and does `FilteredPhotos.ToList()` — a 20k-element list allocation per arrow-key press, purely as a defensive snapshot for a task that then waits `VideoProxyPrefetchSettleDelayMs` (700 ms) and usually gets cancelled by the next keypress.
  - Fix: pass only `currentIndex` to `PrefetchVideoProxiesAfterSettleAsync` and take the snapshot *after* the settle delay survives (`SelectedIndex != currentIndex` already guards staleness); or early-return before the copy when no photo in the folder is a video (cache a `_hasVideos` bool at load). Both are one-liners; do both.
  - Verify: allocation trace while holding an arrow key — the per-press `List` (and its 160 KB backing array on 20k folders) disappears from the profile.

### Startup

- [ ] **FlyleafLib/FFmpeg engine starts synchronously in the window constructor** — `MainWindow.xaml.cs:258-269`, `64-79`
  - `EnsureFlyleafEngineStarted()` + `CreatePlayer()` run inside `MainWindow()`: `Engine.Start` probes and loads the FFmpeg native set (avcodec/avformat/…, ~10 DLLs) before the window can even render — pure cost on the cold-start path, paid even by users culling stills-only folders.
  - Fix: make `_player` lazy. Extract a `private Player EnsurePlayer()` that runs `EnsureFlyleafEngineStarted(); _player ??= CreatePlayer(); hook events;` and call it from the first code path that needs a player (video selection — where `VideoSourceUri` gets consumed — plus the mute/speed/seek handlers, which should no-op when `_player` is null). The event hookups (`Player_OpenCompleted` etc.) move inside `EnsurePlayer`. If first-video latency then feels bad, warm it in the background instead: `Dispatcher.BeginInvoke(EnsurePlayer, DispatcherPriority.ApplicationIdle)` after `Loaded` — still off the critical path, ready by the time a human reaches a video.
  - Gotcha: `RotateVideoCommand` and friends read `_player` — audit every `_player.` reference for null-safety (field becomes `Player?`).
  - Verify: stopwatch from launch to first interactive frame (or PerfView) before/after; confirm videos still play, incl. as the *first* selected item in a folder.

## Memory

- [ ] **Thumbnail bitmap cache survives folder switches** — `JpegBytesToImageConverter.cs:44-47`
  - Static LRU, up to 2048 entries, each pinning a decoded ~150 KB BitmapSource + the source `byte[]` → worst case ~300-400 MB. After a folder switch every old entry is unreachable but stays until slow scroll-eviction.
  - Fix: add `public static void ClearCache()` (take `Gate`, clear `Cache` + `Lru`) and call it during `LoadFolderAsync` teardown. Returns the previous shoot's memory immediately.
