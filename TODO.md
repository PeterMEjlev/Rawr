## SONNET:

- ## TIFF and PNG support 
(RAWR is CR3/JPEG/video only)

## OPUS:
- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Auto sort (or tag) images that have over a certain percent (changeable in user settings) of its pixels clipped / overblown.

- ## XMP sidecar files for lightroom compatability
