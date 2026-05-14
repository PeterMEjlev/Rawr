### **SONNET:


- ## Change "Filters" title to "Quick Filters" in order to differentiate it from the Filters button (which is the advanced filters)

## OPUS:
- ## Allow user to manually add location metadata to selected photos (using the map function)

- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Subject classifier — small zero-shot model (CLIP-tiny or similar) tags photos with "person", "landscape", "food", "animal". Filterable. Surprisingly accurate even at small sizes.

- ## Custom import settings when importing from an SD/CF card. i.e filters for putting photos or videos in seperate (sub)folders (or RAW vs JPG etc). The app should auto detect new sd/cf cards like LR.

- ## The histogram for a burst group and when going into the burst group are different?


- ## Phase 2 of recursive folder view: tag editing across subfolders (translate synthetic display IDs to per-subfolder DB IDs), restore HDR/Panorama auto-tag DB sync per subfolder, recursive Cache-All, ExecuteMacro tag part, XMP keyword auto-create

