using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Rawr.Core.Models;

namespace Rawr.App.Services;

/// <summary>
/// Zero-shot subject classifier (CLIP-style). Tags a photo's cached thumbnail
/// with a fixed set of coarse categories: person / landscape / food / animal.
///
/// Architecture: we ship the image encoder only — the text-prompt embeddings
/// for each category are precomputed offline and stored alongside the model in
/// <c>subject_tags.json</c>. At inference time we compute the image embedding,
/// L2-normalise it, and score it against every tag's (already L2-normalised)
/// text embedding.
///
/// Decision rule (not a flat per-tag threshold): raw CLIP cosine similarities
/// aren't calibrated across categories — "a photo of a bird" sits closer to the
/// image manifold than "a forest" for almost any outdoor shot, so a single
/// absolute gate over-fires on some categories and under-fires on others. We
/// instead take a temperature-scaled <b>softmax</b> over the top-level set
/// (standalone categories + group roots) plus a generic <c>background</c> anchor
/// that absorbs probability mass for none-of-the-above frames. A top-level
/// category is applied when its softmax probability clears
/// <see cref="AppSettings.SubjectTagThreshold"/> (scaled 0–1). Leaves are
/// <b>parent-gated</b>: a group's leaves (Dog/Cat/…) are only considered once the
/// group root itself passed, and then they compete in their own softmax so we
/// never tag Dog+Cat+Bird on one frame. This is what stops Animal leaves from
/// firing on a mountain (Nature wins the top-level softmax → Animal never opens
/// its leaves) and Bird from firing on a gymnast (Person wins).
///
/// Both files are optional. <see cref="IsAvailable"/> reports whether the
/// pipeline is wired up; if not, <see cref="UnavailableReason"/> tells the
/// caller why so the UI can surface it. Lazy init mirrors
/// <see cref="FaceAnalyzer"/> — model load happens on the first
/// <see cref="Classify"/> call rather than at construction so folder-open
/// stays snappy on machines that never installed the models.
///
/// Thread-safe for concurrent <see cref="Classify"/> calls: the ONNX session
/// is reentrant when given independent input tensors, and the embedding table
/// is read-only after init.
/// </summary>
public sealed class SubjectClassifier : IDisposable
{
    // Image encoder file. Any CLIP-style image encoder ONNX with NCHW input
    // and a single 1-D embedding output works — input dims are read from
    // metadata at init, so swapping in a different export (MobileCLIP,
    // OpenCLIP, TinyCLIP) just works as long as it matches the model whose
    // text encoder produced subject_tags.json.
    private const string ImageModelFile = "subject_image_encoder.onnx";

    // Precomputed text embeddings + tag metadata. Schema described at the
    // bottom of this file (see SubjectTagsFile / TagEntry). Generated offline
    // by tools/generate_subject_embeddings.py.
    private const string TagsFile = "subject_tags.json";

    // CLIP/MobileCLIP convention: pixels normalised with these per-channel
    // means/std-devs (RGB order). The embedding generator script uses the
    // same constants so the image and text embeddings live in the same space.
    private static readonly float[] PixelMean = { 0.48145466f, 0.4578275f,  0.40821073f };
    private static readonly float[] PixelStd  = { 0.26862954f, 0.26130258f, 0.27577711f };

    // Fallback input size when the encoder doesn't expose a static H/W in
    // metadata (rare — almost every export pins this). 224 is the standard
    // CLIP image-encoder input.
    private const int FallbackInputSize = 224;

    // Softmax temperature (logit = cosine × scale). CLIP's trained logit scale
    // is ~100, which is very peaky — great for single-label argmax but it
    // collapses the threshold knob and kills legitimate secondary subjects. We
    // run softer so the probabilities stay graded and a genuine second subject
    // (person on a mountain) can still clear the threshold. Lower = softer /
    // more multi-label; higher = sharper / more single-label.
    private const float LogitScale = 50f;

    // Within a group that already passed, the dominant leaf must hold at least
    // this share of the group's leaf softmax (background included) to be tagged.
    // Keeps borderline leaves off without losing the obvious ones.
    private const float LeafProbThreshold = 0.30f;

    // Special tag name in subject_tags.json whose embedding is the
    // none-of-the-above anchor. Participates in every softmax but is never
    // emitted as a SubjectTag.
    private const string BackgroundTagName = "background";

    private readonly object _initLock = new();
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private int _inputH = FallbackInputSize;
    private int _inputW = FallbackInputSize;
    private int _embedDim;
    private Dictionary<SubjectTag, float[]> _embeddings = new();
    private float[]? _background;
    private volatile bool _initAttempted;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    /// <summary>Trigger model load (cheap if already loaded).</summary>
    public void Initialize() => EnsureInitialized();

    /// <summary>
    /// Classify a JPEG (typically the cached thumbnail or preview) and return
    /// the set of tags that cleared their threshold. <paramref name="groupThresholdFor"/>
    /// resolves the softmax-probability gate for each top-level group, so the
    /// caller can apply per-group sensitivity (a group the model struggles with
    /// can demand more certainty than the rest). Returns null if the classifier
    /// isn't available or the JPEG can't be decoded. Returning
    /// <see cref="SubjectTag.None"/> means classification ran successfully and
    /// nothing scored high enough — distinct from a null "not classified yet"
    /// result that the persistence layer leans on.
    /// </summary>
    public SubjectTag? Classify(byte[] jpeg, Func<SubjectTag, float> groupThresholdFor)
    {
        EnsureInitialized();
        if (!IsAvailable) return null;

        var imageEmbed = ComputeImageEmbedding(jpeg);
        if (imageEmbed == null) return null;

        // Top-level decision: softmax over standalone categories + group roots
        // (plus the background anchor), so categories compete instead of each
        // racing a fixed cosine floor. See the class header for why this beats
        // independent thresholding.
        var topProbs = SoftmaxOver(SubjectTaxonomy.Groups.Select(g => g.Group), imageEmbed);

        SubjectTag result = SubjectTag.None;
        foreach (var g in SubjectTaxonomy.Groups)
        {
            if (!topProbs.TryGetValue(g.Group, out float p) || p < groupThresholdFor(g.Group)) continue;
            result |= g.Group;
            if (g.Leaves.Count == 0) continue;

            // Parent gate passed — let this group's leaves compete among
            // themselves (background included) and emit only the dominant one(s).
            var leafProbs = SoftmaxOver(g.Leaves, imageEmbed);
            foreach (var leaf in g.Leaves)
                if (leafProbs.TryGetValue(leaf, out float lp) && lp >= LeafProbThreshold)
                    result |= leaf;
        }

        // Defensive: leaves only fire behind a passed parent, so the group bit is
        // already set, but keep the invariant explicit (group ⊇ its leaves).
        return SubjectTaxonomy.ApplyGroupRollup(result);
    }

    /// <summary>
    /// Decode, preprocess and run the image encoder, returning the
    /// L2-normalised image embedding (or null if the JPEG can't be decoded /
    /// the model output is shorter than the embedding dim).
    /// </summary>
    private float[]? ComputeImageEmbedding(byte[] jpeg)
    {
        var rgb = DecodeJpegToRgb(jpeg, out int srcW, out int srcH);
        if (rgb == null) return null;

        var resized = ResizeRgb(rgb, srcW, srcH, _inputW, _inputH);
        var input = NormalizeToNchw(resized, _inputW, _inputH);

        var tensor = new DenseTensor<float>(input, new[] { 1, 3, _inputH, _inputW });
        using var results = _session!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, tensor)
        });

        var named = results.First(r => r.Name == _outputName);
        var embedding = named.AsTensor<float>().ToArray();

        // CLIP image encoders sometimes emit a sequence ([1, N, D]) and
        // sometimes the pooled vector ([1, D]). Take the last D values either
        // way — that's the CLS / projection-head output we want.
        if (embedding.Length < _embedDim) return null;
        var imageEmbed = new float[_embedDim];
        Array.Copy(embedding, embedding.Length - _embedDim, imageEmbed, 0, _embedDim);
        L2Normalize(imageEmbed);
        return imageEmbed;
    }

    /// <summary>
    /// Temperature-scaled softmax of the image against the given category set,
    /// with the <c>background</c> anchor added to the denominator (when present)
    /// so probabilities reflect "this category vs. everything else, incl. none".
    /// Categories without a loaded embedding are silently omitted; the returned
    /// dictionary never contains the background entry.
    /// </summary>
    private Dictionary<SubjectTag, float> SoftmaxOver(IEnumerable<SubjectTag> flags, float[] imageEmbed)
    {
        var logits = new List<(SubjectTag Flag, float Logit)>();
        float max = float.NegativeInfinity;
        foreach (var f in flags)
        {
            if (!_embeddings.TryGetValue(f, out var e)) continue;
            float logit = Dot(imageEmbed, e) * LogitScale;
            logits.Add((f, logit));
            if (logit > max) max = logit;
        }

        float bgLogit = _background != null ? Dot(imageEmbed, _background) * LogitScale : float.NegativeInfinity;
        if (_background != null && bgLogit > max) max = bgLogit;

        var result = new Dictionary<SubjectTag, float>(logits.Count);
        if (logits.Count == 0) return result;

        double denom = 0;
        foreach (var (_, logit) in logits) denom += Math.Exp(logit - max);
        if (_background != null) denom += Math.Exp(bgLogit - max);
        if (denom <= 0) return result;

        foreach (var (flag, logit) in logits)
            result[flag] = (float)(Math.Exp(logit - max) / denom);
        return result;
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
                var modelDir = Path.Combine(AppContext.BaseDirectory, "models");
                var modelPath = Path.Combine(modelDir, ImageModelFile);
                var tagsPath  = Path.Combine(modelDir, TagsFile);

                var missing = new List<string>();
                if (!File.Exists(modelPath)) missing.Add(ImageModelFile);
                if (!File.Exists(tagsPath))  missing.Add(TagsFile);
                if (missing.Count > 0)
                {
                    UnavailableReason = $"Missing model file(s) in {modelDir}: {string.Join(", ", missing)}. See models/README.md.";
                    return;
                }

                var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                };
                _session = new InferenceSession(modelPath, opts);

                _inputName  = _session.InputMetadata.Keys.First();
                _outputName = _session.OutputMetadata.Keys.First();

                var inputDims = _session.InputMetadata[_inputName].Dimensions;
                if (inputDims.Length >= 4)
                {
                    if (inputDims[2] > 0) _inputH = inputDims[2];
                    if (inputDims[3] > 0) _inputW = inputDims[3];
                }

                var json = File.ReadAllText(tagsPath);
                var parsed = JsonSerializer.Deserialize<SubjectTagsFile>(json, JsonOpts);
                if (parsed?.Tags == null || parsed.Tags.Count == 0)
                {
                    UnavailableReason = $"{TagsFile} contains no tags.";
                    _session.Dispose(); _session = null;
                    return;
                }
                _embedDim = parsed.Tags[0].Embedding?.Length ?? 0;
                if (_embedDim == 0)
                {
                    UnavailableReason = $"{TagsFile}: embeddings are empty. Regenerate with tools/generate_subject_embeddings.py.";
                    _session.Dispose(); _session = null;
                    return;
                }

                var embeddings = new Dictionary<SubjectTag, float[]>(parsed.Tags.Count);
                float[]? background = null;
                foreach (var t in parsed.Tags)
                {
                    if (string.IsNullOrEmpty(t.Name)) continue;
                    if (t.Embedding == null || t.Embedding.Length != _embedDim) continue;
                    var copy = (float[])t.Embedding.Clone();
                    L2Normalize(copy);

                    if (string.Equals(t.Name, BackgroundTagName, StringComparison.OrdinalIgnoreCase))
                    {
                        background = copy;
                        continue;
                    }
                    if (!TryMapFlag(t.Name, out var flag)) continue;
                    embeddings[flag] = copy;
                }
                if (embeddings.Count == 0)
                {
                    UnavailableReason = $"{TagsFile}: no usable tag entries (names must match SubjectTag values).";
                    _session.Dispose(); _session = null;
                    return;
                }
                _embeddings = embeddings;
                _background = background;

                IsAvailable = true;
            }
            catch (Exception ex)
            {
                UnavailableReason = $"Failed to load subject-classifier model: {ex.Message}";
                _session?.Dispose(); _session = null;
            }
        }
    }

    private static bool TryMapFlag(string name, out SubjectTag flag)
    {
        // Case-insensitive match against SubjectTag enum names. Anything that
        // doesn't map is silently skipped so the JSON can carry extra entries
        // ahead of a future schema bump without breaking the current build.
        return Enum.TryParse(name, ignoreCase: true, out flag) && flag != SubjectTag.None;
    }

    // ── Pixel utilities ──

    private static byte[]? DecodeJpegToRgb(byte[] jpeg, out int width, out int height)
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

            // CLIP-style preprocessing expects RGB, channel-first.
            var converted = new FormatConvertedBitmap(bi, PixelFormats.Rgb24, null, 0);
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
    /// Bilinear resize. CLIP preprocessing usually center-crops to a square
    /// after resizing the shorter side, but for triage-grade classification
    /// the straight squash is good enough — the score threshold is the bigger
    /// lever, and skipping the crop keeps off-centre subjects in frame.
    /// </summary>
    private static byte[] ResizeRgb(byte[] src, int sw, int sh, int dw, int dh)
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
    /// Pack a tightly-packed RGB byte buffer into NCHW float32 with the CLIP
    /// per-channel mean / std-dev normalisation baked in. Returns a flat array
    /// laid out as [R-plane][G-plane][B-plane].
    /// </summary>
    private static float[] NormalizeToNchw(byte[] rgb, int w, int h)
    {
        int plane = w * h;
        var data = new float[3 * plane];
        for (int i = 0; i < plane; i++)
        {
            int p = i * 3;
            data[i]             = ((rgb[p]     / 255f) - PixelMean[0]) / PixelStd[0];
            data[i + plane]     = ((rgb[p + 1] / 255f) - PixelMean[1]) / PixelStd[1];
            data[i + 2 * plane] = ((rgb[p + 2] / 255f) - PixelMean[2]) / PixelStd[2];
        }
        return data;
    }

    private static void L2Normalize(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        double norm = Math.Sqrt(sumSq);
        if (norm <= 1e-9) return;
        float inv = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    private static float Dot(float[] a, float[] b)
    {
        float s = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    // ── JSON schema for subject_tags.json ──

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class SubjectTagsFile
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("embed_dim")]
        public int EmbedDim { get; set; }

        [JsonPropertyName("tags")]
        public List<TagJson> Tags { get; set; } = new();
    }

    private sealed class TagJson
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("prompts")]
        public List<string>? Prompts { get; set; }

        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
