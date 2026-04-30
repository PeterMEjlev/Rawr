## SONNET:
- ## File renaming (w. templates)

- ## Exposure Compensation

- ## True RAW histogram

- ## Focus peaking
edge-detection overlay that highlights the sharpest areas in the frame

- ## Folder tree panel
tree-based browser for navigating the filesystem without a separate open dialog

- ## Fully rebindable keyboard shortcuts

- ## TIFF and PNG support 
(RAWR is CR3/JPEG/video only)


## OPUS:
- ## Burst group thumbnail
Make burst group thumbnail stack effect more refined. It doesnt look very good currently.

- ## Zoom as fast as FastRawViewer

- ## TODO: Add visual similarity check to burst grouping
Improve burst/group detection so images are not grouped purely based on timestamp.

Current idea:

```text
For each image:
    candidates = images from same camera within 10 seconds

    for each candidate:
        calculate visual similarity using a fast perceptual hash from the embedded JPEG preview

        if timestamp difference <= 2 seconds:
            group if images are at least somewhat visually similar

        else if timestamp difference <= 10 seconds:
            group only if images are strongly visually similar

Implementation notes:

Keep existing same-camera requirement.
Use embedded JPEG preview rather than full RAW processing for speed.
Cache calculated image hashes in the metadata/database cache.
Start with conservative thresholds and tune later with real photo folders.
Suggested threshold idea:
0-2 sec: relaxed similarity threshold
2-10 sec: stricter similarity threshold
Add setting later for grouping sensitivity: Conservative / Balanced / Aggressive.
Important: completely different photos should not be grouped even if they were taken within 2 seconds