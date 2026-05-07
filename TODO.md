## SONNET:

## OPUS:
- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Subject classifier — small zero-shot model (CLIP-tiny or similar) tags photos with "person", "landscape", "food", "animal". Filterable. Surprisingly accurate even at small sizes.

- ## 2-up and 4-up compare — already on the roadmap. Implement with synced pan/zoom; this is a flagship pro feature.

- ## manually selecting the desired thumbnail for a burst


- ## Custom keyboard macros / chords — "Shift+1 = pick + 5 stars + Yellow label + advance".

- ## Multi-shot HDR detection — bracket sequences, group separately from regular bursts. Detect if shot is HDR (maybe by same content, but with varying exposure levels = HDR?)

- ## detect panorama? how to?

- ## What does the export list button do? remove it? bundle it with the copy button to save space in the top panel?