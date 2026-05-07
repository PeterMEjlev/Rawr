## SONNET:

## OPUS:
- ## Make focus peaking more robust against high contrast area which arent sharp - the alternative I'd try next is the multi-scale ratio (LoG at σ≈1 vs σ≈2 — sharp edges concentrate at the small scale, soft ones spread). It's a different mechanism than contrast-normalization and may behave better for your shots

- ## Subject classifier — small zero-shot model (CLIP-tiny or similar) tags photos with "person", "landscape", "food", "animal". Filterable. Surprisingly accurate even at small sizes.

- ## manually selecting the desired thumbnail for a burst

- ## Custom keyboard macros / chords — "Shift+1 = pick + 5 stars + Yellow label + tag".

- ## Multi-shot HDR detection — bracket sequences, group separately from regular bursts. Detect if shot is HDR (maybe by same content, but with varying exposure levels = HDR?)
no frame shift
different exposure levels
high perceptual hash similarity


- ## detect panorama? 
For each neighboring pair, estimate how the second image moved relative to the previous one:

Frame 1 -> Frame 2: shifted 28% right
Frame 2 -> Frame 3: shifted 31% right
Frame 3 -> Frame 4: shifted 27% right

That looks like a panorama.

A normal burst might look like:

Frame 1 -> Frame 2: shifted 2% right
Frame 2 -> Frame 3: shifted 1% left
Frame 3 -> Frame 4: shifted 3% right

That is just small movement.

A random set might look like:

Frame 1 -> Frame 2: shifted 40% right
Frame 2 -> Frame 3: shifted 15% down
Frame 3 -> Frame 4: shifted 60% left

Not a clean panorama.

A panorama usually has moderate overlap:

30% to 80% overlap

Too much overlap likely means normal burst:

90% to 100% overlap = probably burst

Too little overlap likely means unrelated shots:

0% to 20% overlap = probably not panorama

A reasonable rough rule:

if overlap between neighboring frames is 35-85%
and movement direction is consistent
and image count >= 3
then classify as panorama candidate

- ## What does the export list button do? remove it? bundle it with the copy button to save space in the top panel?