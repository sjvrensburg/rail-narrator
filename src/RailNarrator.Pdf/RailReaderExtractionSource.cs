using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailNarrator.Domain;
using SkiaSharp;
using CoreRectF = RailReader.Core.Models.RectF;

namespace RailNarrator.Pdf;

/// <summary>
/// The layout/OCR backend an extraction runs on.
/// </summary>
/// <param name="AnalyzerId">Stable identifier, folded into the extraction cache key.</param>
/// <param name="AnalyzerFactory">Creates one analyzer per extraction. Analyzers hold native sessions and are not thread-safe.</param>
/// <param name="Capabilities">The analyzer's self-description, used to decide whether a reading-order resolver is needed.</param>
/// <param name="ResolverFactory">Reading-order resolver, or null to let Core pick from <paramref name="Capabilities"/>.</param>
/// <param name="OcrFactory">Creates an OCR engine, or null when recognition is unavailable.</param>
/// <remarks>
/// Kept as a value the caller supplies rather than a hard-wired choice because the two realistic
/// configurations behave very differently: a model-free analyzer is instant and needs no download
/// but reports every block as plain text, so role-keyed features are inert; an ONNX model supplies
/// semantic roles including display maths but needs ~50 MB of weights on disk. The extraction must
/// work either way and must say which one it used.
/// </remarks>
public sealed record ExtractionBackend(
    string AnalyzerId,
    Func<ILayoutAnalyzer> AnalyzerFactory,
    LayoutModelCapabilities Capabilities,
    Func<IReadingOrderResolver?>? ResolverFactory = null,
    Func<IOcrService>? OcrFactory = null)
{
    /// <summary>
    /// Core's model-free text-layer analyzer. No ONNX runtime, no weights, no native dependency, so
    /// tests and a first run work with nothing downloaded. Every block comes back as plain text, so
    /// running-header suppression and display-maths detection fall back to geometry and glyph cues.
    /// </summary>
    public static ExtractionBackend ModelFree(Func<IOcrService>? ocrFactory = null)
    {
        var probe = new TextLayoutAnalyzer();
        var caps = probe.Capabilities;
        probe.Dispose();
        return new ExtractionBackend("text-layout", () => new TextLayoutAnalyzer(), caps,
            () => new XYCutPlusPlusResolver(), ocrFactory);
    }

    /// <summary>PP-DocLayoutV3 via RailReader.Core.Analysis. Needs the model on disk.</summary>
    public static ExtractionBackend OnnxLayout(string modelPath, Func<IOcrService>? ocrFactory = null)
    {
        var analyzer = new LayoutAnalyzer(modelPath);
        var caps = analyzer.Capabilities;
        analyzer.Dispose();
        return new ExtractionBackend(
            $"pp-doclayout-v3:{Path.GetFileName(modelPath)}",
            () => new LayoutAnalyzer(modelPath),
            caps,
            // V3 provides its own reading order; XY-cut would discard a signal the model was trained
            // to produce. Core's AnalysisWorker makes the same choice.
            () => caps.ProvidesReadingOrder ? new ModelOrderResolver() : new XYCutPlusPlusResolver(),
            ocrFactory);
    }
}

/// <summary>
/// Extracts narration-ready snapshots from a PDF using RailReaderCore's own pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Runs Core's analyzer, reading-order resolver, orientation detection and line detection directly
/// rather than through <c>DocumentController</c>'s animation loop. Those entry points are public and
/// pure, so extraction becomes a deterministic function of the document and the options — the same
/// bytes and the same settings always give the same snapshot, which is what makes cached plans and
/// cached audio safe to reuse and what makes the geometry tests reproducible.
/// </para>
/// <para>
/// Everything here runs off the UI thread. PDF analysis, OCR and inference are the slow parts of the
/// pipeline and blocking the render loop on them is what makes a reader feel broken.
/// </para>
/// <para>
/// Coordinates leave Core in displayed page points (top-left origin, Y down, page <c>/Rotate</c> and
/// any view rotation already composed) and are stamped with a <see cref="CoordinateFrameId"/> saying
/// so. They are never treated as raw PDF user-space coordinates, which have the opposite Y direction.
/// </para>
/// </remarks>
public sealed class RailReaderExtractionSource : IExtractionSource, IDisposable
{
    private readonly IPdfServiceFactory _pdfFactory;
    private readonly IPdfTextService _textService;
    private bool _disposed;

    public RailReaderExtractionSource(IPdfServiceFactory? pdfFactory = null)
    {
        _pdfFactory = pdfFactory ?? new SkiaPdfServiceFactory();
        _textService = _pdfFactory.CreatePdfTextService();
    }

    public async Task<ExtractionSnapshot> ExtractAsync(
        ExtractionRequest request,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(request.FilePath))
            throw new FileNotFoundException("The PDF to narrate was not found.", request.FilePath);

        // Extraction is CPU-bound and synchronous underneath, so the whole pass runs on the pool
        // rather than blocking the caller.
        return await Task.Run(() => Extract(request, progress, ct), ct).ConfigureAwait(false);
    }

    private ExtractionSnapshot Extract(ExtractionRequest request, IProgress<ExtractionProgress>? progress, CancellationToken ct)
    {
        var backend = _backend ?? ExtractionBackend.ModelFree();

        string contentHash = CacheKey.Sha256HexOfFile(request.FilePath);
        int revision = request.Revision > 0 ? request.Revision : 1;
        var identity = new ExtractionIdentity(contentHash, revision);

        IPdfService pdf = _pdfFactory.CreatePdfService(request.FilePath, request.Password);
        try
        {
            int viewRotation = request.ViewRotation;
            var frame = CoordinateFrameId.DisplayedPagePoints(0, viewRotation);
            var pages = new List<PageExtraction>();
            var requested = request.Pages ?? Enumerable.Range(0, pdf.PageCount).ToList();
            int done = 0;

            using var analyzer = backend.AnalyzerFactory();
            var resolver = backend.ResolverFactory?.Invoke();
            IOcrService? ocr = null;
            try
            {
                if (request.Options.Ocr != OcrPolicy.Off && backend.OcrFactory is { } ocrFactory)
                {
                    try { ocr = ocrFactory(); }
                    catch (Exception ex)
                    {
                        // A missing OCR model must not stop a native-text document from being read.
                        _ocrError = $"OCR is unavailable: {ex.Message}";
                    }
                }

                foreach (int pageIndex in requested)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pageIndex < 0 || pageIndex >= pdf.PageCount) continue;
                    pages.Add(ExtractPage(pdf, pageIndex, viewRotation, request, backend, analyzer, resolver, ocr, frame));
                    progress?.Report(new ExtractionProgress(++done, requested.Count,
                        pageIndex == requested[0] ? "Analysing layout" : "Extracting text and geometry"));
                }
            }
            finally
            {
                ocr?.Dispose();
            }

            return new ExtractionSnapshot(identity, frame, request.Options, pages);
        }
        finally
        {
            (pdf as IDisposable)?.Dispose();
        }
    }

    private string? _ocrError;
    private ExtractionBackend? _backend;

    /// <summary>Sets the backend used when <see cref="ExtractionRequest.Options"/> does not name one.</summary>
    public RailReaderExtractionSource WithBackend(ExtractionBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        return this;
    }

    /// <summary>The reason OCR could not be initialised, or null. Surfaced so a scan is not silently unreadable.</summary>
    public string? OcrStartupError => _ocrError;

    private PageExtraction ExtractPage(
        IPdfService pdf, int pageIndex, int viewRotation, ExtractionRequest request,
        ExtractionBackend backend, ILayoutAnalyzer analyzer, IReadingOrderResolver? resolver,
        IOcrService? ocr, CoordinateFrameId frame)
    {
        var (pageW, pageH) = pdf.GetPageSize(pageIndex, viewRotation);
        var warnings = new List<string>();

        // --- text layer ---------------------------------------------------------------
        PageText native = _textService.ExtractPageText(pdf.PdfBytes, pageIndex, viewRotation, request.Password);
        bool nativeUsable = CountMeaningfulChars(native) >= 8;

        PageText text = native;
        bool ocrUsed = false;
        var ocrLineBoxes = new List<BBox>();

        if (!nativeUsable)
        {
            if (ocr is not null && request.Options.Ocr == OcrPolicy.Full)
            {
                var mode = request.Options.Ocr == OcrPolicy.LinesOnly ? OcrMode.Lines : OcrMode.Full;
                var ocrPage = OcrRecognizer.RecognizePage(pdf, ocr, pageIndex, mode, OcrDpiFor(request.Options), ct: default);
                if (ocrPage.HasText)
                {
                    text = BuildPageTextFromOcr(ocrPage);
                    ocrUsed = true;
                    ocrLineBoxes = ocrPage.Lines.Select(l => l.Box).ToList();
                }
                else
                {
                    warnings.Add("This page has no readable text layer and recognition found nothing. It will be skipped rather than narrated as empty.");
                }
            }
            else if (request.Options.Ocr != OcrPolicy.Off && ocr is null)
            {
                warnings.Add(_ocrError ?? "This page has no text layer and no OCR engine is available, so it cannot be narrated.");
            }
            else
            {
                warnings.Add("This page has no usable text layer and recognition is turned off.");
            }
        }
        else if (CoverageLooksSparse(native, pageW, pageH) && ocr is not null && request.Options.Ocr == OcrPolicy.Full)
        {
            // A page can carry a few real characters and still be a scan. Sparse native text must not
            // suppress the recognition the page needs.
            warnings.Add("The text layer on this page is sparse; recognition was run as well. Check the result before trusting it.");
            var ocrPage = OcrRecognizer.RecognizePage(pdf, ocr, pageIndex, OcrMode.Full, OcrDpiFor(request.Options));
            if (ocrPage.HasText)
            {
                var ocrText = BuildPageTextFromOcr(ocrPage);
                if (CountMeaningfulChars(ocrText) > CountMeaningfulChars(native))
                {
                    text = ocrText;
                    ocrUsed = true;
                    ocrLineBoxes = ocrPage.Lines.Select(l => l.Box).ToList();
                }
            }
        }

        // --- layout ---------------------------------------------------------------------
        int targetSize = (int)Math.Clamp(Math.Round(Math.Max(pageW, pageH) / 72.0 * AnalysisDpiFor(request.Options)), 1, 8192);
        var (rgb, pxW, pxH) = pdf.RenderPagePixmap(pageIndex, targetSize, viewRotation);
        float scaleX = pxW > 0 ? (float)(pageW / pxW) : 1f;
        float scaleY = pxH > 0 ? (float)(pageH / pxH) : 1f;

        PageAnalysis analysis;
        try
        {
            // A layout model takes the page at exactly its input size (max side == InputSize) and
            // rejects anything larger rather than crop it. Rendering it the high-resolution raster
            // made every page fail, so a configured model silently produced no blocks at all. The
            // high-resolution raster is still what line detection below works on.
            if (backend.Capabilities.Classes.Count > 0 && backend.Capabilities.InputSize > 0
                && Math.Max(pxW, pxH) != backend.Capabilities.InputSize)
            {
                var (modelRgb, modelW, modelH) = pdf.RenderPagePixmap(pageIndex, backend.Capabilities.InputSize, viewRotation);
                analysis = analyzer.RunAnalysis(modelRgb, modelW, modelH, pageW, pageH, text.CharBoxes);
            }
            else analysis = analyzer.RunAnalysis(rgb, pxW, pxH, pageW, pageH, text.CharBoxes);
        }
        catch (Exception ex)
        {
            warnings.Add($"Layout analysis failed ({ex.GetType().Name}: {ex.Message}); this page may not be narrated correctly.");
            analysis = new PageAnalysis { PageWidth = pageW, PageHeight = pageH };
        }

        if (!backend.Capabilities.ProvidesReadingOrder && resolver is not null)
            resolver.AssignOrder(analysis.Blocks, pageW, pageH, text.CharBoxes);

        // Orientation and per-block line detection. Without this pass LayoutBlock.Lines is empty and
        // there is nothing to anchor a highlight to.
        BlockPostProcessor.PostProcess(
            analysis.Blocks, rgb, pxW, pxH, scaleX, scaleY, text.CharBoxes,
            tableRowReading: true, cellNavigation: false,
            ocrLines: ocrLineBoxes.Count > 0 ? ocrLineBoxes : null);

        return BuildPageExtraction(pageIndex, pageW, pageH, text, analysis, ocrUsed, warnings);
    }

    private static int OcrDpiFor(ExtractionOptions options) => options.AnalysisRenderSize > 0 ? Math.Max(150, options.AnalysisRenderSize * 300 / 1024) : 300;

    private static int AnalysisDpiFor(ExtractionOptions options) => options.AnalysisRenderSize > 0 ? options.AnalysisRenderSize : 1024;

    private static int CountMeaningfulChars(PageText text)
    {
        if (text.CharBoxes is null) return 0;
        int n = 0;
        foreach (var cb in text.CharBoxes)
            if (cb.Right - cb.Left > 0 && cb.Bottom - cb.Top > 0) n++;
        return n;
    }

    private static bool CoverageLooksSparse(PageText text, double pageW, double pageH)
    {
        if (pageW <= 0 || pageH <= 0) return true;
        double glyphArea = 0;
        foreach (var cb in text.CharBoxes)
            glyphArea += Math.Max(0, cb.Right - cb.Left) * Math.Max(0, cb.Bottom - cb.Top);
        // A born-digital text page covers roughly 2-15% of its area with ink. Well under 1% with a
        // handful of characters is the signature of a scan that carries a stray text layer.
        return glyphArea / (pageW * pageH) < 0.005 && CountMeaningfulChars(text) < 60;
    }

    /// <summary>
    /// Builds a <see cref="PageText"/> from recognised lines, in page points.
    /// </summary>
    /// <remarks>
    /// Character indices are rebased from each line's own text into the concatenated page string, so
    /// every downstream consumer sees one coherent index space. <c>FromOcr</c> is set so the result is
    /// presented as unverified rather than as the document's own text.
    /// </remarks>
    internal static PageText BuildPageTextFromOcr(OcrPage ocrPage)
    {
        var sb = new StringBuilder();
        var chars = new List<CharBox>();
        foreach (var line in ocrPage.Lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            int baseIndex = sb.Length;
            sb.Append(line.Text);
            sb.Append('\n');
            if (line.Chars is null) continue;
            foreach (var c in line.Chars)
            {
                // OcrLine.Chars indices are offsets into that line's own Text.
                if (c.Index < 0 || c.Index >= line.Text.Length) continue;
                chars.Add(new CharBox(baseIndex + c.Index, c.Left, c.Top, c.Right, c.Bottom, c.Angle));
            }
        }
        return new PageText(sb.ToString(), chars) { FromOcr = true };
    }

    private static PageExtraction BuildPageExtraction(
        int pageIndex, double pageW, double pageH, PageText text, PageAnalysis analysis,
        bool ocrUsed, List<string> warnings)
    {
        var charRects = new Dictionary<int, CoreRectF>(text.CharBoxes.Count);
        foreach (var cb in text.CharBoxes)
            if (!charRects.ContainsKey(cb.Index)) charRects[cb.Index] = new CoreRectF(cb.Left, cb.Top, cb.Right, cb.Bottom);

        var blocks = new List<ExtractedBlock>(analysis.Blocks.Count);
        var lines = new List<ExtractedLine>();

        for (int b = 0; b < analysis.Blocks.Count; b++)
        {
            var block = analysis.Blocks[b];
            var lineIndices = new List<int>(block.Lines.Count);

            for (int l = 0; l < block.Lines.Count; l++)
            {
                var li = block.Lines[l];
                var rect = LineRect(li);
                var range = CharRangeInRect(text, rect);
                if (range.Length == 0) continue;   // a line with no characters carries nothing to narrate
                lineIndices.Add(lines.Count);
                lines.Add(new ExtractedLine(
                    LineId: $"p{pageIndex}-b{b}-l{l}",
                    BlockIndex: b,
                    Text: range,
                    Rect: rect,
                    FromOcr: ocrUsed || text.FromOcr));
            }

            blocks.Add(new ExtractedBlock(
                BlockId: $"p{pageIndex}-b{b}",
                Kind: MapRole(block.Role),
                Order: block.Order,
                BBox: ToPageRect(block.BBox),
                LineIndices: lineIndices,
                Confidence: block.Confidence,
                UprightTurns: block.UprightTurns));
        }

        var chars = new List<ExtractedChar>(text.CharBoxes.Count);
        foreach (var cb in text.CharBoxes)
            chars.Add(new ExtractedChar(cb.Index, new PageRect(cb.Left, cb.Top, cb.Right, cb.Bottom), cb.Angle));

        double glyphArea = 0;
        foreach (var c in chars) glyphArea += c.Rect.Area;
        var coverage = new TextCoverage(
            TextLayerPresent: !ocrUsed && text.CharBoxes.Count > 0,
            OcrUsed: ocrUsed,
            CharCount: text.Text.Trim().Length,
            PageAreaPoints: pageW * pageH,
            GlyphAreaPoints: glyphArea,
            Warnings: warnings);

        return new PageExtraction(pageIndex, pageW, pageH, text.Text, blocks, lines, chars, coverage);
    }

    private static PageRect LineRect(LineInfo li)
        => new(li.X, li.Y - li.Height / 2f, li.X + li.Width, li.Y + li.Height / 2f);

    private static PageRect ToPageRect(BBox b) => new(b.X, b.Y, b.X + b.W, b.Y + b.H);

    /// <summary>
    /// The character range whose midpoints fall inside a line's rectangle.
    /// </summary>
    private static TextRange CharRangeInRect(PageText text, PageRect rect)
    {
        int min = int.MaxValue, max = int.MinValue;
        foreach (var cb in text.CharBoxes)
        {
            float mx = (cb.Left + cb.Right) / 2f, my = (cb.Top + cb.Bottom) / 2f;
            if (mx < rect.Left || mx > rect.Right || my < rect.Top || my > rect.Bottom) continue;
            if (cb.Index < 0 || cb.Index >= text.Text.Length) continue;
            if (cb.Index < min) min = cb.Index;
            if (cb.Index > max) max = cb.Index;
        }
        return max >= min ? TextRange.FromBounds(min, max + 1) : TextRange.Empty;
    }

    /// <summary>Maps Core's block roles onto Rail Narrator's own classification.</summary>
    internal static BlockKind MapRole(BlockRole role) => role switch
    {
        BlockRole.Text => BlockKind.Text,
        BlockRole.Heading => BlockKind.Heading,
        BlockRole.Title => BlockKind.Title,
        BlockRole.Caption => BlockKind.Caption,
        BlockRole.Aside => BlockKind.Aside,
        BlockRole.DisplayMath => BlockKind.DisplayMath,
        BlockRole.InlineMath => BlockKind.InlineMath,
        BlockRole.Algorithm => BlockKind.Algorithm,
        BlockRole.Table => BlockKind.Table,
        BlockRole.Figure => BlockKind.Figure,
        BlockRole.Chart => BlockKind.Chart,
        BlockRole.Header => BlockKind.Header,
        BlockRole.Footer => BlockKind.Footer,
        BlockRole.PageNumber => BlockKind.PageNumber,
        BlockRole.Footnote => BlockKind.Footnote,
        BlockRole.Reference => BlockKind.Reference,
        BlockRole.Decoration => BlockKind.Decoration,
        _ => BlockKind.Unknown,
    };

    public async Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(
        ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
    {
        if (_currentPdf is null || _currentPath is null) return null;
        if (region.IsEmpty || targetPixelWidth <= 0) return null;

        return await Task.Run(() =>
        {
            var pdf = _currentPdf!;
            var (pageW, pageH) = pdf.GetPageSize(pageIndex);
            if (pageW <= 0 || pageH <= 0) return (ReadOnlyMemory<byte>?)null;

            // Render at a resolution that makes the crop's width the requested pixel width. Equation
            // sub- and superscripts are only a few points tall, so a low-resolution crop loses exactly
            // the detail transcription depends on.
            double scale = targetPixelWidth / Math.Max(1.0, region.Width);
            int targetSize = (int)Math.Clamp(Math.Round(Math.Max(pageW, pageH) * scale), 64, 8192);
            var (rgb, pxW, pxH) = pdf.RenderPagePixmap(pageIndex, targetSize);
            if (pxW <= 0 || pxH <= 0) return (ReadOnlyMemory<byte>?)null;

            float sx = pxW / (float)pageW, sy = pxH / (float)pageH;
            var pixelRect = new SKRectI(
                (int)Math.Clamp(Math.Floor(region.Left * sx), 0, pxW - 1),
                (int)Math.Clamp(Math.Floor(region.Top * sy), 0, pxH - 1),
                (int)Math.Clamp(Math.Ceiling(region.Right * sx), 1, pxW),
                (int)Math.Clamp(Math.Ceiling(region.Bottom * sy), 1, pxH));
            if (pixelRect.Width <= 0 || pixelRect.Height <= 0) return (ReadOnlyMemory<byte>?)null;

            // The pixmap is packed 3-byte RGB, which Skia has no colour type for (Rgb888x is four
            // bytes a pixel: installing this buffer as it failed, and every crop threw). Copy just
            // the crop's rows out as RGBA.
            if (rgb.Length < pxW * pxH * 3) return (ReadOnlyMemory<byte>?)null;
            int w = pixelRect.Width, h = pixelRect.Height;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int src = ((pixelRect.Top + y) * pxW + pixelRect.Left) * 3, dst = y * w * 4;
                for (int x = 0; x < w; x++, src += 3, dst += 4)
                {
                    rgba[dst] = rgb[src];
                    rgba[dst + 1] = rgb[src + 1];
                    rgba[dst + 2] = rgb[src + 2];
                    rgba[dst + 3] = 255;
                }
            }
            using var image = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), rgba, w * 4);
            using var data = image?.Encode(SKEncodedImageFormat.Png, 100);
            return data is null ? null : new ReadOnlyMemory<byte>(data.ToArray());
        }, ct).ConfigureAwait(false);
    }

    private IPdfService? _currentPdf;
    private string? _currentPath;

    /// <summary>Keeps a document open so crop rendering does not re-open it for every equation.</summary>
    public void AttachDocument(string filePath, string? password = null)
    {
        if (_currentPath == filePath && _currentPdf is not null) return;
        (_currentPdf as IDisposable)?.Dispose();
        _currentPdf = _pdfFactory.CreatePdfService(filePath, password);
        _currentPath = filePath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_currentPdf as IDisposable)?.Dispose();
        _currentPdf = null;
    }
}
