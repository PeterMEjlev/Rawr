## SONNET:

## OPUS:
- ## Allow user to manually add location metadata to selected photos (using the map function)

- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Subject classifier — small zero-shot model (CLIP-tiny or similar) tags photos with "person", "landscape", "food", "animal". Filterable. Surprisingly accurate even at small sizes.

- ## the tooltip for shortcuts with special characters like ÆØÅ just show up as \ and not the actual keyboard shortcut

- ## When a file is deleted, the selected photo goes to the first photo. it needs to go to the next photo instead

- ## Goto last star rated / flagged / labeled / tagged photo. basically last photo that was interacted with in a folder (resume where last sorting started)

- ## Custom import settings when importing from an SD/CF card. i.e filters for putting photos or videos in seperate (sub)folders (or RAW vs JPG etc)

- ## The histogram for a burst group and when going into the burst group are different?

- ## if a top folder is open in rawr, and the app is restarted then only the subfolder of the top folder (that was open) is opened as the top level

- ## click in video preview should play/pause

- ## video metadata should include fps

- ## ability to rotate vertical videos 

- ## Phase 2 of recursive folder view: tag editing across subfolders (translate synthetic display IDs to per-subfolder DB IDs), restore HDR/Panorama auto-tag DB sync per subfolder, recursive Cache-All, ExecuteMacro tag part, XMP keyword auto-create