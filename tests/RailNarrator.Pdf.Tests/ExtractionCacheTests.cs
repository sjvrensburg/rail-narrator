using RailNarrator.Domain;
using RailNarrator.Infrastructure;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Pdf.Tests;

/// <summary>
/// A snapshot read back from the extraction cache must narrate exactly as the fresh one did:
/// same units, same plan fingerprint, same highlight geometry.
/// </summary>
public sealed class ExtractionCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rn-extraction-cache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CachedSnapshotOfARealPdf_BuildsTheSamePlan()
    {
        // Two columns and a line-end hyphen: reading order, line geometry and the U+0002 marker
        // all have to survive the round trip.
        string path = SyntheticPdf.WriteTwoColumn(SyntheticPdf.TempPath("cache-two-col"),
            ["The left column opens the argument.", "It continues on a second line."],
            ["The right column closes it.", "Nothing follows the right column."]);
        string hyphenated = SyntheticPdf.WriteHyphenated(SyntheticPdf.TempPath("cache-hyphen"),
            ("The estimator is unbiased under the stated assump-", "tions and remains consistent."));

        foreach (string pdf in new[] { path, hyphenated })
        {
            using var source = new RailReaderExtractionSource();
            source.WithBackend(ExtractionBackend.ModelFree());
            var cache = new CachingExtractionSource(source, _root);
            var request = new ExtractionRequest(pdf, null, null,
                new ExtractionOptions("text-layout", OcrPolicy.Off, 1024, false, ExtractorVersions.Current),
                NarrationFilterSettings.Default);

            var fresh = await cache.ExtractAsync(request);
            Assert.False(cache.LastWasCacheHit);
            var cached = await cache.ExtractAsync(request);
            Assert.True(cache.LastWasCacheHit);

            Assert.Equal(fresh.Identity, cached.Identity);
            Assert.Equal(fresh.Frame, cached.Frame);
            Assert.Equal(fresh.Options, cached.Options);
            for (int i = 0; i < fresh.PageCount; i++)
            {
                Assert.Equal(fresh.Pages[i].Text, cached.Pages[i].Text);
                Assert.Equal(fresh.Pages[i].Chars, cached.Pages[i].Chars);
                Assert.Equal(fresh.Pages[i].Lines, cached.Pages[i].Lines);
                Assert.Equal(fresh.Pages[i].Blocks.Select(b => (b.BlockId, b.Kind, b.Order, b.BBox)),
                    cached.Pages[i].Blocks.Select(b => (b.BlockId, b.Kind, b.Order, b.BBox)));
            }

            var builder = new ReadingUnitBuilder(new ReadingUnitBuilderOptions(NarrationFilterSettings.Default));
            var freshUnits = builder.Build(fresh);
            var cachedUnits = builder.Build(cached);
            Assert.Equal(
                NarrationPlanner.ComputeInputFingerprint(NarrationPlannerOptions.Default, NarrationFilterSettings.Default, freshUnits),
                NarrationPlanner.ComputeInputFingerprint(NarrationPlannerOptions.Default, NarrationFilterSettings.Default, cachedUnits));

            var planner = new NarrationPlanner();
            var freshPlan = await planner.BuildPlanAsync(fresh, freshUnits, NarrationFilterSettings.Default);
            var cachedPlan = await planner.BuildPlanAsync(cached, cachedUnits, NarrationFilterSettings.Default);
            Assert.Equal(freshPlan.Segments.Select(s => s.SpokenText), cachedPlan.Segments.Select(s => s.SpokenText));
            Assert.Equal(
                freshUnits.SelectMany(u => u.Anchors).SelectMany(a => a.Rects),
                cachedUnits.SelectMany(u => u.Anchors).SelectMany(a => a.Rects));
            Assert.Equal(freshPlan.Segments.Select(s => s.AnchorIds.Count), cachedPlan.Segments.Select(s => s.AnchorIds.Count));
        }
    }
}
