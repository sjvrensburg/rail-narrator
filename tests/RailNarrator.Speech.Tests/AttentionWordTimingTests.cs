using System.Text.Json;
using RailNarrator.Domain;
using RailNarrator.Speech;
using Xunit;

namespace RailNarrator.Speech.Tests;

/// <summary>
/// Word timing read from a speech model's attention. The fixture is three passages as the real
/// Phonon model spoke them (through the patched library, in Maeve): each chunk's text and tokens,
/// the alignment head's attention at every frame, the clip's loudness, and the word times the
/// reference implementation gave, which were compared with a forced aligner (ADR-0013).
/// </summary>
public sealed class AttentionWordTimingTests
{
    private sealed record Row(string text, long samples, List<ChunkRow> chunks, double[] db, List<WordRow> words);
    private sealed record ChunkRow(string text, int[] ends, float[][] rows);
    private sealed record WordRow(int start, int length, long from, long to);

    private static List<Row> Fixture()
        => JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "phonon-attention.json")))!;

    private static IReadOnlyList<AttentionWordTiming.Chunk> Chunks(Row row)
        => row.chunks.Select(c => new AttentionWordTiming.Chunk(c.text, c.ends, c.rows)).ToList();

    private static IReadOnlyList<TimedCue>? Words(Row row, out string? problem, string? script = null, IReadOnlyList<AttentionWordTiming.Chunk>? chunks = null)
        => AttentionWordTiming.Words(script ?? row.text, chunks ?? Chunks(row), row.db, 24000, 1920, row.samples, out problem);

    [Fact]
    public void RealPassages_GiveTheReferencesWordTimes()
    {
        foreach (var row in Fixture())
        {
            var cues = Words(row, out string? problem);

            Assert.Null(problem);
            Assert.NotNull(cues);
            Assert.Equal(row.words.Count, cues.Count);
            for (int i = 0; i < cues.Count; i++)
            {
                Assert.Equal(new TextRange(row.words[i].start, row.words[i].length), cues[i].SpokenRange);
                // The reference rounds a half differently: within a sample.
                Assert.InRange(cues[i].StartSample - row.words[i].from, -1, 1);
                Assert.InRange(cues[i].EndSample - row.words[i].to, -1, 1);
            }
        }
    }

    [Fact]
    public void TheCues_AreWhatTheValidatorAcceptsForEveryWord()
    {
        foreach (var row in Fixture())
        {
            var cues = Words(row, out _)!.Select(c => c with { AnchorIds = ["a"] }).ToArray();

            var checkedCues = TimedCueValidator.Validate(cues, row.samples, row.text, new HashSet<string> { "a" },
                provenance: TimingProvenance.ModelAttention);

            Assert.Empty(checkedCues.Rejected);
            Assert.True(checkedCues.IsUsable);
            Assert.Equal(1.0, checkedCues.WordCoverageFraction);
            Assert.Equal(SpokenWord.Tokenize(row.text).Select(w => w.Range), cues.Select(c => c.SpokenRange!.Value));
        }
    }

    [Fact]
    public void APassageSpokenInSeveralChunks_IsTimedAcrossThem()
    {
        var row = Fixture().Single(r => r.chunks.Count > 1);
        var cues = Words(row, out _)!;

        // The second chunk's first word starts after the first chunk's frames, not at zero.
        long firstChunkSamples = row.chunks[0].rows.Length * 1920L;
        var second = cues.First(c => row.text.Substring(c.SpokenRange!.Value.Start, c.SpokenRange.Value.Length) == "However");
        Assert.InRange(second.StartSample, firstChunkSamples, firstChunkSamples + 24000);
        Assert.True(cues.Zip(cues.Skip(1)).All(p => p.First.EndSample <= p.Second.StartSample));
    }

    [Fact]
    public void WhenTheEngineSpokeOtherWords_ThereIsNoTiming()
    {
        var row = Fixture()[0];

        Assert.Null(Words(row, out string? problem, script: row.text.Replace("remainder", "rest", StringComparison.Ordinal)));
        Assert.Contains("other words", problem, StringComparison.Ordinal);
        Assert.Null(Words(row, out problem, script: row.text + " And more."));
        Assert.Contains("other words", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseAndPunctuationTheEngineChanged_DoNotMatter()
    {
        // The engine capitalises the first letter and ends the text with a full stop.
        var row = Fixture()[0];
        string script = char.ToLowerInvariant(row.text[0]) + row.text[1..].TrimEnd('.');

        var cues = Words(row, out string? problem, script: script);

        Assert.Null(problem);
        Assert.Equal(SpokenWord.Tokenize(script).Count, cues!.Count);
    }

    [Fact]
    public void AHeadThatStopsShortOfTheText_GivesNoTiming()
    {
        // A skipped ending would be hidden by a position that never moves back.
        var row = Fixture()[0];
        var cut = Chunks(row).Select(c => c with { Frames = c.Frames.Take(c.Frames.Count / 2).ToList() }).ToList();

        Assert.Null(Words(row, out string? problem, chunks: cut));
        Assert.Contains("did not reach the end", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadThatDoesNotFollowTheText_GivesNoTiming()
    {
        var row = Fixture()[0];
        var flat = Chunks(row).Select(c => c with
        {
            Frames = c.Frames.Select((f, i) => Enumerable.Range(0, f.Length).Select(k => k == f.Length - 1 && i > 0 ? 0.06f : 0.05f).ToArray()).ToList(),
        }).ToList();

        Assert.Null(Words(row, out string? problem, chunks: flat));
        Assert.Contains("not following", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnvelope_IsOneValueForEachTenMilliseconds()
    {
        var samples = new float[24000];
        for (int i = 12000; i < 24000; i++) samples[i] = 0.5f * MathF.Sin(i * 0.1f);

        var db = AttentionWordTiming.Envelope(samples, 24000);

        Assert.Equal(100, db.Length);
        Assert.True(db[10] < -100);
        Assert.InRange(db[80], -12, -6);   // a 0.5 sine is about -9 dB
    }
}
