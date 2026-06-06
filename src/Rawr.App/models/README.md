# ONNX models

The toolbar **👁 Detect Closed Eyes** button and the auto-running **subject
classifier** both depend on ONNX models that aren't committed to the repo.
Drop them in this directory and they get copied into the build output
automatically (`*.onnx` and `*.json` are wildcarded in `Rawr.App.csproj`).

If any file is missing the affected feature degrades gracefully — the button /
filter chips still appear, but the feature logs a status message naming the
missing file(s) instead of running.

## Required files

### 1. `face_detection_yunet_2023mar.onnx`

OpenCV's YuNet face detector (Apache-2.0). Outputs face bounding boxes plus
five landmarks per face: right-eye centre, left-eye centre, nose tip, right and
left mouth corners. RAWR uses the eye-centre landmarks to crop each eye.

- Source: <https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet>
- File: `face_detection_yunet_2023mar.onnx` (~340 KB)

### 2. `eye_state.onnx`

A small CNN that takes a grayscale eye crop and reports an open-eye
probability. **Recommended:** PINTO0309's OCEC (Open / Closed Eye Classifier)
checkpoint — fast, accurate, well-maintained.

- Source: <https://github.com/PINTO0309/OCEC>
- File: `ocec_p.onnx` — drop it in this folder as-is, or rename to
  `eye_state.onnx` if you prefer the canonical name. RAWR accepts either
  filename (~few hundred KB).
- Input: `images`, NCHW shape `[N, 1, 24, 40]` (grayscale, 0–1 normalised).
  RAWR resizes each eye crop to those dimensions, preserving aspect ratio.
- Output: `prob_open`, shape `[N, 1]` — gives **P(open) directly**, NOT a
  `[closed, open]` softmax pair. An eye is treated as closed when this
  probability falls below the threshold in **Settings → Faces**.

**Legacy 2-class checkpoints also work.** If the eye model has no `prob_open`
output, RAWR falls back to interpreting the first output as a `[closed, open]`
softmax (using `output[1]` as the open probability). Input dimensions are read
from ONNX metadata, so square 24×24 models or anything else with a static
NCHW shape work without code changes.

## Threshold

Sensitivity is configurable in **Settings → Faces** (default 50 %). An eye
counts as closed when the classifier's "open" probability falls below the
threshold.

## Re-running

The button skips photos already analysed in the current folder's
`.rawr/culling.db`. To force re-analysis, clear the `face_count`,
`closed_eye_count`, `min_eye_open_score` columns:

```sql
UPDATE photos SET face_count = NULL, closed_eye_count = NULL, min_eye_open_score = NULL;
```

---

# Subject classifier (zero-shot CLIP)

The sidebar **Subjects** subsection is populated by a small CLIP-style image
encoder that runs as a low-priority background pass after folder open. Disable
the pass entirely with **Settings → Subjects → Auto-classify** if you don't want
it.

The tags form a shallow two-level taxonomy (see `SubjectTaxonomy` in
`src/Rawr.Core/Models`): **group** roots — *Animal*, *Vehicle*, *Nature* — each
own a handful of **leaf** categories (Dog/Cat/Bird/…, Car/Plane/Bike/…,
Mountain/Forest/Water/…), alongside the standalone categories *Person*, *Food*
and *Architecture*. Both group roots and leaves get their own text embedding and
are scored independently; the runtime then rolls any leaf hit up into its group
(`SubjectTaxonomy.ApplyGroupRollup`), so a group bit is always a superset of its
leaves (no "Dog without Animal"). In the sidebar, group chips expand to reveal
their leaves; a group's count is the union over its leaves.

## Required files

### 1. `subject_image_encoder.onnx`

The CLIP **image encoder** half. Any export with NCHW float input and a
single 1-D embedding output works — input dimensions are read from ONNX
metadata, so 224×224 (standard CLIP) and other sizes are both fine.

Recommended (small, fast, accurate):

- **MobileCLIP-S0** — Apple's compact CLIP. ~55 MB image encoder.
  <https://github.com/apple/ml-mobileclip>
- **OpenCLIP ViT-B-16** — the bigger-but-better fallback (~340 MB image encoder).
  <https://github.com/mlfoundations/open_clip>

Export to ONNX with the model's own export script, then rename the resulting
image-encoder file to `subject_image_encoder.onnx` and drop it in this folder.

### 2. `subject_tags.json`

Precomputed text embeddings for each category, generated with the **matching
text encoder** of whichever CLIP variant produced the image encoder. RAWR
ships only the image encoder so the text encoder + tokenizer aren't a runtime
dependency — but the two halves must come from the same model.

Generate with the helper script:

```powershell
pip install open_clip_torch torch
python tools/generate_subject_embeddings.py `
    --model ViT-B-16 `
    --pretrained datacomp_xl_s13b_b90k `
    --output src/Rawr.App/models/subject_tags.json
```

Edit `TAG_PROMPTS` in the script to extend the prompt set or add new
categories (after first adding them to the `SubjectTag` enum in
`src/Rawr.Core/Models/SubjectTag.cs`, and — for group roots — wiring the
group→leaf relationship in `SubjectTaxonomy`). The names in `TAG_PROMPTS` must
match the enum names case-insensitively; the script writes one embedding per
name and the runtime silently skips any name it can't map.

> **Note:** `datacomp_xl_s13b_b90k` was added to `open_clip_torch` around v2.20.
> Older installs only expose `openai` / `laion400m` tags and will error with
> "Pretrained weights … not found". Use a recent `open_clip_torch`, and make
> sure it's the **same** checkpoint that produced `subject_image_encoder.onnx`
> or the text and image embeddings won't share a vector space.

## Decision rule & threshold

The classifier does **not** threshold each category's raw cosine independently
(that over-fires on uncalibrated categories — e.g. tagging Bird/Dog on a
mountain). Instead it runs a temperature-scaled **softmax** over the top-level
set (standalone categories + group roots) plus a generic `background` anchor,
and applies a top-level category when its *probability* clears the threshold.
Leaves are **parent-gated**: a group's leaves (Dog/Cat/…) are only scored once
the group root passed, then compete in their own softmax so at most the dominant
leaf is tagged. The regenerated `subject_tags.json` therefore includes a
`background` entry — keep it; without it the softmax has nothing to absorb
none-of-the-above frames and precision drops.

Sensitivity is configurable **per top-level group** in **Settings → Subjects**
— one slider each for Person / Animal / Vehicle / Nature / Architecture / Food
(default 22 ⇒ probability ≥ 0.22). Raise a group that over-fires (e.g. Animal
tagging mountains) without making the others stricter; lower one whose subjects
are missed. Stored as `AppSettings.SubjectGroupThresholds` (keyed by group name);
a group with no entry falls back to the global `SubjectTagThreshold`. Leaf
categories (Dog/Bird/…) inherit their parent group's gate, then compete in a
within-group softmax (`SubjectClassifier.LeafProbThreshold`, default 0.30).

The softmax temperature is a code constant (`SubjectClassifier.LogitScale`,
default 50): lower it for softer / more multi-label output, raise it toward
CLIP's trained ~100 for sharper / more single-label output.

## Re-running

The background pass skips photos with a non-NULL `subject_tags` value. To
force re-classification after tweaking the threshold or swapping models:

```sql
UPDATE photos SET subject_tags = NULL;
```
