### **SONNET:

- ## Update the SHORTCUTS panel with new shortcuts

- ## be able to change timeline size in full screen

- ## be able to remove timeline in full screen view

- ## Folders that are open in RAWR dont disappear when ejected

## OPUS:
- ## Allow user to manually add location metadata to selected photos (using the map function)

- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Phase 2 of recursive folder view: tag editing across subfolders (translate synthetic display IDs to per-subfolder DB IDs), restore HDR/Panorama auto-tag DB sync per subfolder, recursive Cache-All, ExecuteMacro tag part, XMP keyword auto-create

- ## The current model used for classifying images based on the subjects is not good enough. it attaches the DOG tag to two images (one is an image of a mountain and a person, and te other is just of the mountain), the BIRD tag is attached to multiple iomages of gymnasts as well as an image of a mountain. The MOUNTAIN and FOREST classification is generally okay. What can we do to improve the efficacy of the classification?