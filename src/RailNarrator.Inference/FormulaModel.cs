using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using RailNarrator.Domain;
using SkiaSharp;

namespace RailNarrator.Inference;

/// <summary>The two files of an installed formula model.</summary>
/// <param name="ModelPath">The ONNX graph.</param>
/// <param name="ConfigPath">Its <c>inference.yml</c>, which holds the tokeniser's vocabulary.</param>
public sealed record FormulaModelFiles(string ModelPath, string ConfigPath)
{
    public bool Exist => File.Exists(ModelPath) && File.Exists(ConfigPath);
}

/// <summary>
/// PP-FormulaNet_plus-M (Baidu, Apache-2.0) in-process on ONNX Runtime, on the CPU: the image of one
/// display equation in, LaTeX out. ADR-0011.
/// </summary>
/// <remarks>
/// <para>
/// The graph is OpenDataLab's export (MinerU 4), with the autoregressive loop inside it: one run
/// takes a 384 x 384 grey image and returns the token ids, up to a forced end at 1,536 tokens. So
/// there is no per-token call from here, and no token probability to read: what can be checked is
/// that the output ended by itself, does not repeat, and is well formed. Whether it is the equation
/// on the page is checked against the text layer by the caller (<see cref="FormulaTextLayerCheck"/>).
/// </para>
/// <para>
/// The image preparation follows the model's reference pipeline step for step
/// (<see cref="FormulaImage"/>); the model was trained on its output.
/// </para>
/// <para>
/// The session is built on first use (the file is some 590 MB) and one equation runs at a time. A
/// run cannot be stopped by ONNX Runtime's own means once started except by terminating it, which
/// a cancelled token and the time limit both do.
/// </para>
/// </remarks>
public sealed class PpFormulaNetRecogniser : IFormulaRecogniser, IDisposable
{
    /// <summary>Bump when the image preparation, the decoding or the checks change: part of every cache key.</summary>
    public const string PipelineVersion = "pp-formulanet-plus-m/1";

    /// <summary>The graph forces the end of the sequence here; an output this long did not finish.</summary>
    public const int TokenLimit = 1536;

    private const long EndOfSequence = 2;

    private readonly FormulaModelFiles _files;
    private readonly int _threads;
    private readonly TimeSpan _timeLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private FormulaVocabulary? _vocabulary;
    private string? _startupError;
    private bool _disposed;

    /// <param name="threads">Intra-op threads; 0 for half the logical processors, at most 8 (more did not help the published timings).</param>
    /// <param name="timeLimit">A run longer than this is stopped and reported as unreadable; 45 s by default.</param>
    public PpFormulaNetRecogniser(FormulaModelFiles files, int threads = 0, TimeSpan? timeLimit = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _threads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        _timeLimit = timeLimit ?? TimeSpan.FromSeconds(45);
        var info = new FileInfo(files.ModelPath);
        Revision = info.Exists
            ? string.Create(CultureInfo.InvariantCulture, $"{PipelineVersion}|{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")
            : $"{PipelineVersion}|{Path.GetFileName(files.ModelPath)}";
    }

    public string Revision { get; }

    public async Task<FormulaRecognition> RecogniseAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (png.IsEmpty) return FormulaRecognition.Failed("no image was supplied");
        float[]? tensor = FormulaImage.Prepare(png.Span);
        if (tensor is null) return FormulaRecognition.Failed("the image is blank or is not an equation's shape");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Run(tensor, ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private FormulaRecognition Run(float[] tensor, CancellationToken ct)
    {
        if (_startupError is not null) return FormulaRecognition.Failed(_startupError);
        if (_session is null)
        {
            try
            {
                _vocabulary = FormulaVocabulary.Load(_files.ConfigPath);
                using var options = new SessionOptions { IntraOpNumThreads = _threads, InterOpNumThreads = 1 };
                _session = new InferenceSession(_files.ModelPath, options);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or FormatException or UnauthorizedAccessException)
            {
                // Not retried for every equation of the document.
                return FormulaRecognition.Failed(_startupError = $"the formula model could not be loaded ({ex.Message})");
            }
        }

        long[] ids;
        using var runOptions = new RunOptions();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeLimit);
        // Terminate is the one way to stop a run that is inside the graph's loop.
        using var stop = timeout.Token.Register(() => { try { runOptions.Terminate = true; } catch (ObjectDisposedException) { } });
        try
        {
            using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 1, FormulaImage.Size, FormulaImage.Size]);
            using var outputs = _session.Run(runOptions, [_session.InputNames[0]], [input], [_session.OutputNames[0]]);
            ids = outputs[0].GetTensorDataAsSpan<long>().ToArray();
        }
        catch (OnnxRuntimeException) when (timeout.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            return FormulaRecognition.Failed($"it took longer than {_timeLimit.TotalSeconds:F0} s");
        }
        catch (OnnxRuntimeException ex)
        {
            return FormulaRecognition.Failed($"the model failed ({ex.Message})");
        }

        int end = Array.IndexOf(ids, EndOfSequence);
        if (end < 0 || end >= TokenLimit)
            return FormulaRecognition.Failed($"it did not finish within its limit of {TokenLimit} tokens");
        var tokens = ids.AsSpan(0, end);
        if (FormulaOutput.RunsIntoRepetition(tokens))
            return FormulaRecognition.Failed("its output runs into a repetition");
        string latex = FormulaOutput.Clean(_vocabulary!.Decode(tokens));
        if (latex.Length == 0) return FormulaRecognition.Failed("it returned nothing");
        if (!FormulaOutput.BracesBalance(latex)) return FormulaRecognition.Failed("its output has unbalanced braces");
        return new FormulaRecognition(latex, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// The model's image preparation, as its reference pipeline does it (PaddleX's and MinerU's
/// <c>UniMERNetImgDecode</c>, <c>UniMERNetTestTransform</c> and <c>LatexImageFormat</c>): trim the
/// margins, scale the short side to 384, shrink to fit a 384 square, centre on black, normalise.
/// </summary>
/// <remarks>
/// The two scalings are Pillow's (bilinear, then <c>thumbnail</c>: a box reduction and bicubic), in
/// its 8-bit arithmetic, so the image is the reference's byte for byte: checked against Pillow
/// 11.3 and 12.0 on the display equations of the GARCH paper and on synthetic images of twenty
/// shapes. (Pillow 13 runs the vertical pass first for some sizes, which moves a few pixels by one
/// level; the model was not trained on that.) Skipping the box reduction, a plainer way to scale,
/// changed the tokens of one to five equations in eighty.
/// </remarks>
public static class FormulaImage
{
    public const int Size = 384;
    private const float Mean = 0.7931f, Std = 0.1738f;

    /// <summary>The model's input for a PNG, <c>[1, 1, 384, 384]</c> row-major, or null when there is no ink to read.</summary>
    public static float[]? Prepare(ReadOnlySpan<byte> png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;
        int w = bitmap.Width, h = bitmap.Height;
        var planes = new byte[3][];
        for (int c = 0; c < 3; c++) planes[c] = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = bitmap.GetPixel(x, y);
                // On white, as a page is: a transparent pixel is paper, not ink.
                int a = p.Alpha;
                planes[0][y * w + x] = (byte)((p.Red * a + 255 * (255 - a) + 127) / 255);
                planes[1][y * w + x] = (byte)((p.Green * a + 255 * (255 - a) + 127) / 255);
                planes[2][y * w + x] = (byte)((p.Blue * a + 255 * (255 - a) + 127) / 255);
            }
        return Prepare(planes, w, h);
    }

    /// <summary>The model's input for an image given as R, G and B planes.</summary>
    public static float[]? Prepare(byte[][] planes, int width, int height)
    {
        var canvas = Canvas(planes, width, height);
        if (canvas is null) return null;
        var tensor = new float[Size * Size];
        const float scale = 1f / 255f;
        for (int i = 0; i < tensor.Length; i++)
        {
            float r = (canvas[0][i] * scale - Mean) / Std, g = (canvas[1][i] * scale - Mean) / Std, b = (canvas[2][i] * scale - Mean) / Std;
            // The reference weights the channels in BGR order on an RGB image; kept, since that is
            // what the model saw. On a grey image the weights sum to one and the order is moot.
            tensor[i] = 0.114f * r + 0.587f * g + 0.299f * b;
        }
        return tensor;
    }

    /// <summary>
    /// The 384 x 384 image the model sees, as R, G and B planes (identical for a grey source): the
    /// trimmed equation scaled to fit and centred on black. Null when the image is blank, or so
    /// long and thin that it is a rule, not an equation.
    /// </summary>
    public static byte[][]? Canvas(byte[][] planes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(planes);
        if (width <= 0 || height <= 0) return null;
        bool grey = planes[0].AsSpan().SequenceEqual(planes[1]) && planes[0].AsSpan().SequenceEqual(planes[2]);

        // Trim the margins: Pillow's "L" conversion, stretched to 0..255, ink where below 200.
        var luma = new byte[width * height];
        int min = 255, max = 0;
        for (int i = 0; i < luma.Length; i++)
        {
            int l = (planes[0][i] * 19595 + planes[1][i] * 38470 + planes[2][i] * 7471 + 0x8000) >> 16;
            luma[i] = (byte)l;
            if (l < min) min = l;
            if (l > max) max = l;
        }
        if (max == min) return null;
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if ((luma[y * width + x] - min) / (double)(max - min) * 255 < 200)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        if (right < 0) return null;
        int w = right - left + 1, h = bottom - top + 1;
        // Scaling the short side to 384 makes the long one 384 times the aspect ratio.
        if (Math.Max(w, h) > 64L * Math.Min(w, h)) return null;

        int shortSide = Math.Min(w, h), longSide = Math.Max(w, h);
        int newLong = (int)(Size * (double)longSide / shortSide);
        int w1 = w <= h ? Size : newLong, h1 = w <= h ? newLong : Size;
        // thumbnail((384, 384)): the largest size within the square that keeps the aspect ratio.
        int w2 = w1, h2 = h1;
        if (w1 > Size || h1 > Size)
        {
            double aspect = (double)w1 / h1;
            if (1.0 >= aspect) w2 = RoundAspect(Size * aspect, n => Math.Abs(aspect - n / (double)Size));
            else h2 = RoundAspect(Size / aspect, n => n == 0 ? 0 : Math.Abs(aspect - Size / (double)n));
            if (1.0 >= aspect) h2 = Size; else w2 = Size;
        }

        var canvas = new byte[3][];
        for (int c = 0; c < (grey ? 1 : 3); c++)
        {
            var cropped = new byte[w * h];
            for (int y = 0; y < h; y++) Array.Copy(planes[c], (top + y) * width + left, cropped, y * w, w);
            var scaled = PillowResample.Resize(cropped, w, h, w1, h1, PillowResample.Filter.Bilinear);
            if (w2 != w1 || h2 != h1) scaled = PillowResample.Thumbnail(scaled, w1, h1, w2, h2);
            var plane = new byte[Size * Size];
            int padX = (Size - w2) / 2, padY = (Size - h2) / 2;
            for (int y = 0; y < h2; y++) Array.Copy(scaled, y * w2, plane, (padY + y) * Size + padX, w2);
            canvas[c] = plane;
        }
        if (grey) canvas[1] = canvas[2] = canvas[0];
        return canvas;
    }

    // Pillow: max(min(floor(number), ceil(number), key=key), 1); min keeps the first of two equal keys.
    private static int RoundAspect(double number, Func<int, double> key)
    {
        int lo = (int)Math.Floor(number), hi = (int)Math.Ceiling(number);
        return Math.Max(key(lo) <= key(hi) ? lo : hi, 1);
    }
}

/// <summary>
/// Pillow's image resampling for one 8-bit plane: the convolution resize of <c>Image.resize</c> and
/// the box reduction <c>Image.thumbnail</c> does first (<c>reducing_gap=2.0</c>), in Pillow's own
/// fixed-point arithmetic so the bytes are the same.
/// </summary>
public static class PillowResample
{
    public enum Filter { Bilinear, Bicubic }

    private const int PrecisionBits = 32 - 8 - 2;

    /// <summary><c>Image.resize((outWidth, outHeight), resample)</c>.</summary>
    public static byte[] Resize(byte[] source, int width, int height, int outWidth, int outHeight, Filter filter)
        => Resize(source, width, height, outWidth, outHeight, filter, 0, 0, width, height);

    /// <summary><c>Image.thumbnail</c>'s scaling to a size already chosen: <c>resize(size, BICUBIC, reducing_gap=2.0)</c>.</summary>
    public static byte[] Thumbnail(byte[] source, int width, int height, int outWidth, int outHeight)
    {
        int factorX = Math.Max(1, (int)((double)width / outWidth / 2.0));
        int factorY = Math.Max(1, (int)((double)height / outHeight / 2.0));
        if (factorX == 1 && factorY == 1)
            return Resize(source, width, height, outWidth, outHeight, Filter.Bicubic);
        // The safe box of the whole image is the whole image, so the reduction starts at the origin.
        var reduced = Reduce(source, width, height, factorX, factorY, out int reducedWidth, out int reducedHeight);
        return Resize(reduced, reducedWidth, reducedHeight, outWidth, outHeight, Filter.Bicubic,
            0, 0, (float)((double)width / factorX), (float)((double)height / factorY));
    }

    /// <summary><c>Image.reduce((factorX, factorY))</c>: each output pixel is the mean of a block, in Pillow's rounding.</summary>
    public static byte[] Reduce(byte[] source, int width, int height, int factorX, int factorY, out int outWidth, out int outHeight)
    {
        int stride = outWidth = (width + factorX - 1) / factorX;
        outHeight = (height + factorY - 1) / factorY;
        var output = new byte[outWidth * outHeight];
        int fullX = width / factorX, fullY = height / factorY, restX = width % factorX, restY = height % factorY;

        Block(0, fullX, 0, fullY, factorX, factorY);
        // The partial blocks at the right and bottom edges are means over the pixels there are.
        if (restX > 0) Block(fullX, fullX + 1, 0, fullY, restX, factorY);
        if (restY > 0) Block(0, fullX, fullY, fullY + 1, factorX, restY);
        if (restX > 0 && restY > 0) Block(fullX, fullX + 1, fullY, fullY + 1, restX, restY);
        return output;

        void Block(int x0, int x1, int y0, int y1, int blockW, int blockH)
        {
            // Pillow's rounding: (sum + area / 2) * floor(2^24 / area) >> 24, whichever of its
            // specialised loops runs (checked against Pillow for every block up to 9 x 9).
            int area = blockW * blockH;
            uint amend = (uint)(area / 2), multiplier = (uint)((1 << 24) / area);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    uint sum = amend;
                    for (int yy = y * factorY; yy < y * factorY + blockH; yy++)
                        for (int xx = x * factorX; xx < x * factorX + blockW; xx++)
                            sum += source[yy * width + xx];
                    output[y * stride + x] = (byte)((sum * multiplier) >> 24);
                }
        }
    }

    // The box is single precision because Pillow's is (a C float[4]); a double here moves a
    // coefficient by one unit often enough to change a pixel in every other image.
    private static byte[] Resize(byte[] source, int width, int height, int outWidth, int outHeight, Filter filter,
        float boxLeft, float boxTop, float boxRight, float boxBottom)
    {
        bool horizontal = outWidth != width || boxLeft != 0 || boxRight != outWidth;
        bool vertical = outHeight != height || boxTop != 0 || boxBottom != outHeight;
        byte[] current = source;
        int currentWidth = width;
        if (horizontal)
        {
            var (bounds, coefficients, size) = Coefficients(width, boxLeft, boxRight, outWidth, filter);
            var pass = new byte[outWidth * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < outWidth; x++)
                {
                    int first = bounds[x * 2], count = bounds[x * 2 + 1];
                    int sum = 1 << (PrecisionBits - 1);
                    for (int k = 0; k < count; k++) sum += current[y * width + first + k] * coefficients[x * size + k];
                    pass[y * outWidth + x] = Clip(sum);
                }
            current = pass;
            currentWidth = outWidth;
        }
        if (vertical)
        {
            var (bounds, coefficients, size) = Coefficients(height, boxTop, boxBottom, outHeight, filter);
            var pass = new byte[currentWidth * outHeight];
            for (int y = 0; y < outHeight; y++)
            {
                int first = bounds[y * 2], count = bounds[y * 2 + 1];
                for (int x = 0; x < currentWidth; x++)
                {
                    int sum = 1 << (PrecisionBits - 1);
                    for (int k = 0; k < count; k++) sum += current[(first + k) * currentWidth + x] * coefficients[y * size + k];
                    pass[y * currentWidth + x] = Clip(sum);
                }
            }
            current = pass;
        }
        return ReferenceEquals(current, source) ? (byte[])source.Clone() : current;
    }

    private static byte Clip(int sum) => (byte)Math.Clamp(sum >> PrecisionBits, 0, 255);

    private static (int[] Bounds, int[] Coefficients, int KernelSize) Coefficients(int inSize, float in0, float in1, int outSize, Filter filter)
    {
        double scale = (double)(in1 - in0) / outSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = (filter == Filter.Bilinear ? 1.0 : 2.0) * filterScale;
        int kernelSize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var coefficients = new int[outSize * kernelSize];
        var weights = new double[kernelSize];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = in0 + (xx + 0.5) * scale;
            double total = 0, ss = 1.0 / filterScale;
            int first = Math.Max(0, (int)(center - support + 0.5));
            int count = Math.Min(inSize, (int)(center + support + 0.5)) - first;
            for (int x = 0; x < count; x++)
            {
                double weight = Weight(filter, (x + first - center + 0.5) * ss);
                weights[x] = weight;
                total += weight;
            }
            for (int x = 0; x < count; x++)
            {
                double weight = total != 0 ? weights[x] / total : weights[x];
                coefficients[xx * kernelSize + x] = (int)(weight < 0 ? -0.5 + weight * (1 << PrecisionBits) : 0.5 + weight * (1 << PrecisionBits));
            }
            bounds[xx * 2] = first;
            bounds[xx * 2 + 1] = Math.Max(0, count);
        }
        return (bounds, coefficients, kernelSize);
    }

    private static double Weight(Filter filter, double x)
    {
        if (x < 0) x = -x;
        if (filter == Filter.Bilinear) return x < 1.0 ? 1.0 - x : 0.0;
        const double a = -0.5;
        if (x < 1.0) return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0) return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }
}

/// <summary>
/// The model's vocabulary, read from its <c>inference.yml</c>: token ids to text. Decoding only, so
/// the merge rules are not needed.
/// </summary>
/// <remarks>
/// The file is PaddleX's dump of a Hugging Face byte-level BPE tokeniser (Nougat's, 50,000 tokens)
/// as YAML. Only the two blocks needed are read, <c>model: vocab:</c> and <c>added_tokens:</c>, by
/// their indentation; a line that is not what the reader expects is an error, not a guess, because
/// a vocabulary that is one entry out reads every equation as nonsense.
/// </remarks>
public sealed partial class FormulaVocabulary
{
    private readonly string[] _tokens;
    private readonly HashSet<long> _special;

    // GPT-2's byte-to-character table: the bytes that are not printable get characters from U+0100 on.
    private static readonly Dictionary<char, byte> ByteOf = BuildByteTable();

    private FormulaVocabulary(string[] tokens, HashSet<long> special)
    {
        _tokens = tokens;
        _special = special;
    }

    public int Count => _tokens.Length;

    /// <summary>The token's text as the file has it (byte-level characters, not yet text).</summary>
    public string Token(int id) => _tokens[id];

    public static FormulaVocabulary Load(string configPath)
    {
        using var reader = new StreamReader(configPath, Encoding.UTF8);
        return Read(reader);
    }

    public static FormulaVocabulary Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var tokens = new Dictionary<int, string>();
        var special = new HashSet<long>();
        string? line;
        string block = "";
        string? content = null;
        int id = -1;
        bool isSpecial = false;

        void EndAddedToken()
        {
            if (content is not null && id >= 0 && isSpecial) special.Add(id);
            content = null; id = -1; isSpecial = false;
        }

        while ((line = reader.ReadLine()) is not null)
        {
            int indent = line.Length - line.TrimStart(' ').Length;
            if (block == "vocab")
            {
                if (indent >= 10)
                {
                    var m = VocabularyLine().Match(line);
                    if (!m.Success) throw new FormatException($"The formula model's vocabulary has a line this reader does not understand: '{Shorten(line)}'.");
                    tokens[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)] = Scalar(m.Groups[1].Value);
                    continue;
                }
                block = "";
            }
            else if (block == "added")
            {
                if (indent >= 6 && line.AsSpan(indent).StartsWith("- "))
                {
                    EndAddedToken();
                    string first = line[(indent + 2)..];
                    if (first.StartsWith("content: ", StringComparison.Ordinal)) content = Scalar(first["content: ".Length..]);
                    continue;
                }
                if (indent >= 8)
                {
                    string field = line[indent..];
                    if (field.StartsWith("content: ", StringComparison.Ordinal)) content = Scalar(field["content: ".Length..]);
                    else if (field.StartsWith("id: ", StringComparison.Ordinal)) id = int.Parse(field["id: ".Length..], CultureInfo.InvariantCulture);
                    else if (field.StartsWith("special: ", StringComparison.Ordinal)) isSpecial = field.EndsWith("true", StringComparison.Ordinal);
                    continue;
                }
                EndAddedToken();
                block = "";
            }

            string trimmed = line.TrimEnd();
            if (trimmed == "        vocab:") block = "vocab";
            else if (trimmed == "      added_tokens:" && special.Count == 0) block = "added";
        }
        EndAddedToken();

        if (tokens.Count < 1000)
            throw new FormatException("The file holds no tokeniser vocabulary (a 'vocab:' block under 'model:').");
        var table = new string[tokens.Count];
        for (int i = 0; i < table.Length; i++)
            table[i] = tokens.TryGetValue(i, out string? token) ? token
                : throw new FormatException($"The formula model's vocabulary has no token {i} of {tokens.Count}.");
        if (special.Count == 0) special = [0, 1, 2, 3];
        return new FormulaVocabulary(table, special);
    }

    /// <summary>The text of a token sequence: special tokens dropped, byte-level characters back to UTF-8.</summary>
    public string Decode(ReadOnlySpan<long> ids)
    {
        var bytes = new List<byte>(ids.Length * 3);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (long id in ids)
        {
            if (id < 0 || id >= _tokens.Length || _special.Contains(id)) continue;
            foreach (var rune in _tokens[id].EnumerateRunes())
            {
                if (rune.IsBmp && ByteOf.TryGetValue((char)rune.Value, out byte b)) bytes.Add(b);
                else bytes.AddRange(utf8[..rune.EncodeToUtf8(utf8)]);   // an added token written as plain text
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static Dictionary<char, byte> BuildByteTable()
    {
        var table = new Dictionary<char, byte>(256);
        int extra = 0;
        for (int b = 0; b < 256; b++)
        {
            bool printable = b is >= 33 and <= 126 or >= 161 and <= 172 or >= 174;
            table[printable ? (char)b : (char)(256 + extra++)] = (byte)b;
        }
        return table;
    }

    /// <summary>A YAML scalar as PyYAML writes one on a single line: plain, single-quoted or double-quoted.</summary>
    internal static string Scalar(string text)
    {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"') return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 1; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= text.Length - 1) throw new FormatException($"A quoted token ends in a backslash: {Shorten(text)}.");
            char e = text[i];
            int digits = e switch { 'x' => 2, 'u' => 4, 'U' => 8, _ => 0 };
            if (digits > 0)
            {
                if (i + digits >= text.Length) throw new FormatException($"A quoted token has a short escape: {Shorten(text)}.");
                sb.Append(char.ConvertFromUtf32(int.Parse(text.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                i += digits;
                continue;
            }
            sb.Append(e switch
            {
                '0' => '\0', 'a' => '\a', 'b' => '\b', 't' or '\t' => '\t', 'n' => '\n', 'v' => '\v', 'f' => '\f', 'r' => '\r',
                'e' => '\u001B', ' ' => ' ', '"' => '"', '/' => '/', '\\' => '\\', 'N' => '\u0085', '_' => '\u00A0',
                'L' => '\u2028', 'P' => '\u2029',
                _ => throw new FormatException($"A quoted token has an escape this reader does not know: \\{e}."),
            });
        }
        return sb.ToString();
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;

    // "          <token>: <id>": the id is the digits after the last ": ", whatever the token holds.
    [GeneratedRegex(@"^ {10}(.+): (\d+)$")]
    private static partial Regex VocabularyLine();
}

/// <summary>What is done to the model's output before it is called LaTeX, and the checks that it is one equation's worth.</summary>
public static partial class FormulaOutput
{
    /// <summary>
    /// The decoded text tidied: the upright-Greek commands of one training source as the ordinary
    /// ones (<c>\upalpha</c> is alpha) and the layout commands that say nothing dropped, as the
    /// reference post-processing does.
    /// </summary>
    public static string Clean(string decoded)
    {
        string s = Whitespace().Replace(decoded ?? "", " ").Trim();
        s = Upright().Replace(s, m => m.Groups[1].Value is "arrow" or "downarrow" or "lus" or "silon" ? m.Value : "\\" + m.Groups[1].Value);
        s = Unspoken().Replace(s, "");
        return Whitespace().Replace(s, " ").Trim();
    }

    /// <summary>Every unescaped brace closes and none closes unopened.</summary>
    public static bool BracesBalance(string latex)
    {
        int depth = 0;
        for (int i = 0; i < latex.Length; i++)
        {
            if (latex[i] == '\\') { i++; continue; }
            if (latex[i] == '{') depth++;
            else if (latex[i] == '}' && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>
    /// The output ends in the same few tokens over and over: a decoder that lost its place. A long
    /// equation repeats shapes, never the same tokens exactly for this long.
    /// </summary>
    public static bool RunsIntoRepetition(ReadOnlySpan<long> tokens)
    {
        for (int period = 1; period <= 24; period++)
        {
            int needed = Math.Max(48, period * 6), run = 0;
            for (int i = tokens.Length - 1; i >= period && tokens[i] == tokens[i - period]; i--) run++;
            if (run >= needed) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\\up([a-zA-Z]+)")]
    private static partial Regex Upright();

    [GeneratedRegex(@"\\(?:lefteqn|boldmath|ensuremath|centering|textsubscript|sides|textsl|textcent|emph|protect|null)(?![a-zA-Z])")]
    private static partial Regex Unspoken();
}

/// <summary>
/// Reads display equations by rule from a formula model's transcription
/// (<see cref="MathInterpretationRequest.Formulas"/>): no model call. Null unless every equation was
/// transcribed, agrees with the page's text about its Greek letters, and can be spoken without
/// notation left over.
/// </summary>
public sealed class FormulaMathReader : IMathReader
{
    public string Revision => $"formula-read/1|{LocalLatexSpeaker.Version}";

    public Task<string?> ReadAsync(MathInterpretationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Read(request));
    }

    internal static string? Read(MathInterpretationRequest request)
    {
        var formulas = request.Formulas;
        // A transcription that names letters the page does not is not read aloud unarbitrated.
        if (formulas.Count == 0 || formulas.Any(f => f.Disagrees)) return null;
        var equations = request with { Kind = MathPassageKind.DisplayEquation };
        var spoken = formulas.Select(f => MathTranscriptionReading.Spoken(f.Latex, equations with { TextLayer = f.TextLayer })).ToList();
        return spoken.Any(s => s is null) ? null : string.Join(" ", spoken);
    }
}

/// <summary>
/// The formula model as the transcriber of the two-stage maths path (the modes that read an equation
/// faithfully, not <see cref="MathMode.Interpret"/>): a display equation's crop to LaTeX with no
/// vision endpoint.
/// </summary>
public sealed class FormulaModelTranscriber : IMathTranscriber
{
    private readonly IFormulaRecogniser _formulas;

    public FormulaModelTranscriber(IFormulaRecogniser formulas) => _formulas = formulas ?? throw new ArgumentNullException(nameof(formulas));

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<MathTranscriptionResult> TranscribeAsync(MathTranscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var read = await _formulas.RecogniseAsync(request.PngBytes, ct).ConfigureAwait(false);
        if (!read.Succeeded)
            return new MathTranscriptionResult(null, null, [], $"The formula model could not read the equation ({read.Problem}).", null);
        var formula = FormulaTextLayerCheck.Reconcile(read.Latex!, request.SourceTextHint ?? "");
        if (formula.Disagrees)
            return new MathTranscriptionResult(null, null, formula.Notes, "The formula model's transcription disagrees with the page's text. " + string.Join(" ", formula.Notes), null);
        var warnings = new List<string> { "Transcribed by a formula model and unverified. Check it against the original before trusting the narration." };
        warnings.AddRange(formula.Notes);
        return new MathTranscriptionResult(formula.Latex, null, warnings, null, request.AnchorIds.Count > 0 ? string.Join(",", request.AnchorIds) : null);
    }
}
