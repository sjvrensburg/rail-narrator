using System.Security.Cryptography;
using System.Text;

namespace RailNarrator.Domain;

/// <summary>
/// Deterministic cache keys for each stage of the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Every stage is cached separately, and each key folds in exactly the inputs that can change that
/// stage's output. The point is invalidation without a manual cache clear: edit an equation's
/// transcription and only the affected verbalisation, speech and timings are rebuilt; change the
/// voice and only speech is; re-OCR the document and everything downstream of extraction goes.
/// </para>
/// <para>
/// Audio and its timings share one key and are always written and evicted together. Splitting them
/// is how a clip ends up paired with another clip's word boundaries — a highlight that is
/// confidently, smoothly wrong.
/// </para>
/// </remarks>
public static class CacheKey
{
    private const string Scheme = "rn1";

    /// <summary>SHA-256 of a byte sequence, lowercase hex.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>SHA-256 of a UTF-8 string, lowercase hex.</summary>
    public static string Sha256Hex(string s) => Sha256Hex(Encoding.UTF8.GetBytes(s ?? ""));

    /// <summary>SHA-256 of a file's bytes. Read in chunks so a large PDF is not buffered whole.</summary>
    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0) sha.AppendData(buffer.AsSpan(0, read));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>An extraction snapshot's identity: the bytes plus everything that shapes the extraction.</summary>
    public static string ForExtraction(string documentContentHash, ExtractionOptions options, NarrationFilterSettings filters)
        => Compose("extraction", documentContentHash,
            options.AnalyzerId, ((int)options.Ocr).ToString(), options.AnalysisRenderSize.ToString(),
            options.DeskewLines.ToString(), options.ExtractorVersion, FilterBits(filters));

    /// <summary>A maths transcription: the crop, its source anchor, and the prompt/model that read it.</summary>
    public static string ForMathTranscription(
        string documentContentHash, ExtractionIdentity extraction, string anchorId, string cropHash,
        string promptVersion, string modelRevision)
        => Compose("math-tx", documentContentHash, extraction.DocumentContentHash, extraction.ExtractionRevision.ToString(),
            anchorId, cropHash, promptVersion, modelRevision);

    /// <summary>
    /// A verbalisation: the recognised expression, the mode, the bounded context, and the
    /// prompt/model revisions. Context is hashed rather than inlined so a key stays short while
    /// still changing when the surrounding text does.
    /// </summary>
    public static string ForMathVerbalisation(
        string documentContentHash, string anchorId, string representation, MathMode mode,
        string contextBefore, string contextAfter, string language, string promptVersion, string modelRevision)
        => Compose("math-vb", documentContentHash, anchorId, Sha256Hex(representation), ((int)mode).ToString(),
            Sha256Hex(contextBefore + "\u241F" + contextAfter), language, promptVersion, modelRevision);

    /// <summary>
    /// A speech asset. The script revision and a hash of the exact spoken text are both included:
    /// the revision catches an edit counter incrementing, and the hash catches text that changed
    /// without the revision being bumped.
    /// </summary>
    public static string ForSpeechAsset(
        string segmentId, int scriptRevision, string spokenText, string language, string providerId,
        string modelRevision, string voiceId, string synthesisOptions, AudioFormat format)
        => Compose("speech", segmentId, scriptRevision.ToString(), Sha256Hex(spokenText), language,
            providerId, modelRevision, voiceId, synthesisOptions,
            format.SampleRate.ToString(), format.Channels.ToString(), ((int)format.SampleFormat).ToString());

    /// <summary>
    /// Alignment for an asset. Keyed on the asset id, which already folds in the script, voice,
    /// provider and model — so re-synthesising a clip can never leave it wearing another clip's
    /// timings.
    /// </summary>
    public static string ForAlignment(string assetId, string alignerId, string alignerRevision, string scriptHash)
        => Compose("align", assetId, alignerId, alignerRevision, scriptHash);

    /// <summary>A user's pronunciation override for one word or phrase.</summary>
    public static string ForPronunciation(string language, string surfaceForm)
        => Compose("pron", language, Sha256Hex(surfaceForm.ToLowerInvariant()));

    private static string FilterBits(NarrationFilterSettings f) => string.Join("",
        f.ExcludeRunningHeaders ? "H" : "-", f.ExcludeFooters ? "F" : "-", f.ExcludePageNumbers ? "N" : "-",
        f.IncludeCitations ? "c" : "-", f.IncludeFootnotes ? "f" : "-", f.IncludeCaptions ? "p" : "-",
        f.IncludeReferences ? "r" : "-", f.IncludeEquations ? "e" : "-", f.IncludeTables ? "t" : "-");

    private static string Compose(params string[] parts)
        => $"{Scheme}:{Sha256Hex(string.Join("\u241E", parts))}";
}
