# Face / closed-eye ONNX models

The toolbar **👁 Detect Closed Eyes** button is wired but the two ONNX models
it needs are not committed to the repo. Drop them in this directory and they
get copied into the build output automatically (`*.onnx` is wildcarded in
`Rawr.App.csproj`).

If either file is missing the feature degrades gracefully — the button still
appears, but clicking it sets a status message naming the missing file(s)
instead of running.

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
