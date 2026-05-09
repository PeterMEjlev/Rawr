## SONNET:

## OPUS:
- ## Allow user to manually add location metadata to selected photos (using the map function)

- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Subject classifier — small zero-shot model (CLIP-tiny or similar) tags photos with "person", "landscape", "food", "animal". Filterable. Surprisingly accurate even at small sizes.

- ## the tooltip for shortcuts with special characters like ÆØÅ just show up as \ and not the actual keyboard shortcut

- ## "Failed to open video"

