# Architecture

## Goals

1. **Speed first.** Every architectural choice is judged on whether it shortens
   the time between *user lands on a folder* and *user finishes culling*.
2. **No catalogs, no surprises.** Culling state lives in the photo folder.
   You can move the folder, archive it, copy it to another machine — your
   ratings come along.
3. **Format-pluggable.** CR3 is the priority but the RAW layer is an
   interface, so additional formats (NEF, ARW, …) can plug in the same way.

## Layers

```
┌───────────────────────────────────────────────────────────┐
│ Rawr.App  (WPF, net9.0-windows)                           │
│   MainWindow.xaml  ──────►  MainViewModel                 │
│       ▲                       │                           │
│       │ data binding          ▼                           │
│   converters / theme    PhotoItem (ObservableObject)      │
└───────────────────────────────────────────────────────────┘
                       │
                       ▼
┌───────────────────────────────────────────────────────────┐
│ Rawr.Raw  (net9.0-windows)                                │
│   IPreviewExtractor                                       │
│      ├── LibRawExtractor   (P/Invoke → libraw.dll)        │
│      └── WicExtractor      (BitmapDecoder → WIC codecs)   │
└───────────────────────────────────────────────────────────┘
                       │
                       ▼
┌───────────────────────────────────────────────────────────┐
│ Rawr.Core  (net9.0)                                       │
│   Models           PhotoItem · PhotoMetadata · CullFlag · │
│                    ColorLabel                             │
│   Services         FolderScanner · PreviewCache ·         │
│                    FileOperations                         │
│   Data             CullingDatabase  (SQLite)              │
└───────────────────────────────────────────────────────────┘
```

`Rawr.Core` has no UI dependencies and could be reused from a CLI or a
different shell. `Rawr.Raw` is split out because the LibRaw P/Invoke surface
is the most likely thing to need swapping (different LibRaw build, different
RAW backend entirely).

## The hot path: opening a folder

`MainViewModel.LoadFolderAsync(path)`:

1. **Cancel any in-flight scan.** Token-based — switching folders mid-scan
   should not produce stale UI.
2. **`FolderScanner.Scan`** lists supported extensions. Pure file enumeration —
   no `FileInfo` calls per item, no codec touches. Returns in milliseconds.
3. **Open SQLite + cache.** `CullingDatabase.Open` creates `.rawr/culling.db`
   if absent and reads all saved state into a dictionary. `PreviewCache`
   creates `.rawr/cache/`.
4. **Build PhotoItems.** One `PhotoItem` per file, with saved rating/flag/
   label rehydrated from the dictionary. UI is bindable immediately.
5. **First pass: cached thumbnails.** For each photo, try
   `PreviewCache.LoadThumbnail` synchronously on the UI thread. These reads
   are tiny (~20 KB each) and unblock the filmstrip instantly on subsequent
   opens.
6. **Second pass: extract missing thumbnails.** On a background `Task.Run`,
   loop over photos with no cached thumb, call
   `IPreviewExtractor.ExtractThumbnail`, save to disk, marshal back to the
   UI thread to set `ThumbnailJpeg`. Status text updates every 10 photos.

Selection drives a separate, parallel pipeline:

`MainViewModel.OnSelectedIndexChanged → LoadPreviewForSelectedAsync`:

1. Try `PreviewCache.LoadPreview` (medium-resolution embedded JPEG).
2. Otherwise show the thumbnail immediately as a placeholder.
3. Background-extract the medium preview, save to cache, swap into the
   `PreviewImage` property — but only if the user hasn't moved on (we
   compare `SelectedPhoto == photo` after the await).

## CR3 specifics

A CR3 file is an ISO-BMFF (MP4-style) container. Inside there are typically
three embedded JPEGs:

| Box  | Resolution    | Size      | Used for         |
| ---- | ------------- | --------- | ---------------- |
| THMB | ~320 × 214    | ~20 KB    | Filmstrip        |
| PRVW | ~1620 × 1080  | ~400 KB   | Selected preview |
| mdat | sensor (e.g. 8192×5464) | ~3–5 MB | Zoom / 1:1 |

`libraw_open_file` parses the container in ~1 ms; `libraw_unpack_thumb`
performs a seek+read of the embedded JPEG bytes. **No demosaic, no colour
conversion.** That's the entire reason CR3 culling can be fast.

cRAW (lossy compressed sensor data) and regular CR3 share identical embedded
preview blocks — we don't care which one we got.

## Persistence

Two side-car artefacts, both inside `<photo-folder>/.rawr/`:

- `culling.db`: SQLite. Schema is one table, `photos`, keyed by file *name*
  (not full path) so the data survives moving the folder.
- `cache/<basename>_thumb.jpg` and `_preview.jpg`: extracted JPEG bytes.
  Disposable — deleting the cache only costs CPU, never user data.

We never write into the RAW files themselves and we don't write XMP
side-cars yet (see roadmap).

## Threading

- **UI thread**: all `ObservableObject` mutations, all `BitmapImage`
  construction (well: construction happens off-thread but `Freeze()` is
  called so cross-thread access is legal).
- **Background `Task.Run`**: P/Invoke into LibRaw, JPEG file IO, SQLite
  writes for non-UI events.
- **Cancellation**: the active folder scan owns a `CancellationTokenSource`.
  Switching folders cancels the previous one; consumers (`Task.Run` loops,
  awaited operations) honour the token.

The dispatcher is hit at most once every ten thumbnails to update status
text — keeping the UI responsive without a flood of marshalling.

## What this codebase deliberately doesn't do

- **No XMP side-car writing** — only the SQLite DB knows your ratings.
  Lightroom-compatible XMP export is on the roadmap.
- **No image editing** — exposure, white balance, crop, anything.
- **No catalog** — there is no global database, no "import" step.
- **No demosaicing path** — full-resolution viewing relies on the embedded
  JPEG. Real-pixel debayered viewing would require LibRaw's full processing
  pipeline and is out of scope for a culling tool.
