// Reading-order survey through the application's own extraction adapter (layout model, resolver,
// Core's post-processing), as status.md items 70 and 80 report it.
//
//   rail-narrator-order-survey <heron|pp-doclayout-v3> <model.onnx> <out.json> [--max-pages N] <pdf or dir>...
//
// A block is "after-below" when it is read after the nearest content block directly below it in its
// column: x-overlapping, starting no more than 2 pt above its bottom. Header, Footer and PageNumber
// are not content. Headings and titles are counted apart from other blocks. This is the measure of
// RailReaderCore's tools/order-eval compare.py (issue #223), applied to what the application reads.
// CPU, OCR off: ordering does not depend on OCR, and the run is then deterministic.
using System.Text.Json;
using RailNarrator.Domain;
using RailNarrator.Pdf;
using RailReader.Core.Models;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: rail-narrator-order-survey <heron|pp-doclayout-v3> <model.onnx> <out.json> [--max-pages N] <pdf or dir>...");
    return 2;
}
string analyzer = args[0], model = args[1], output = args[2];
int maxPages = 20;
var inputs = new List<string>();
for (int i = 3; i < args.Length; i++)
{
    if (args[i] == "--max-pages") { maxPages = int.Parse(args[++i]); continue; }
    if (Directory.Exists(args[i])) inputs.AddRange(Directory.EnumerateFiles(args[i], "*.pdf").Order(StringComparer.Ordinal));
    else inputs.Add(args[i]);
}

var architecture = analyzer == "heron" ? LayoutModelArchitecture.Heron : LayoutModelArchitecture.PPDocLayoutV3;
var backend = ExtractionBackend.OnnxLayout(new LayoutModelFiles(architecture, analyzer, model, model), null, LayoutAcceleration.Cpu);
var furniture = new HashSet<BlockKind> { BlockKind.Header, BlockKind.Footer, BlockKind.PageNumber };
int headingBad = 0, headingTotal = 0, otherBad = 0, otherTotal = 0, pagesSurveyed = 0;
var findings = new List<object>();
var documents = new List<object>();

foreach (string pdf in inputs)
{
    try
    {
        using var source = new RailReaderExtractionSource();
        source.WithBackend(backend);
        var snapshot = await source.ExtractAsync(new ExtractionRequest(pdf, null, Enumerable.Range(0, maxPages).ToList(),
            new ExtractionOptions(analyzer, OcrPolicy.Off, 1024, false, ExtractorVersions.Current), NarrationFilterSettings.Default));
        int docBad = 0;
        foreach (var page in snapshot.Pages)
        {
            pagesSurveyed++;
            var content = page.Blocks.Where(b => !furniture.Contains(b.Kind) && !b.BBox.IsEmpty).ToList();
            foreach (var a in content)
            {
                ExtractedBlock? below = null;
                foreach (var b in content)
                {
                    if (ReferenceEquals(a, b) || !(a.BBox.Left < b.BBox.Right && b.BBox.Left < a.BBox.Right)) continue;
                    if (b.BBox.Top < a.BBox.Bottom - 2) continue;
                    if (below is null || b.BBox.Top < below.BBox.Top) below = b;
                }
                if (below is null) continue;
                bool heading = a.Kind is BlockKind.Heading or BlockKind.Title;
                bool bad = a.Order > below.Order;
                if (heading) { headingTotal++; if (bad) headingBad++; } else { otherTotal++; if (bad) otherBad++; }
                if (!bad) continue;
                docBad++;
                findings.Add(new
                {
                    Pdf = Path.GetFileName(pdf), Page = page.PageIndex + 1, Kind = a.Kind.ToString(), BelowKind = below.Kind.ToString(),
                    Text = Snippet(page, a), BelowText = Snippet(page, below),
                });
            }
        }
        documents.Add(new { Pdf = Path.GetFileName(pdf), Pages = snapshot.Pages.Count, AfterBelow = docBad });
        Console.Error.WriteLine($"{Path.GetFileName(pdf)}: {snapshot.Pages.Count} pages, {docBad} after-below");
    }
    catch (Exception ex)
    {
        documents.Add(new { Pdf = Path.GetFileName(pdf), Error = ex.Message });
        Console.Error.WriteLine($"{Path.GetFileName(pdf)}: failed: {ex.Message}");
    }
}

var summary = new
{
    Core = typeof(LayoutModelArchitecture).Assembly.GetName().Version?.ToString(),
    Analyzer = analyzer, Model = Path.GetFileName(model), MaxPages = maxPages, Documents = documents.Count, Pages = pagesSurveyed,
    HeadingsAfterBelow = headingBad, Headings = headingTotal, OthersAfterBelow = otherBad, Others = otherTotal,
};
File.WriteAllText(output, JsonSerializer.Serialize(new { summary, documents, findings }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(summary));
return 0;

static string Snippet(PageExtraction page, ExtractedBlock block)
{
    string text = string.Join(" ", block.LineIndices.Where(i => i >= 0 && i < page.Lines.Count)
        .Select(i => page.Lines[i].Text)
        .Where(r => r.Start >= 0 && r.End <= page.Text.Length)
        .Select(r => page.Text.Substring(r.Start, r.Length)));
    return text.Length > 80 ? text[..80] + "…" : text;
}
