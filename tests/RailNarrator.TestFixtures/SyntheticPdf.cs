using RailReader.Core.Services;
using SkiaSharp;

namespace RailNarrator.TestFixtures;

/// <summary>
/// One line of text placed at an exact position, so a test can assert on the geometry it gets back.
/// </summary>
/// <param name="Text">The characters to draw.</param>
/// <param name="X">Left edge in PDF points.</param>
/// <param name="BaselineY">Baseline in PDF points, measured from the top of the page as displayed.</param>
/// <param name="FontSize">Type size in points.</param>
public sealed record FixtureLine(string Text, float X, float BaselineY, float FontSize = 11f);

/// <summary>
/// Builds redistributable synthetic PDFs with known text and geometry.
/// </summary>
/// <remarks>
/// <para>
/// Everything the deterministic tests need is generated here rather than committed as a binary:
/// no proprietary PDF, no licence question, and the exact character positions are known at build
/// time so a test can assert that a highlight landed on the right line rather than merely that it
/// landed somewhere.
/// </para>
/// <para>
/// Pages are 612×792 pt (US Letter) with a 72 pt margin unless a test says otherwise. Text is drawn
/// line by line at explicit baselines, because drawing a paragraph as one blob leaves the line
/// geometry up to the rasteriser and makes assertions approximate.
/// </para>
/// </remarks>
public static class SyntheticPdf
{
    public const float PageWidth = 612f;
    public const float PageHeight = 792f;
    public const float Margin = 72f;

    /// <summary>Default line pitch in points.</summary>
    public const float LinePitch = 16f;

    /// <summary>
    /// Writes a single-column page of body text. Returns the file path.
    /// </summary>
    public static string WriteSingleColumn(string path, IReadOnlyList<string> lines, int pageCount = 1)
    {
        var pages = new List<List<FixtureLine>>();
        for (int p = 0; p < pageCount; p++)
        {
            var page = new List<FixtureLine>();
            float y = Margin + LinePitch;
            foreach (var line in lines)
            {
                page.Add(new FixtureLine(line, Margin, y));
                y += LinePitch;
            }
            pages.Add(page);
        }
        return Write(path, pages);
    }

    /// <summary>
    /// Writes a two-column page. Column order matters: a correct reading-order resolver returns the
    /// whole left column before the whole right column, not alternating line by line.
    /// </summary>
    /// <param name="left">Lines for the left column.</param>
    /// <param name="right">Lines for the right column.</param>
    /// <param name="gutter">Gap between columns in points.</param>
    public static string WriteTwoColumn(string path, IReadOnlyList<string> left, IReadOnlyList<string> right, float gutter = 24f)
    {
        float columnWidth = (PageWidth - 2 * Margin - gutter) / 2f;
        var lines = new List<FixtureLine>();
        float y = Margin + LinePitch;
        foreach (var l in left) { lines.Add(new FixtureLine(l, Margin, y)); y += LinePitch; }
        y = Margin + LinePitch;
        foreach (var r in right) { lines.Add(new FixtureLine(r, Margin + columnWidth + gutter, y)); y += LinePitch; }
        return Write(path, [lines]);
    }

    /// <summary>
    /// Writes a page with a repeated running header and footer plus body text. The header text is
    /// identical on every page, which is the signal the running-element detector keys on.
    /// </summary>
    public static string WriteWithRunningElements(string path, string header, string footer, IReadOnlyList<string> bodyLines, int pageCount = 3)
    {
        var pages = new List<List<FixtureLine>>();
        for (int p = 0; p < pageCount; p++)
        {
            var lines = new List<FixtureLine>
            {
                new(header, Margin, Margin - 24f, 9f),
                new($"{footer}   {p + 1}", Margin, PageHeight - Margin + 30f, 9f),
            };
            float y = Margin + LinePitch;
            foreach (var body in bodyLines)
            {
                lines.Add(new FixtureLine(body, Margin, y));
                y += LinePitch;
            }
            pages.Add(lines);
        }
        return Write(path, pages);
    }

    /// <summary>
    /// Writes a page whose text layer is absent: shapes are drawn but no text, the way a scan looks
    /// to a text extractor. Used to prove that a missing text layer is reported rather than read as an
    /// empty but successful page.
    /// </summary>
    public static string WriteTextless(string path, int pageCount = 2)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var document = SKDocument.CreatePdf(stream);
        for (int p = 0; p < pageCount; p++)
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            using var paint = new SKPaint { Color = SKColors.DarkGray, IsAntialias = true };
            for (int i = 0; i < 12; i++)
                canvas.DrawRect(Margin, Margin + i * LinePitch, PageWidth - 2 * Margin, 6f, paint);
            document.EndPage();
        }
        document.Close();
        return path;
    }

    /// <summary>
    /// Writes a page that looks like a scan carrying a stray text layer: bars drawn where its lines
    /// of print would be, which no text accounts for, and one short line of real text below them.
    /// </summary>
    public static string WriteScanWithStrayText(string path, string strayText)
    {
        var stray = new FixtureLine(strayText, Margin, PageHeight - Margin);
        return WriteChecked(path, [[stray]], canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.DarkGray, IsAntialias = true };
            for (int i = 0; i < 12; i++)
                canvas.DrawRect(Margin, Margin + i * LinePitch, PageWidth - 2 * Margin, 6f, paint);
        });
    }

    /// <summary>
    /// Writes a sentence that ends one page and continues on the next, so a unit must carry anchors on
    /// two pages. The two halves are separate lines with a deliberate hyphen-free break.
    /// </summary>
    public static string WriteCrossPageSentence(string path, string firstHalf, string secondHalf)
    {
        var page1 = new List<FixtureLine> { new(firstHalf, Margin, Margin + LinePitch) };
        var page2 = new List<FixtureLine> { new(secondHalf, Margin, Margin + LinePitch) };
        return Write(path, [page1, page2]);
    }

    /// <summary>
    /// Writes lines containing end-of-line hyphenation, so dehyphenation has something real to join.
    /// </summary>
    public static string WriteHyphenated(string path, params (string FirstFragment, string SecondFragment)[] pairs)
    {
        var lines = new List<FixtureLine>();
        float y = Margin + LinePitch;
        foreach (var (first, second) in pairs)
        {
            lines.Add(new FixtureLine(first, Margin, y));
            y += LinePitch;
            lines.Add(new FixtureLine(second, Margin, y));
            y += LinePitch * 1.5f;
        }
        return Write(path, [lines]);
    }

    /// <summary>
    /// Writes a page with prose lines and a display-equation-like line: a short centred block carrying
    /// operators and sub/superscript-sized glyphs.
    /// </summary>
    public static string WriteWithDisplayMaths(string path, IReadOnlyList<string> proseBefore, string equation, IReadOnlyList<string> proseAfter)
    {
        var lines = new List<FixtureLine>();
        float y = Margin + LinePitch;
        foreach (var p in proseBefore) { lines.Add(new FixtureLine(p, Margin, y)); y += LinePitch; }

        y += LinePitch;
        // Drawn in three pieces: a base line, a smaller raised run (a superscript) and a smaller
        // lowered run (a subscript), which is the geometry the script-size cue looks for.
        float centreX = PageWidth / 2f - 90f;
        lines.Add(new FixtureLine(equation, centreX, y, 12f));
        y += LinePitch * 1.5f;

        foreach (var p in proseAfter) { lines.Add(new FixtureLine(p, Margin, y)); y += LinePitch; }
        return Write(path, [lines]);
    }

    /// <summary>
    /// Writes pages from explicit line placements. The general entry point the helpers build on.
    /// </summary>
    public static string Write(string path, IReadOnlyList<IReadOnlyList<FixtureLine>> pages)
        => WriteChecked(path, pages, background: null);

    /// <summary>How many times a PDF whose text does not read back is written again.</summary>
    private const int WriteAttempts = 5;

    // Only for reading back what was written; guarded by Core's PDFium gate like every other use.
    private static readonly PdfTextService TextReader = new();

    /// <summary>
    /// Writes the pages, then reads their text back through PDFium and writes them again if a
    /// letter or digit that was drawn does not come back.
    /// </summary>
    /// <remarks>
    /// Skia's PDF writer takes each font's glyph-to-Unicode table from the platform's font
    /// manager. On Windows that is DirectWrite, whose <c>GetGlyphIndices</c> intermittently returns
    /// a glyph index past the font's glyph count (Skia, <c>SkTypeface_win_dw.cpp</c>, crbug.com/470146);
    /// Skia then stops filling the table, so the document's glyphs map to U+0000. The page keeps
    /// its glyphs and their positions and loses its text: twice on Windows CI a test read
    /// "Acknowledgments" as fifteen NULs. Each new document asks DirectWrite again, so writing the
    /// file again gives the text back. Linux (FreeType) is not affected.
    /// Only letters and digits are compared, as counts: PDFium adds characters of its own (a NUL
    /// between two spaces, line ends), and a character the font has no glyph for is not drawn as itself.
    /// </remarks>
    private static string WriteChecked(string path, IReadOnlyList<IReadOnlyList<FixtureLine>> pages,
        Action<SKCanvas>? background)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var drawn = pages.Select(DrawnLettersAndDigits).ToArray();
        string problem = "";
        for (int attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var document = SKDocument.CreatePdf(stream))
            {
                foreach (var pageLines in pages)
                {
                    var canvas = document.BeginPage(PageWidth, PageHeight);
                    background?.Invoke(canvas);
                    foreach (var line in pageLines)
                        DrawLine(canvas, line);
                    document.EndPage();
                }
                document.Close();
            }

            problem = TextProblem(File.ReadAllBytes(path), drawn);
            if (problem.Length == 0)
                return path;
            Console.Error.WriteLine($"SyntheticPdf: {Path.GetFileName(path)} attempt {attempt}: {problem}; writing it again.");
        }
        throw new InvalidOperationException(
            $"The fixture {Path.GetFileName(path)} did not read back after {WriteAttempts} attempts: {problem}.");
    }

    /// <summary>How many of each letter and digit a page draws in a glyph of the font.</summary>
    private static Dictionary<char, int> DrawnLettersAndDigits(IReadOnlyList<FixtureLine> lines)
    {
        var counts = new Dictionary<char, int>();
        foreach (var line in lines)
        {
            using var font = Font(line.FontSize);
            foreach (char c in line.Text)
                if (char.IsLetterOrDigit(c) && font.GetGlyph(c) != 0)
                    counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        return counts;
    }

    /// <summary>Empty when every page reads back every letter and digit it drew.</summary>
    private static string TextProblem(byte[] pdf, Dictionary<char, int>[] drawn)
    {
        for (int page = 0; page < drawn.Length; page++)
        {
            string text = TextReader.ExtractPageText(pdf, page).Text;
            int missing = drawn[page].Sum(d => Math.Max(0, d.Value - text.Count(c => c == d.Key)));
            if (missing > 0)
            {
                int nuls = text.Count(c => c == '\0');
                return $"page {page + 1} lacks {missing} of the letters and digits drawn on it " +
                       $"({nuls} of its {text.Length} characters read as U+0000)";
            }
        }
        return "";
    }

    private static SKFont Font(float size)
    {
        // FromFamilyName's typeface is shared and cannot be disposed by its caller.
        var typeface = SKTypeface.FromFamilyName("DejaVu Serif", SKFontStyle.Normal);
        return new SKFont(typeface ?? SKTypeface.Default, size);
    }

    private static void DrawLine(SKCanvas canvas, FixtureLine line)
    {
        using var font = Font(line.FontSize);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        // Skia's Y is measured from the top of the page with Y down, which matches the displayed
        // frame the extraction reports, so the baseline can be passed straight through.
        canvas.DrawText(line.Text, line.X, line.BaselineY, font, paint);
    }

    /// <summary>A temporary path for a fixture, cleaned up when the process exits.</summary>
    public static string TempPath(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rail-narrator-fixtures");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{name}-{Guid.NewGuid():N}.pdf");
        TempFiles.Register(path);
        return path;
    }
}

/// <summary>Tracks fixture files so a test run does not litter the temporary directory.</summary>
internal static class TempFiles
{
    private static readonly List<string> Paths = [];
    private static bool _hooked;

    public static void Register(string path)
    {
        lock (Paths)
        {
            Paths.Add(path);
            if (_hooked) return;
            _hooked = true;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Paths)
            {
                foreach (var p in Paths) { try { File.Delete(p); } catch { /* best effort at exit */ } }
            }
        };
    }
}
