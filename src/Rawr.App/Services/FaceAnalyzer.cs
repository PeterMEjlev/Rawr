using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Rawr.App.Services;

/// <summary>
/// User-triggered face / closed-eye analysis over the cached preview JPEG.
///
/// Two ONNX models, both shipped in <c>src/Rawr.App/models/</c>:
///
///   • <b>face_detection_yunet_2023mar.onnx</b> — OpenCV's YuNet (Apache-2.0).
///     Returns axis-aligned face bboxes plus 5 landmarks per face: right-eye
///     center, left-eye center, nose tip, right & left mouth corners.
///   • <b>eye_state.onnx</b> — small CNN that takes a grayscale eye crop and
///     reports an open-eye probability. The recommended checkpoint is
///     PINTO0309/OCEC's <c>ocec_p.onnx</c> (renamed): input <c>images</c>
///     shape <c>[N, 1, 24, 40]</c>, output <c>prob_open</c> shape
///     <c>[N, 1]</c> giving P(open) directly (NOT a 2-class [closed, open]
///     softmax). The analyser also accepts older 2-class checkpoints by
///     falling back to <c>output[1]</c> as the open probability.
///
/// Both shape and output interpretation are read from ONNX metadata at init,
/// so swapping in a different eye model with different dimensions just works
/// — RAWR resizes the eye crop to whatever the model expects.
///
/// Both files are optional. <see cref="IsAvailable"/> reports whether the
/// pipeline is wired up; if not, <see cref="UnavailableReason"/> tells the
/// caller why so the UI can surface it. Construction is cheap (no model load
/// happens until the first <see cref="Analyze"/> call) so failure to ship the
/// models doesn't slow folder-open or punish users who never press the button.
///
/// This class is thread-safe for concurrent <see cref="Analyze"/> calls — the
/// underlying ONNX <see cref="InferenceSession"/> is reentrant when called
/// with separate input arrays. The lazy init is double-checked under a lock.
/// </summary>
public sealed class FaceAnalyzer : IDisposable
{
    private const string FaceModelFile = "face_detection_yunet_2023mar.onnx";

    // Eye-state classifier filename candidates, in priority order. The first
    // file that actually exists in the models directory wins. "eye_state.onnx"
    // is the canonical name; "ocec_p.onnx" is accepted as-is so users who
    // download the recommended PINTO0309/OCEC checkpoint don't have to rename
    // it. Add more aliases here if you ship alternate models.
    private static readonly string[] EyeModelCandidates =
    {
        "eye_state.onnx",
        "ocec_p.onnx",
    };

    // YuNet is anchor-free; these are the strides at which it predicts.
    private static readonly int[] YuNetStrides = { 8, 16, 32 };

    // Square inference size for YuNet. 320 keeps cost low; faces still detect
    // reliably down to ~24 px in the resized image (≈ 120 px in a 1620-px
    // preview).
    private const int FaceInputSize = 320;

    // Confidence floor for YuNet face proposals is user-tunable via
    // AppSettings.FaceDetectionConfidence (Settings → Classification); the former
    // 0.6 default lives there now. YuNet's "score" is the geometric mean of the
    // cls and IoU branches; 0.6 suppresses false positives on busy scenes.
    private const float FaceNmsIoUThreshold = 0.3f;

    // Eye crop is sized as a fraction of the face's longer side. YuNet eye
    // landmarks are eye centres, so we want a region around them big enough
    // to cover the full eye (lid to lid) plus a little context. The 0.28 is
    // the side length when the model expects a square crop; for rectangular
    // models the longer dim gets this length and the shorter dim is scaled
    // by the model's aspect ratio so the eye isn't distorted on resize.
    private const float EyeCropFraction = 0.28f;

    // Fallback dims if the eye model has no static shape in its metadata
    // (rare — most exports do). 24×40 matches PINTO0309/OCEC.
    private const int EyeFallbackW = 40;
    private const int EyeFallbackH = 24;

    // Name OCEC exports for its single output. When present we treat the
    // tensor as P(open) directly (single value per eye); when absent we fall
    // back to a legacy 2-class [closed, open] softmax convention.
    private const string OcecOpenOutputName = "prob_open";

    private readonly object _initLock = new();
    private InferenceSession? _faceSession;
    private InferenceSession? _eyeSession;
    private string? _faceInputName;
    private string? _eyeInputName;
    private string? _eyeOutputName;
    // True when the eye model emits P(open) directly under the OCEC
    // convention (output named "prob_open"); false when the output is a
    // legacy 2-class softmax with index 1 = open.
    private bool _eyeOutputIsDirectOpenProb;
    private int _eyeInputW = EyeFallbackW;
    private int _eyeInputH = EyeFallbackH;
    private volatile bool _initAttempted;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Trigger model load (cheap if already loaded). Call this before reading
    /// <see cref="IsAvailable"/> / <see cref="UnavailableReason"/> from UI code
    /// that needs to decide whether to proceed with the batch.
    /// </summary>
    public void Initialize() => EnsureInitialized();

    public FaceAnalysisResult? Analyze(byte[] previewJpeg, float closedThreshold)
    {
        EnsureInitialized();
        if (!IsAvailable) return null;

        // Decode preview JPEG to BGR pixels at a known size.
        var rgb = DecodeJpegToBgr(previewJpeg, out int srcW, out int srcH);
        if (rgb == null) return null;

        // Resize to the face-detector input square via WIC (the same path used
        // for the cached previews, so quality is consistent with rest of app).
        var resized = ResizeBgr(rgb, srcW, srcH, FaceInputSize, FaceInputSize);

        var faces = RunFaceDetection(resized, FaceInputSize, FaceInputSize);
        if (faces.Count == 0)
            return new FaceAnalysisResult(0, 0, 1.0f);

        // Map landmarks back from FaceInputSize space into the original preview
        // pixel space so eye crops come from the higher-resolution decode.
        float scaleX = (float)srcW / FaceInputSize;
        float scaleY = (float)srcH / FaceInputSize;

        // Aspect-aware crop sizing. The eye model may want a non-square input
        // (OCEC is 24×40 H×W). Cropping a rectangle that already matches the
        // model aspect ratio means the resize doesn't squash the eye.
        int longerDim = Math.Max(_eyeInputW, _eyeInputH);
        float halfWFactor = (float)_eyeInputW / longerDim; // 1.0 for square / wider models
        float halfHFactor = (float)_eyeInputH / longerDim;

        float minOpen = 1.0f;
        int closedFaces = 0;

        foreach (var f in faces)
        {
            float faceW = f.W * scaleX;
            float baseHalf = faceW * EyeCropFraction * 0.5f;
            int halfW = Math.Max(4, (int)MathF.Round(baseHalf * halfWFactor));
            int halfH = Math.Max(4, (int)MathF.Round(baseHalf * halfHFactor));

            bool faceHasClosedEye = false;
            // Right and left eye landmark indices per YuNet docs.
            for (int k = 0; k < 2; k++)
            {
                float lx = f.Landmarks[k * 2]     * scaleX;
                float ly = f.Landmarks[k * 2 + 1] * scaleY;

                var crop = CropGray(rgb, srcW, srcH, (int)lx, (int)ly, halfW, halfH);
                if (crop == null) continue;

                float openProb = ClassifyEyeOpenProb(crop);
                if (openProb < minOpen) minOpen = openProb;
                if (openProb < closedThreshold) faceHasClosedEye = true;
            }

            if (faceHasClosedEye) closedFaces++;
        }

        return new FaceAnalysisResult(faces.Count, closedFaces, minOpen);
    }

    /// <summary>
    /// Diagnostic variant of <see cref="Analyze"/> that keeps the per-face
    /// geometry and eye-open probabilities (normalised to the image, 0–1) so the
    /// debug overlay can draw boxes and list scores. Mirrors <see cref="Analyze"/>'s
    /// pipeline; kept separate so the bulk path stays allocation-light.
    /// </summary>
    public FaceDebugResult? AnalyzeDebug(byte[] previewJpeg, float closedThreshold)
    {
        EnsureInitialized();
        if (!IsAvailable) return null;

        var rgb = DecodeJpegToBgr(previewJpeg, out int srcW, out int srcH);
        if (rgb == null) return null;

        var resized = ResizeBgr(rgb, srcW, srcH, FaceInputSize, FaceInputSize);
        var faces = RunFaceDetection(resized, FaceInputSize, FaceInputSize);
        if (faces.Count == 0)
            return new FaceDebugResult(0, 0, 1.0f, srcW, srcH, Array.Empty<FaceDebugFace>());

        float scaleX = (float)srcW / FaceInputSize;
        float scaleY = (float)srcH / FaceInputSize;
        int longerDim = Math.Max(_eyeInputW, _eyeInputH);
        float halfWFactor = (float)_eyeInputW / longerDim;
        float halfHFactor = (float)_eyeInputH / longerDim;

        float minOpen = 1.0f;
        int closedFaces = 0;
        var list = new List<FaceDebugFace>(faces.Count);

        foreach (var f in faces)
        {
            float faceW = f.W * scaleX;
            float baseHalf = faceW * EyeCropFraction * 0.5f;
            int halfW = Math.Max(4, (int)MathF.Round(baseHalf * halfWFactor));
            int halfH = Math.Max(4, (int)MathF.Round(baseHalf * halfHFactor));

            // k=0 right eye, k=1 left eye (YuNet landmark order).
            float rightOpen = float.NaN, leftOpen = float.NaN;
            bool faceHasClosedEye = false;
            for (int k = 0; k < 2; k++)
            {
                float lx = f.Landmarks[k * 2]     * scaleX;
                float ly = f.Landmarks[k * 2 + 1] * scaleY;
                var crop = CropGray(rgb, srcW, srcH, (int)lx, (int)ly, halfW, halfH);
                if (crop == null) continue;

                float openProb = ClassifyEyeOpenProb(crop);
                if (k == 0) rightOpen = openProb; else leftOpen = openProb;
                if (openProb < minOpen) minOpen = openProb;
                if (openProb < closedThreshold) faceHasClosedEye = true;
            }
            if (faceHasClosedEye) closedFaces++;

            // Normalise the box from the 320² detection square to 0–1 image space
            // (the square squashes the whole frame, so dividing by FaceInputSize maps
            // straight back to fractions of width/height).
            list.Add(new FaceDebugFace(
                f.X / FaceInputSize, f.Y / FaceInputSize, f.W / FaceInputSize, f.H / FaceInputSize,
                f.Score, rightOpen, leftOpen, faceHasClosedEye));
        }

        return new FaceDebugResult(faces.Count, closedFaces, minOpen, srcW, srcH, list);
    }

    // ── Initialisation ──

    private void EnsureInitialized()
    {
        if (_initAttempted) return;
        lock (_initLock)
        {
            if (_initAttempted) return;
            _initAttempted = true;

            try
            {
                var modelDir = ResolveModelDirectory();
                var facePath = Path.Combine(modelDir, FaceModelFile);
                var eyePath  = EyeModelCandidates
                    .Select(f => Path.Combine(modelDir, f))
                    .FirstOrDefault(File.Exists);

                if (!File.Exists(facePath) || eyePath == null)
                {
                    var missing = new List<string>();
                    if (!File.Exists(facePath)) missing.Add(FaceModelFile);
                    if (eyePath == null) missing.Add($"one of [{string.Join(", ", EyeModelCandidates)}]");
                    UnavailableReason = $"Missing model file(s) in {modelDir}: {string.Join(", ", missing)}. See models/README.md.";
                    return;
                }

                var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,  // we parallelise across photos in the VM
                    IntraOpNumThreads = 1,
                };

                _faceSession = new InferenceSession(facePath, opts);
                _eyeSession  = new InferenceSession(eyePath,  opts);

                _faceInputName = _faceSession.InputMetadata.Keys.First();
                _eyeInputName  = _eyeSession.InputMetadata.Keys.First();

                // Read NCHW dims from the eye input metadata. Static dims are
                // positive ints; dynamic dims (rare for these models) come back
                // as -1 or 0 — fall back to OCEC defaults in that case.
                var eyeDims = _eyeSession.InputMetadata[_eyeInputName].Dimensions;
                if (eyeDims.Length >= 4)
                {
                    int h = eyeDims[2];
                    int w = eyeDims[3];
                    if (h > 0) _eyeInputH = h;
                    if (w > 0) _eyeInputW = w;
                }

                // Prefer OCEC's "prob_open" output when present; otherwise take
                // the first declared output and treat it as a 2-class softmax
                // (legacy convention).
                var eyeOutputs = _eyeSession.OutputMetadata.Keys.ToList();
                if (eyeOutputs.Contains(OcecOpenOutputName))
                {
                    _eyeOutputName = OcecOpenOutputName;
                    _eyeOutputIsDirectOpenProb = true;
                }
                else
                {
                    _eyeOutputName = eyeOutputs.First();
                    _eyeOutputIsDirectOpenProb = false;
                }

                IsAvailable = true;
            }
            catch (Exception ex)
            {
                UnavailableReason = $"Failed to load ONNX models: {ex.Message}";
                _faceSession?.Dispose(); _faceSession = null;
                _eyeSession?.Dispose();  _eyeSession  = null;
            }
        }
    }

    private static string ResolveModelDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "models");
    }

    // ── Face detection (YuNet) ──

    private readonly record struct DetectedFace(float X, float Y, float W, float H, float Score, float[] Landmarks);

    private List<DetectedFace> RunFaceDetection(byte[] bgrInput, int width, int height)
    {
        // YuNet input: NCHW float32, shape [1, 3, H, W], BGR, no normalisation
        // (raw 0–255 values).
        int planeSize = width * height;
        var data = new float[3 * planeSize];
        for (int i = 0; i < planeSize; i++)
        {
            data[i]                    = bgrInput[i * 3];      // B
            data[i + planeSize]        = bgrInput[i * 3 + 1];  // G
            data[i + 2 * planeSize]    = bgrInput[i * 3 + 2];  // R
        }

        var tensor = new DenseTensor<float>(data, new[] { 1, 3, height, width });
        using var results = _faceSession!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_faceInputName!, tensor)
        });

        // YuNet emits, per stride: cls_<s> [N], obj_<s> [N], bbox_<s> [N,4],
        // kps_<s> [N,10]. Older builds use indexed names ("loc", "conf", "iou")
        // but the 2023-mar release uses the named-by-stride convention.
        var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());

        // User-tunable confidence floor (Settings → Classification). Snapshot once
        // — it's read against every anchor in the loops below.
        float faceScoreThreshold = Math.Clamp(AppSettings.Current.FaceDetectionConfidence / 100f, 0f, 1f);
        float faceScoreThresholdSq = faceScoreThreshold * faceScoreThreshold;

        var raw = new List<DetectedFace>();
        foreach (var stride in YuNetStrides)
        {
            if (!byName.TryGetValue($"cls_{stride}", out var cls)) continue;
            if (!byName.TryGetValue($"obj_{stride}", out var obj)) continue;
            if (!byName.TryGetValue($"bbox_{stride}", out var bbox)) continue;
            if (!byName.TryGetValue($"kps_{stride}", out var kps)) continue;

            int gridW = width  / stride;
            int gridH = height / stride;
            int n     = gridW * gridH;

            // ToArray() avoids paying the indexer dispatch cost per pixel and
            // lets the JIT keep these in registers.
            var clsArr  = cls.ToArray();
            var objArr  = obj.ToArray();
            var bboxArr = bbox.ToArray();
            var kpsArr  = kps.ToArray();

            for (int idx = 0; idx < n; idx++)
            {
                // YuNet's score = sqrt(cls * obj). Cheap to filter with the squared
                // version to skip the sqrt for the long tail of low-score anchors.
                float clsP = Sigmoidish(clsArr[idx]);
                float objP = Sigmoidish(objArr[idx]);
                float scoreSq = clsP * objP;
                if (scoreSq < faceScoreThresholdSq) continue;

                int gx = idx % gridW;
                int gy = idx / gridW;
                float anchorCx = (gx + 0.5f) * stride;
                float anchorCy = (gy + 0.5f) * stride;

                float cx = anchorCx + bboxArr[idx * 4]     * stride;
                float cy = anchorCy + bboxArr[idx * 4 + 1] * stride;
                float w  = MathF.Exp(bboxArr[idx * 4 + 2]) * stride;
                float h  = MathF.Exp(bboxArr[idx * 4 + 3]) * stride;

                var lms = new float[10];
                for (int k = 0; k < 5; k++)
                {
                    lms[k * 2]     = anchorCx + kpsArr[idx * 10 + k * 2]     * stride;
                    lms[k * 2 + 1] = anchorCy + kpsArr[idx * 10 + k * 2 + 1] * stride;
                }

                raw.Add(new DetectedFace(
                    X: cx - w / 2,
                    Y: cy - h / 2,
                    W: w,
                    H: h,
                    Score: MathF.Sqrt(scoreSq),
                    Landmarks: lms));
            }
        }

        return NonMaxSuppression(raw, FaceNmsIoUThreshold);
    }

    // YuNet outputs raw logits in some exports and post-sigmoid probs in others
    // depending on the conversion script. Treat anything in [0,1] as already
    // probabilities; otherwise apply sigmoid. Cheap and robust to either.
    private static float Sigmoidish(float v)
    {
        if (v >= 0f && v <= 1f) return v;
        return 1f / (1f + MathF.Exp(-v));
    }

    private static List<DetectedFace> NonMaxSuppression(List<DetectedFace> proposals, float iouThr)
    {
        var sorted = proposals.OrderByDescending(p => p.Score).ToList();
        var keep = new List<DetectedFace>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(p => Iou(best, p) > iouThr);
        }
        return keep;
    }

    private static float Iou(DetectedFace a, DetectedFace b)
    {
        float ix1 = MathF.Max(a.X, b.X);
        float iy1 = MathF.Max(a.Y, b.Y);
        float ix2 = MathF.Min(a.X + a.W, b.X + b.W);
        float iy2 = MathF.Min(a.Y + a.H, b.Y + b.H);
        float iw = MathF.Max(0f, ix2 - ix1);
        float ih = MathF.Max(0f, iy2 - iy1);
        float inter = iw * ih;
        float union = a.W * a.H + b.W * b.H - inter;
        return union <= 0f ? 0f : inter / union;
    }

    // ── Eye-state classification ──

    private float ClassifyEyeOpenProb(byte[] grayCrop)
    {
        // Eye classifier input: NCHW float32 [1, 1, H, W], pixels normalised
        // 0..1. H/W came from the model's input metadata at init time so this
        // works for both OCEC's 24×40 and any square legacy checkpoint.
        int len = _eyeInputW * _eyeInputH;
        var data = new float[len];
        for (int i = 0; i < len; i++)
            data[i] = grayCrop[i] / 255f;

        var tensor = new DenseTensor<float>(data, new[] { 1, 1, _eyeInputH, _eyeInputW });
        using var results = _eyeSession!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_eyeInputName!, tensor)
        });

        // Pull the specific output named at init (OCEC uses "prob_open"; legacy
        // models use whatever name they ship with).
        var named = results.First(r => r.Name == _eyeOutputName);
        var output = named.AsTensor<float>().ToArray();
        if (output.Length == 0) return 1.0f;

        if (_eyeOutputIsDirectOpenProb)
        {
            // OCEC convention: a single value per eye, P(open) directly. The
            // tensor shape is [N, 1] but flattened that's just output[0]. NOT
            // a [closed, open] softmax — index 0 IS the open probability here.
            return Math.Clamp(output[0], 0f, 1f);
        }

        // Legacy 2-class head: index 0 = closed, index 1 = open. Some
        // checkpoints emit raw logits (apply softmax); others ship a
        // softmax-baked head. Detect by checking if the pair sums to 1.
        if (output.Length < 2) return 1.0f;
        float a = output[0], b = output[1];
        if (a >= 0f && b >= 0f && Math.Abs(a + b - 1f) < 0.01f)
            return b;

        // Softmax on a 2-vector reduces to a sigmoid of the difference.
        return 1f / (1f + MathF.Exp(-(b - a)));
    }

    // ── Pixel utilities ──

    /// <summary>Decode a JPEG into a tightly packed BGR byte array.</summary>
    private static byte[]? DecodeJpegToBgr(byte[] jpeg, out int width, out int height)
    {
        width = height = 0;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(jpeg);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();

            var converted = new FormatConvertedBitmap(bi, PixelFormats.Bgr24, null, 0);
            converted.Freeze();

            width = converted.PixelWidth;
            height = converted.PixelHeight;
            int stride = width * 3;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bilinear-resize a BGR buffer. Implemented in-process (no WIC round-trip)
    /// so the inference path is allocation-light and trivially thread-safe.
    /// </summary>
    private static byte[] ResizeBgr(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 3];
        float xRatio = (sw - 1f) / dw;
        float yRatio = (sh - 1f) / dh;

        for (int y = 0; y < dh; y++)
        {
            float sy = y * yRatio;
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, sh - 1);
            float wy = sy - y0;

            for (int x = 0; x < dw; x++)
            {
                float sx = x * xRatio;
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, sw - 1);
                float wx = sx - x0;

                int p00 = (y0 * sw + x0) * 3;
                int p01 = (y0 * sw + x1) * 3;
                int p10 = (y1 * sw + x0) * 3;
                int p11 = (y1 * sw + x1) * 3;
                int dp  = (y * dw + x) * 3;

                for (int c = 0; c < 3; c++)
                {
                    float v = src[p00 + c] * (1 - wx) * (1 - wy) +
                              src[p01 + c] * wx       * (1 - wy) +
                              src[p10 + c] * (1 - wx) * wy       +
                              src[p11 + c] * wx       * wy;
                    dst[dp + c] = (byte)v;
                }
            }
        }
        return dst;
    }

    /// <summary>
    /// Crop a (possibly rectangular) region centred on (cx, cy) with half-edges
    /// <paramref name="halfW"/>×<paramref name="halfH"/> and resample it to the
    /// eye model's expected <see cref="_eyeInputW"/>×<see cref="_eyeInputH"/>
    /// grayscale input. Returns null if the region falls entirely outside the
    /// image.
    /// </summary>
    private byte[]? CropGray(byte[] bgr, int sw, int sh, int cx, int cy, int halfW, int halfH)
    {
        int x0 = cx - halfW;
        int y0 = cy - halfH;
        int srcW = halfW * 2;
        int srcH = halfH * 2;
        if (srcW <= 0 || srcH <= 0) return null;
        if (x0 + srcW <= 0 || y0 + srcH <= 0 || x0 >= sw || y0 >= sh) return null;

        var crop = new byte[_eyeInputW * _eyeInputH];
        float xRatio = (float)srcW / _eyeInputW;
        float yRatio = (float)srcH / _eyeInputH;
        for (int oy = 0; oy < _eyeInputH; oy++)
        {
            int sy = y0 + (int)(oy * yRatio);
            if (sy < 0) sy = 0;
            else if (sy >= sh) sy = sh - 1;
            for (int ox = 0; ox < _eyeInputW; ox++)
            {
                int sx = x0 + (int)(ox * xRatio);
                if (sx < 0) sx = 0;
                else if (sx >= sw) sx = sw - 1;
                int p = (sy * sw + sx) * 3;
                // BT.601 luma — fine for the eye classifier which was trained on
                // grayscale crops anyway.
                int luma = (bgr[p] * 29 + bgr[p + 1] * 150 + bgr[p + 2] * 77) >> 8;
                crop[oy * _eyeInputW + ox] = (byte)luma;
            }
        }
        return crop;
    }

    public void Dispose()
    {
        _faceSession?.Dispose();
        _eyeSession?.Dispose();
    }
}

public sealed record FaceAnalysisResult(int FaceCount, int ClosedEyeCount, float MinEyeOpenScore);

/// <summary>One detected face for the debug overlay. Box coords are normalised
/// (0–1) to the image; eye-open probs are 0–1, or NaN when the eye crop failed.</summary>
public sealed record FaceDebugFace(
    float X, float Y, float W, float H,
    float Score, float RightEyeOpen, float LeftEyeOpen, bool HasClosedEye);

/// <summary>Per-face detection detail plus the source image dimensions (so the
/// overlay can be built at the right aspect ratio).</summary>
public sealed record FaceDebugResult(
    int FaceCount, int ClosedEyeCount, float MinEyeOpenScore,
    int ImageWidth, int ImageHeight, IReadOnlyList<FaceDebugFace> Faces);
