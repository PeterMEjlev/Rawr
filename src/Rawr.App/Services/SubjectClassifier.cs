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
/// L2-normalise it, and take the cosine similarity against every tag's
/// (already L2-normalised) text embedding. Any tag whose similarity exceeds
/// <see cref="AppSettings.SubjectTagThreshold"/> (scaled 0–1) gets applied.
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

    private readonly object _initLock = new();
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private int _inputH = FallbackInputSize;
    private int _inputW = FallbackInputSize;
    private int _embedDim;
    private TagEntry[] _tags = Array.Empty<TagEntry>();
    private volatile bool _initAttempted;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    /// <summary>Trigger model load (cheap if already loaded).</summary>
    public void Initialize() => EnsureInitialized();

    /// <summary>
    /// Classify a JPEG (typically the cached thumbnail or preview) and return
    /// the set of tags whose similarity score met <paramref name="threshold"/>.
    /// Returns null if the classifier isn't available or the JPEG can't be
    /// decoded. Returning <see cref="SubjectTag.None"/> means classification
    /// ran successfully and nothing scored high enough — distinct from a null
    /// "not classified yet" result that the persistence layer leans on.
    /// </summary>
    public SubjectTag? Classify(byte[] jpeg, float threshold)
    {
        EnsureInitialized();
        if (!IsAvailable) return null;

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
        if (embedding.Length < _embedDim) return SubjectTag.None;
        var imageEmbed = new float[_embedDim];
        Array.Copy(embedding, embedding.Length - _embedDim, imageEmbed, 0, _embedDim);
        L2Normalize(imageEmbed);

        SubjectTag result = SubjectTag.None;
        foreach (var tag in _tags)
        {
            float score = Dot(imageEmbed, tag.Embedding);
            if (score >= threshold) result |= tag.Flag;
        }
        // Hybrid grouping: a leaf hit (e.g. Dog) implies its group (Animal) even
        // when the group's own embedding didn't clear the threshold, so a group
        // bit is always a superset of its leaves.
        return SubjectTaxonomy.ApplyGroupRollup(result);
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

                var entries = new List<TagEntry>(parsed.Tags.Count);
                foreach (var t in parsed.Tags)
                {
                    if (string.IsNullOrEmpty(t.Name)) continue;
                    if (t.Embedding == null || t.Embedding.Length != _embedDim) continue;
                    if (!TryMapFlag(t.Name, out var flag)) continue;
                    var copy = (float[])t.Embedding.Clone();
                    L2Normalize(copy);
                    entries.Add(new TagEntry(flag, copy));
                }
                if (entries.Count == 0)
                {
                    UnavailableReason = $"{TagsFile}: no usable tag entries (names must match SubjectTag values).";
                    _session.Dispose(); _session = null;
                    return;
                }
                _tags = entries.ToArray();

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

    private readonly record struct TagEntry(SubjectTag Flag, float[] Embedding);

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
