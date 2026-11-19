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
/// <param name="Capabilities">The analyzer's self-description: its input size, and whether it supplies a reading order.</param>
/// <param name="ResolverFactory">
/// Puts a page's blocks in reading order, or null to choose from <paramref name="Capabilities"/>: the
/// model's own order when it supplies one, else XY-cut++. It runs for every analyzer.
/// </param>
/// <param name="OcrFactory">Creates an OCR engine, or null when recognition is unavailable.</param>
/// <param name="Device">Where <paramref name="AnalyzerFactory"/> runs its model. Null means the CPU.</param>
/// <param name="FallbackAnalyzerFactory">
/// A CPU analyzer to switch to when <paramref name="AnalyzerFactory"/>'s cannot be built or fails
/// mid-document, or null. The switch is reported in a page warning, never made silently.
/// </param>
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
    Func<IOcrService>? OcrFactory = null,
    LayoutDevice? Device = null,
    Func<ILayoutAnalyzer>? FallbackAnalyzerFactory = null)
{
    /// <summary>Where the layout model runs.</summary>
    public LayoutDevice ResolvedDevice => Device ?? LayoutDevice.Cpu;

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

    /// <summary>PP-DocLayoutV3 via RailReader.Core.Analysis, one file for both devices.</summary>
    public static ExtractionBackend OnnxLayout(string modelPath, Func<IOcrService>? ocrFactory = null,
        LayoutAcceleration acceleration = LayoutAcceleration.Cpu)
        => OnnxLayout(new LayoutModelFiles(LayoutModelArchitecture.PPDocLayoutV3, "pp-doclayout-v3", modelPath, modelPath),
            ocrFactory, acceleration);

    /// <summary>A layout model via RailReader.Core.Analysis. Needs the model files on disk.</summary>
    /// <param name="model">The architecture and its files for each device.</param>
    /// <param name="acceleration">
    /// <see cref="LayoutAcceleration.Auto"/> puts the model on a WebGPU-capable GPU when one is found,
    /// the GPU file exists and the model loads on it (checked here, once), and on the CPU otherwise,
    /// with the reason in <see cref="LayoutDevice.FallbackReason"/>. Defaults to the CPU so library
    /// callers opt in.
    /// </param>
    public static ExtractionBackend OnnxLayout(LayoutModelFiles model, Func<IOcrService>? ocrFactory = null,
        LayoutAcceleration acceleration = LayoutAcceleration.Cpu)
    {
        ArgumentNullException.ThrowIfNull(model);
        var device = LayoutDevice.Cpu;
        ILayoutAnalyzer? probe = null;
        if (acceleration == LayoutAcceleration.Auto)
        {
            if (model.GpuModelPath is { } gpuPath && File.Exists(gpuPath))
                probe = LayoutAnalyzerConstruction.TryOnGpu(model.Architecture, gpuPath, out device);
            else
                device = LayoutDevice.Cpu with { FallbackReason = "the GPU model file is not installed" };
        }
        probe ??= LayoutAnalyzerConstruction.OnCpu(model.Architecture, model.CpuModelPath);
        var caps = probe.Capabilities;

        Func<ILayoutAnalyzer> onCpu = () => LayoutAnalyzerConstruction.OnCpu(model.Architecture, model.CpuModelPath);
        Func<ILayoutAnalyzer> build = device.IsGpu
            ? () => LayoutAnalyzerConstruction.TryOnGpu(model.Architecture, model.GpuModelPath!, out var d)
                ?? throw new InvalidOperationException(d.FallbackReason)
            : onCpu;
        // The probe is kept and lent to every extraction rather than rebuilt for each: a GPU session
        // costs about 8 s to build and warm up. See SharedAnalyzer.
        var shared = new SharedAnalyzer(probe, build);
        string file = Path.GetFileName(device.IsGpu ? model.GpuModelPath! : model.CpuModelPath);
        return new ExtractionBackend(
            $"{model.Id}:{file}",
            shared.Rent,
            caps,
            // A model with its own reading order (PP-DocLayoutV3) keeps it; one without (Heron) is
            // ordered by Core's XY-cut++, the pairing RailReader2 and Core's AnalysisWorker use.
            () => caps.ProvidesReadingOrder ? new ModelOrderResolver() : new XYCutPlusPlusResolver(),
            ocrFactory,
            device,
            device.IsGpu ? onCpu : null);
    }
}

/// <summary>A layout model and the file each device runs, as RailReaderCore's registry routes them.</summary>
/// <param name="Architecture">Which analyzer runs the files.</param>
/// <param name="Id">Short stable name for the extraction's identity: <c>heron</c> or <c>pp-doclayout-v3</c>.</param>
/// <param name="GpuModelPath">
/// The file for the GPU, or null for none. Core routes Heron to its FP32 export there (INT8 has no
/// GPU benefit) and PP-DocLayoutV3 to its one FP32 file; the FP16 exports under-detect on a GPU.
/// </param>
/// <param name="CpuModelPath">The file for the CPU: Heron's INT8 export, or PP-DocLayoutV3's FP32 file.</param>
public sealed record LayoutModelFiles(LayoutModelArchitecture Architecture, string Id, string? GpuModelPath, string CpuModelPath);

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

            using var analyzer = AnalyzerSlot.Create(backend);
            var resolver = backend.ResolverFactory?.Invoke()
                ?? (backend.Capabilities.ProvidesReadingOrder ? new ModelOrderResolver() : new XYCutPlusPlusResolver());
            // Built on the first page that needs recognition, not up front: three ONNX models costing
            // 0.5-1.7 s that a document with a text layer on every page never uses.
            IOcrService? ocr = null;
            bool ocrTried = false;
            IOcrService? Ocr()
            {
                if (ocrTried) return ocr;
                ocrTried = true;
                if (request.Options.Ocr != OcrPolicy.Off && backend.OcrFactory is { } ocrFactory)
                {
                    try { ocr = ocrFactory(); }
                    catch (Exception ex)
                    {
                        // A missing OCR model must not stop a native-text document from being read.
                        _ocrError = $"OCR is unavailable: {ex.Message}";
                    }
                }
                return ocr;
            }
            try
            {
                foreach (int pageIndex in requested)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pageIndex < 0 || pageIndex >= pdf.PageCount) continue;
                    pages.Add(ExtractPage(pdf, pageIndex, viewRotation, request, backend, analyzer, resolver, Ocr, frame));
                    progress?.Report(new ExtractionProgress(++done, requested.Count,
                        pageIndex == requested[0] ? "Analysing layout" : "Extracting text and geometry"));
                }
                if (analyzer.UsedFallback) _layoutDeviceFellBack = true;
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

    private volatile bool _layoutDeviceFellBack;

    /// <summary>
    /// True once an extraction in this process had to leave the backend's layout device for its CPU
    /// fallback. Sticky, so a cache can stop storing snapshots whose key names a device that part of
    /// them did not run on. The next extraction still starts on the backend's device and falls back
    /// again if it has to.
    /// </summary>
    public bool LayoutDeviceFellBack => _layoutDeviceFellBack;

    private PageExtraction ExtractPage(
        IPdfService pdf, int pageIndex, int viewRotation, ExtractionRequest request,
        ExtractionBackend backend, AnalyzerSlot analyzer, IReadingOrderResolver resolver,
        Func<IOcrService?> getOcr, CoordinateFrameId frame)
    {
        var (pageW, pageH) = pdf.GetPageSize(pageIndex, viewRotation);
        var warnings = new List<string>();
        if (analyzer.TakePendingNote() is { } switched) warnings.Add(switched);

        // --- text layer ---------------------------------------------------------------
        PageText native = _textService.ExtractPageText(pdf.PdfBytes, pageIndex, viewRotation, request.Password);
        bool nativeUsable = CountMeaningfulChars(native) >= 8;

        PageText text = native;
        bool ocrUsed = false;
        var ocrLineBoxes = new List<BBox>();

        if (!nativeUsable)
        {
            var ocr = request.Options.Ocr == OcrPolicy.Off ? null : getOcr();
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
        else if (request.Options.Ocr == OcrPolicy.Full && CoverageLooksSparse(native, pageW, pageH) && getOcr() is { } ocr)
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
        int targetSize = AnalysisRasterSize(request.Options);
        var (rgb, pxW, pxH) = pdf.RenderPagePixmap(pageIndex, targetSize, viewRotation);
        float scaleX = pxW > 0 ? (float)(pageW / pxW) : 1f;
        float scaleY = pxH > 0 ? (float)(pageH / pxH) : 1f;

        PageAnalysis Analyse()
        {
            // A layout model takes the page at exactly its input size (max side == InputSize) and
            // rejects anything larger rather than crop it. Rendering it the high-resolution raster
            // made every page fail, so a configured model silently produced no blocks at all. The
            // high-resolution raster is still what line detection below works on.
            if (backend.Capabilities.Classes.Count > 0 && backend.Capabilities.InputSize > 0
                && Math.Max(pxW, pxH) != backend.Capabilities.InputSize)
            {
                var (modelRgb, modelW, modelH) = pdf.RenderPagePixmap(pageIndex, backend.Capabilities.InputSize, viewRotation);
                return analyzer.Current.RunAnalysis(modelRgb, modelW, modelH, pageW, pageH, text.CharBoxes);
            }
            return analyzer.Current.RunAnalysis(rgb, pxW, pxH, pageW, pageH, text.CharBoxes);
        }

        PageAnalysis analysis;
        try
        {
            try { analysis = Analyse(); }
            // A GPU that fails mid-document would otherwise fail every remaining page the same way:
            // finish the document on the CPU, starting with this page, and say so. The switch runs
            // in the handler, not a filter, so the failed call has unwound before its session is
            // disposed.
            catch (Exception ex) when (analyzer.CanSwitchToFallback)
            {
                analyzer.SwitchToFallback(ex);
                warnings.Add(analyzer.TakePendingNote()!);
                analysis = Analyse();
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Layout analysis failed ({ex.GetType().Name}: {ex.Message}); this page may not be narrated correctly.");
            analysis = new PageAnalysis { PageWidth = pageW, PageHeight = pageH };
        }

        // Every analyzer's blocks go through a resolver, a model's own order included, as in Core's
        // AnalysisWorker: BlockPostProcessor wants the list sorted in reading order, and a model
        // returns its detections by confidence with the order in a field. Left unsorted, the pass
        // that removes a duplicate block renumbered the survivors by their place in the list, and
        // such a page was read in order of confidence: body text, reference list and only then the
        // headings above them.
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

    /// <summary>
    /// The longest side, in pixels, of the raster that line detection and orientation work on.
    /// </summary>
    /// <remarks>
    /// <see cref="ExtractionOptions.AnalysisRenderSize"/> is a size in pixels. It was read as a DPI,
    /// so a Letter page asked for 11 in × 1920 = 21 120 px and was clamped to 8192: a 156 MB raster
    /// costing 0.4–1.6 s to render on every page, plus line detection over it, against 0.03–0.1 s at
    /// 1920. On a page with a text layer, lines come from the glyph boxes and the raster is only a
    /// fallback and the orientation check; scans get their lines from OCR, which renders its own.
    /// </remarks>
    public static int AnalysisRasterSize(ExtractionOptions options)
        => options.AnalysisRenderSize > 0 ? Math.Clamp(options.AnalysisRenderSize, 256, 8192) : 1024;

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
            double scale = CropScale(region, targetPixelWidth, pageW, pageH);
            // Only the crop is rasterised (Core 0.93.0, issue #222), not the whole page at that scale:
            // a narrow equation used to cost an 8192 px page raster of some 140 MB, held under
            // Core's PDFium lock. The image is the old one to within a pixel at the edges but not
            // byte for byte, so maths answers cached against the old crops are asked for again once
            // (the reader chose this over keeping the whole-page render).
            var (rgb, w, h) = pdf.RenderRegionPixmap(pageIndex,
                new BBox(region.Left, region.Top, region.Width, region.Height), scale, 0, ct);
            return EncodeRgbPng(rgb, w, h);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pixels per point for a crop <paramref name="targetPixelWidth"/> wide. Equation sub- and
    /// superscripts are a few points tall, so a low-resolution crop loses exactly the detail the
    /// model reads. Before region rendering the whole page's longest edge was held to 8192 px, which
    /// shrank a narrow equation's crop (an 80 pt one to 828 px of 1024); now only the crop's own
    /// longest side is held to 8192 px.
    /// </summary>
    internal static double CropScale(PageRect region, int targetPixelWidth, double pageW, double pageH)
    {
        double scale = targetPixelWidth / Math.Max(1.0, region.Width);
        double longest = Math.Max(region.Width, region.Height);
        return Math.Min(scale, 8192 / Math.Max(1.0, longest));
    }

    /// <summary>Packed 3-byte RGB (PDFium's pixmap) to PNG. Skia has no 3-byte colour type, so via RGBA.</summary>
    internal static ReadOnlyMemory<byte>? EncodeRgbPng(byte[] rgb, int w, int h)
    {
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) return null;
        var rgba = new byte[w * h * 4];
        for (int i = 0, j = 0; i < w * h; i++, j += 3)
        {
            rgba[i * 4] = rgb[j];
            rgba[i * 4 + 1] = rgb[j + 1];
            rgba[i * 4 + 2] = rgb[j + 2];
            rgba[i * 4 + 3] = 255;
        }
        using var image = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), rgba, w * 4);
        using var data = image?.Encode(SKEncodedImageFormat.Png, 100);
        return data is null ? null : new ReadOnlyMemory<byte>(data.ToArray());
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
