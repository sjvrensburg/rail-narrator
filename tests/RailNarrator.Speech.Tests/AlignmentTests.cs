using System.Diagnostics;
using System.Text.Json;
using KokoroSharp.Core;
using RailNarrator.Domain;
using Xunit;
using Xunit.Abstractions;

namespace RailNarrator.Speech.Tests;

public sealed class AlignmentTests(ITestOutputHelper output)
{
    private static readonly AudioFormat Format = AudioFormat.Mono24000Float32;
    private static string Json(params object[][] entries)
        => JsonSerializer.Serialize(new { tiers = new { words = new { entries } } });

    [Fact]
    public void RepeatedWordsRetainDistinctScriptOffsetsAndSilenceIsIgnored()
    {
        var result = MfaForcedAligner.ParseAlignment(Json([0.0, 0.2, ""], [0.2, 0.4, "go"],
            [0.4, 0.7, "<eps>"], [0.7, 0.9, "go"]), "Go, go!", Format, 24000);
        Assert.Equal(new TextRange(0, 2), result.Words[0].SpokenRange);
        Assert.Equal(new TextRange(4, 2), result.Words[1].SpokenRange);
        Assert.Equal(16800, result.Words[1].StartSample);
        Assert.All(result.Words, w => Assert.Null(w.Confidence));
    }

    [Theory]
    [InlineData("go stop", "go")]
    [InlineData("go", "stop")]
    [InlineData("go", "<unk>")]
    public void MissingOrWrongWordsCannotShiftLaterHighlights(string script, string label)
        => Assert.Throws<InvalidDataException>(() => MfaForcedAligner.ParseAlignment(Json([0.1, 0.5, label]), script, Format, 24000));

    [Fact]
    public void RejectsOverlapAndOutOfRangeIntervals()
    {
        Assert.Throws<InvalidDataException>(() => MfaForcedAligner.ParseAlignment(Json([0.0, 0.6, "go"], [0.5, 0.9, "go"]), "go go", Format, 24000));
        Assert.Throws<InvalidDataException>(() => MfaForcedAligner.ParseAlignment(Json([0.0, 2.0, "go"]), "go", Format, 24000));
    }

    [Fact]
    public void NativePhonemesUseRawStepOffsetsAndOneActualTrim()
    {
        var cue = KokoroTtsProvider.PhonemeCue(new PhonemeTimestamp { Phoneme = 'a', StartSecond = 0.2f, EndSecond = 0.3f }, 24000);
        var shifted = Assert.Single(KokoroTtsProvider.ShiftCues([cue], 2400, 48000));
        Assert.Equal(26400, shifted.StartSample);
        Assert.Equal(28800, shifted.EndSample);
    }

    [MfaIntegrationFact]
    public async Task RealKokoroAudioAlignsEveryWord()
    {
        string root = Path.Combine(Path.GetTempPath(), "rn-mfa-integration");
        await using var aligner = new MfaForcedAligner(new MfaAlignerSettings(
            Environment.GetEnvironmentVariable("RN_MFA_EXECUTABLE")!,
            Environment.GetEnvironmentVariable("RN_MFA_DICTIONARY")!,
            Environment.GetEnvironmentVariable("RN_MFA_MODEL")!, root));
        await using var tts = new KokoroTtsProvider(new KokoroProviderSettings(Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL")!));
        const string script = "The quick brown fox jumps over the lazy dog.";
        var speech = await tts.SynthesizeAsync(new TtsSynthesisRequest(script, "af_heart", "en-US", Format));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "verified.wav"), WavCodec.Write(speech.Format, speech.Pcm.Span));
        await File.WriteAllTextAsync(Path.Combine(root, "verified.lab"), script);
        var watch = Stopwatch.StartNew();
        var result = await aligner.AlignAsync(speech.Pcm, speech.Format, script);
        output.WriteLine($"{result.Words.Count} words; audio {speech.DurationSeconds:F3}s; alignment {watch.Elapsed.TotalSeconds:F3}s");
        double coldSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        var warm = await aligner.AlignAsync(speech.Pcm, speech.Format, script);
        Assert.Equal(result.Words, warm.Words);
        await File.WriteAllTextAsync(Path.Combine(root, "benchmark.json"), JsonSerializer.Serialize(new {
            audioSeconds = speech.DurationSeconds, coldSeconds, warmSeconds = watch.Elapsed.TotalSeconds, words = result.Words.Count }));
        Assert.Equal(9, result.Words.Count);
        Assert.All(result.Words, w => Assert.InRange(w.EndSample, w.StartSample + 1, speech.FrameCount));
        for (int i = 1; i < result.Words.Count; i++) Assert.True(result.Words[i].StartSample >= result.Words[i - 1].EndSample);
        await File.WriteAllBytesAsync(Path.Combine(root, "verified.wav"), WavCodec.Write(speech.Format, speech.Pcm.Span));
        await File.WriteAllTextAsync(Path.Combine(root, "verified.json"), JsonSerializer.Serialize(result));
        output.WriteLine($"Review artifacts: {root}");
    }

    /// <summary>
    /// Opt-in (also needs <c>RN_MFA_G2P</c>): words the pronunciation dictionary lacks no longer
    /// cost the clip its word timing. 45% of a real forecasting chapter's sentences held at least
    /// one ("RMSE", "SVR", "irradiance", "predictors"), and each was highlighted whole.
    /// </summary>
    [MfaG2pIntegrationFact]
    public async Task RealKokoroAudioWithWordsTheDictionaryLacks_AlignsEveryWord()
    {
        string root = Path.Combine(Path.GetTempPath(), "rn-mfa-g2p-integration");
        await using var aligner = new MfaForcedAligner(new MfaAlignerSettings(
            Environment.GetEnvironmentVariable("RN_MFA_EXECUTABLE")!,
            Environment.GetEnvironmentVariable("RN_MFA_DICTIONARY")!,
            Environment.GetEnvironmentVariable("RN_MFA_MODEL")!, root,
            G2pModelPath: Environment.GetEnvironmentVariable("RN_MFA_G2P")));
        await using var tts = new KokoroTtsProvider(new KokoroProviderSettings(Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL")!));
        const string script = "The RMSE of the SVR forecasts of irradiance from NWP predictors is low.";
        var speech = await tts.SynthesizeAsync(new TtsSynthesisRequest(script, "af_heart", "en-US", Format));

        var result = await aligner.AlignAsync(speech.Pcm, speech.Format, script);

        var tokens = SpokenWord.Tokenize(script);
        Assert.Equal(tokens.Count, result.Words.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            var w = result.Words[i];
            Assert.Equal(tokens[i].Range, w.SpokenRange);
            Assert.InRange(w.EndSample, w.StartSample + 1, speech.FrameCount);
            if (i > 0) Assert.True(w.StartSample >= result.Words[i - 1].EndSample);
            output.WriteLine($"{tokens[i].Text,-12} {speech.Format.ToSeconds(w.StartSample):F3}–{speech.Format.ToSeconds(w.EndSample):F3}s");
        }
        Assert.Contains(result.Warnings, n => n.Contains("rmse", StringComparison.Ordinal) && n.Contains("letters", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, n => n.Contains("irradiance", StringComparison.Ordinal) && n.Contains("generated", StringComparison.Ordinal));
        foreach (string note in result.Warnings) output.WriteLine(note);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledOrTimedOutWorkerIsReapedAndTheNextRequestRestarts(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "rn-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string python = Path.Combine(root, "python");
            await File.WriteAllTextAsync(python, $$$$"""
                #!/bin/sh
                printf '{"ready":true}\n'
                while read -r request; do
                  if [ ! -f '{{{{root}}}}/attempted' ]; then
                    touch '{{{{root}}}}/attempted'
                    sleep 30
                  fi
                  printf '{"tiers":{"words":{"entries":[[0.1,0.5,"go"]]}}}\n'
                done
                """);
            File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string mfa = Path.Combine(root, "mfa"), dictionary = Path.Combine(root, "dictionary"), model = Path.Combine(root, "model");
            foreach (var file in new[] { mfa, dictionary, model }) await File.WriteAllTextAsync(file, "test");
            await using var aligner = new MfaForcedAligner(new MfaAlignerSettings(mfa, dictionary, model, root, TimeoutSeconds: cancel ? 10 : 1));
            using var cts = new CancellationTokenSource();
            var request = aligner.AlignAsync(new byte[24000 * 4], Format, "Go.", cts.Token);
            if (cancel)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!File.Exists(Path.Combine(root, "attempted"))) await Task.Delay(10, deadline.Token);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            }
            else await Assert.ThrowsAsync<TimeoutException>(() => request);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "requests")));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "workers")));
            var recovered = await aligner.AlignAsync(new byte[24000 * 4], Format, "Go.");
            Assert.Single(recovered.Words);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed class MfaIntegrationFactAttribute : FactAttribute
{
    public MfaIntegrationFactAttribute()
    {
        if (new[] { "RN_MFA_EXECUTABLE", "RN_MFA_DICTIONARY", "RN_MFA_MODEL", "RAIL_NARRATOR_KOKORO_MODEL" }
            .Any(v => !File.Exists(Environment.GetEnvironmentVariable(v))))
            Skip = "Set RN_MFA_EXECUTABLE, RN_MFA_DICTIONARY, RN_MFA_MODEL and RAIL_NARRATOR_KOKORO_MODEL for real speech alignment.";
    }
}

public sealed class MfaG2pIntegrationFactAttribute : FactAttribute
{
    public MfaG2pIntegrationFactAttribute()
    {
        if (new[] { "RN_MFA_EXECUTABLE", "RN_MFA_DICTIONARY", "RN_MFA_MODEL", "RN_MFA_G2P", "RAIL_NARRATOR_KOKORO_MODEL" }
            .Any(v => !File.Exists(Environment.GetEnvironmentVariable(v))))
            Skip = "Set RN_MFA_EXECUTABLE, RN_MFA_DICTIONARY, RN_MFA_MODEL, RN_MFA_G2P and RAIL_NARRATOR_KOKORO_MODEL for real speech alignment with generated pronunciations.";
    }
}
