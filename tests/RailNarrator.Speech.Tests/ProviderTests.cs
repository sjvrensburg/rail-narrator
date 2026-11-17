using System.Net;
using System.Text;
using System.Text.Json;
using RailNarrator.Domain;
using RailNarrator.Speech;
using Xunit;

namespace RailNarrator.Speech.Tests;

/// <summary>
/// PCM conversion and the WAV codec. A format mistake here is not subtle: it is noise, or a clip
/// whose length is wrong and therefore cues that are all in the wrong place.
/// </summary>
public sealed class PcmCodecTests
{
    [Fact]
    public void Float32ToInt16_RoundTripsWithinQuantisationError()
    {
        var format = AudioFormat.Mono24000Float32;
        var samples = new float[] { 0f, 0.5f, -0.5f, 1f, -1f, 0.123f };
        var buffer = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4, 4), samples[i]);

        var converted = Pcm.ConvertTo(buffer, format, SampleFormat.Int16LittleEndian);
        Assert.Equal(samples.Length * 2, converted.Length);

        for (int i = 0; i < samples.Length; i++)
        {
            float back = Pcm.ReadSample(converted, i * 2, SampleFormat.Int16LittleEndian);
            Assert.True(Math.Abs(back - samples[i]) < 1f / 32767,
                $"sample {i}: {samples[i]} became {back}");
        }
    }

    [Fact]
    public void Conversion_ClipsOutOfRangeFloats()
    {
        var buffer = new byte[8];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 2.5f);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), -2.5f);

        var converted = Pcm.ConvertTo(buffer, AudioFormat.Mono24000Float32, SampleFormat.Int16LittleEndian);
        // Read back as float: full scale maps to 32767/32768, i.e. 1.0 to within a quantisation step.
        Assert.True(Math.Abs(Pcm.ReadSample(converted, 0, SampleFormat.Int16LittleEndian) - 1f) < 1e-4f);
        Assert.True(Math.Abs(Pcm.ReadSample(converted, 2, SampleFormat.Int16LittleEndian) + 1f) < 1e-4f);
    }

    [Fact]
    public void Conversion_RejectsABufferThatIsNotAWholeNumberOfFrames()
    {
        // Guessing here would silently shift every sample after the first.
        Assert.Throws<ArgumentException>(
            () => Pcm.ConvertTo(new byte[7], AudioFormat.Mono24000Float32, SampleFormat.Int16LittleEndian));
    }

    [Fact]
    public void PeakAndRms_DistinguishSpeechFromSilence()
    {
        var silent = new byte[4000];
        Assert.Equal(0f, Pcm.Peak(silent, AudioFormat.Mono24000Float32));

        var tone = SyntheticTone(2400, 24000, 0.3);
        Assert.True(Pcm.Peak(tone, AudioFormat.Mono24000Float32) > 0.2);
        Assert.True(Pcm.Rms(tone, AudioFormat.Mono24000Float32) > 0.1);
    }

    [Fact]
    public void SilenceTrimming_RemovesLeadingSilenceAndReportsHowMuch()
    {
        int rate = 24000;
        var silence = new byte[rate / 10 * 4];                       // 100 ms of zeros
        var tone = SyntheticTone(rate / 2, rate, 0.3);              // 500 ms of tone
        var buffer = silence.Concat(tone).ToArray();

        var (trimmed, leadingFrames) = Pcm.TrimLeadingSilence(buffer, AudioFormat.Mono24000Float32);

        // The tone's first sample is sin(0) = 0, which is itself below the silence threshold, so the
        // trim may take one extra frame. That is correct behaviour, not an off-by-one.
        Assert.InRange(leadingFrames, rate / 10, rate / 10 + 4);
        Assert.Equal(buffer.Length - leadingFrames * 4, trimmed.Length);
        // The clip's first frame is now the start of speech, which is what makes a clip-boundary cue
        // line up with what the listener hears.
        Assert.True(Math.Abs(Pcm.ReadSample(trimmed, 0, SampleFormat.Float32LittleEndian)) > 0.001f);
    }

    [Fact]
    public void WavRoundTrip_PreservesFormatAndSamples()
    {
        var format = AudioFormat.Mono24000Float32;
        var pcm = SyntheticTone(4800, 24000, 0.25);

        var wav = WavCodec.Write(format, pcm);
        var parsed = WavCodec.Parse(wav);

        Assert.Equal(format, parsed.Format);
        Assert.Equal(4800, parsed.FrameCount);
        Assert.Equal(pcm, parsed.Pcm);
    }

    [Fact]
    public void WavRoundTrip_HandlesInt16AndStereo()
    {
        var format = new AudioFormat(44100, 2, SampleFormat.Int16LittleEndian);
        var pcm = new byte[4410 * 4];
        Random.Shared.NextBytes(pcm);

        var parsed = WavCodec.Parse(WavCodec.Write(format, pcm));
        Assert.Equal(format, parsed.Format);
        Assert.Equal(4410, parsed.FrameCount);
        Assert.Equal(pcm, parsed.Pcm);
    }

    [Fact]
    public void WavParsing_SkipsUnknownChunksBeforeData()
    {
        // A LIST or fact chunk before data is common. Assuming a fixed 44-byte header would read
        // metadata as audio and produce noise that still "plays".
        var format = AudioFormat.Mono44100Float32;
        var pcm = SyntheticTone(4410, 44100, 0.2);
        var plain = WavCodec.Write(format, pcm);

        var withList = new byte[plain.Length + 16];
        Array.Copy(plain, 0, withList, 0, 36);
        Encoding.ASCII.GetBytes("LIST").CopyTo(withList, 36);
        BitConverter.GetBytes(4).CopyTo(withList, 40);
        Encoding.ASCII.GetBytes("INFO").CopyTo(withList, 44);
        Array.Copy(plain, 36, withList, 48, plain.Length - 36);
        // Fix the RIFF size for the inserted chunk.
        BitConverter.GetBytes(plain.Length - 8 + 12).CopyTo(withList, 4);

        var parsed = WavCodec.Parse(withList);
        Assert.Equal(format, parsed.Format);
        Assert.Equal(pcm, parsed.Pcm);
    }

    [Fact]
    public void WavParsing_RejectsAnUnsupportedCodecInsteadOfGuessing()
    {
        var wav = WavCodec.Write(AudioFormat.Mono44100Float32, SyntheticTone(4410, 44100, 0.2));
        // Rewrite the format tag to something this reader cannot represent exactly.
        BitConverter.GetBytes((ushort)0x0055).CopyTo(wav, 20);   // MPEG
        Assert.Throws<InvalidDataException>(() => WavCodec.Parse(wav));
    }

    [Fact]
    public void WavParsing_RejectsTruncatedAndNonWaveInput()
    {
        Assert.Throws<InvalidDataException>(() => WavCodec.Parse(new byte[10]));
        Assert.Throws<InvalidDataException>(() => WavCodec.Parse(Encoding.ASCII.GetBytes("not a wav file at all, definitely not")));
    }

    internal static byte[] SyntheticTone(int frames, int sampleRate, double amplitude)
    {
        var buffer = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4, 4),
                (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / sampleRate)));
        return buffer;
    }
}

/// <summary>
/// The provider contract: capabilities are discovered and reported truthfully, never assumed.
/// </summary>
public sealed class ProviderContractTests
{
    [Fact]
    public async Task Capabilities_ReportUnavailableAsDataNotAsAnException()
    {
        // A missing model or an unstarted sidecar must be diagnosable from the UI, not a crash.
        var tts = new KokoroTtsProvider(new KokoroProviderSettings("/nonexistent/model.onnx"));
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.False(caps.IsAvailable);
            Assert.NotNull(caps.Unavailable);
            Assert.Contains("not found", caps.Unavailable, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await tts.ListVoicesAsync());
        }
    }

    [Fact]
    public async Task Kokoro_RefusesARateItDoesNotProduceRatherThanResamplingSilently()
    {
        // Resampling belongs to the audio engine, where the frame mapping is tracked. A provider that
        // quietly returned a different rate would shift every cue in time.
        string? model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL");
        if (model is null || !File.Exists(model)) return;   // skipped without the model present

        var tts = new KokoroTtsProvider(new KokoroProviderSettings(model));
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.True(caps.IsAvailable);
            Assert.DoesNotContain(caps.Formats, f => f.SampleRate != KokoroTtsProvider.OutputSampleRate);

            var voices = await tts.ListVoicesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tts.SynthesizeAsync(
                new TtsSynthesisRequest("Hello.", voices[0].Id, "en-US", new AudioFormat(44100, 1, SampleFormat.Float32LittleEndian))));
        }
    }

    [Fact]
    public async Task Kokoro_RejectsANonModelFileAsUnavailableRatherThanThrowing()
    {
        // Capability probing must never throw: a missing or corrupt model is a diagnosable state the
        // UI explains, not a crash at startup.
        string notAModel = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(notAModel, "this is not an ONNX model");
        try
        {
            var tts = new KokoroTtsProvider(new KokoroProviderSettings(notAModel));
            await using (tts)
            {
                var caps = await tts.GetCapabilitiesAsync();
                Assert.False(caps.IsAvailable);
                Assert.Contains("Could not open the Kokoro model", caps.Unavailable);
            }
        }
        finally { File.Delete(notAModel); }
    }

    [Fact]
    public async Task Kokoro_RejectsAnIncompatibleExportInsteadOfCrashingTheProcess()
    {
        // Regression guard for a crash that was actually observed. KokoroSharp runs inference on its
        // own dispatch thread and does not catch ONNX errors, so an export whose inputs are named
        // differently (kokoro-onnx uses 'input_ids', KokoroSharp feeds 'tokens') aborts the whole
        // process on first synthesis. The provider now probes the graph first.
        //
        // Needs the real 325 MB file, so it is skipped unless the path is supplied; the assertion is
        // that we get a recoverable 'unavailable' naming the tensors, never a crash.
        string? model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_INCOMPATIBLE_MODEL");
        if (model is null || !File.Exists(model)) return;

        var tts = new KokoroTtsProvider(new KokoroProviderSettings(model));
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.False(caps.IsAvailable);
            Assert.Contains("not a KokoroSharp-compatible Kokoro export", caps.Unavailable);
            Assert.Contains("tokens", caps.Unavailable);
        }
    }

    [Fact]
    public async Task Kokoro_ReportsDurationsCapabilityFromTheModelGraph()
    {
        // Word-level highlighting is capability-gated, so the capability has to come from evidence
        // about the loaded model rather than from an assumption about Kokoro in general.
        string? model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL");
        if (model is null || !File.Exists(model)) return;

        var tts = new KokoroTtsProvider(new KokoroProviderSettings(model));
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.True(caps.IsAvailable, caps.Unavailable);
            // kokoro-fp16.onnx has no durations output, so no word timings may be claimed.
            Assert.False(caps.CanSupplyWordTiming); // Phoneme durations lack script-to-word correspondence.
            if (!tts.ModelReportsDurations) Assert.Equal(TimingProvenance.None, caps.NativeTiming);
        }
    }

    [Fact]
    public async Task FakeProvider_IsDeterministicForTheSameInput()
    {
        var tts = new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: 24000));
        await using (tts)
        {
            var a = await tts.SynthesizeAsync(new TtsSynthesisRequest("Same text.", "fake-default", "en-GB", AudioFormat.Mono24000Float32));
            var b = await tts.SynthesizeAsync(new TtsSynthesisRequest("Same text.", "fake-default", "en-GB", AudioFormat.Mono24000Float32));

            Assert.Equal(a.FrameCount, b.FrameCount);
            Assert.True(a.Pcm.Span.SequenceEqual(b.Pcm.Span), "identical input must produce identical audio for tests to be reproducible");
        }
    }

    [Fact]
    public async Task FakeProvider_ScalesDurationWithTextLength()
    {
        var tts = new FakeTtsProvider();
        await using (tts)
        {
            var shortClip = await tts.SynthesizeAsync(new TtsSynthesisRequest("Hi.", "v", "en-GB", AudioFormat.Mono24000Float32));
            var longClip = await tts.SynthesizeAsync(new TtsSynthesisRequest(
                "A considerably longer sentence that should take more time to speak aloud.", "v", "en-GB", AudioFormat.Mono24000Float32));
            Assert.True(longClip.DurationSeconds > shortClip.DurationSeconds);
        }
    }

    [Fact]
    public async Task Registry_ListsOnlyProvidersThatAreActuallyImplemented()
    {
        var registry = new SpeechProviderRegistry().Register(new FakeTtsProvider());
        Assert.Equal([SpeechProviderIds.Fake], registry.RegisteredIds);
        Assert.NotNull(registry.Get(SpeechProviderIds.Fake));
        Assert.Null(registry.Get("audio8"));      // not implemented, so not offered
        Assert.True(SpeechProviderIds.IsKnown(SpeechProviderIds.Kokoro));
        Assert.True(SpeechProviderIds.IsKnown(SpeechProviderIds.Zonos));

        var first = await registry.FirstAvailableAsync();
        Assert.Equal(SpeechProviderIds.Fake, first?.ProviderId);
    }

    [Fact]
    public async Task Registry_SkipsUnavailableProvidersWhenChoosingADefault()
    {
        var registry = new SpeechProviderRegistry()
            .Register(new KokoroTtsProvider(new KokoroProviderSettings("/nonexistent.onnx")))
            .Register(new FakeTtsProvider());

        var chosen = await registry.FirstAvailableAsync();
        Assert.Equal(SpeechProviderIds.Fake, chosen?.ProviderId);

        var probes = await registry.ProbeAllAsync();
        Assert.Equal(2, probes.Count);
        Assert.Contains(probes, p => p.ProviderId == SpeechProviderIds.Kokoro && !p.Capabilities.IsAvailable);
    }
}

/// <summary>
/// The ZONOS3 sidecar client, exercised against a stubbed HTTP transport so the contract is verified
/// without a running server. The same contract was verified live during development; see ADR-0002.
/// </summary>
public sealed class ZonosProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(string Path, string? Body)> Requests { get; } = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            return _respond(request);
        }
    }

    private static HttpResponseMessage SilentAudio(byte[] pcm)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };
        response.Headers.TryAddWithoutValidation("X-Audio-Sample-Rate", "44100");
        response.Headers.TryAddWithoutValidation("X-Audio-Channels", "1");
        response.Headers.TryAddWithoutValidation("X-Audio-Format", "float32");
        return response;
    }

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Capabilities_ComeFromTheServerNotFromAssumption()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/health" => Json(new { status = "ok" }),
            "/v2/capabilities" => Json(new { audio_codec = "opus_dual_test", edit_transcribe = false }),
            "/v2/speakers" => Json(new[] { new { id = "default_abc", label = "Imogen en British F 1", is_default = true } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(), new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.True(caps.IsAvailable);
            Assert.Contains("opus_dual_test", caps.ModelRevision);
            // No aligner in a synthesis-only install, so no word timings may be claimed.
            Assert.Equal(TimingProvenance.None, caps.NativeTiming);
            Assert.False(caps.CanSupplyWordTiming);

            var voices = await tts.ListVoicesAsync();
            var imogen = Assert.Single(voices);
            Assert.Equal("default_abc", imogen.Id);
            Assert.True(imogen.IsDefault);
            Assert.Equal("en", imogen.Language);
            Assert.Equal("female", imogen.Gender);
        }
    }

    [Fact]
    public async Task UnreachableSidecar_IsReportedAsUnavailableWithTheReason()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(), new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.False(caps.IsAvailable);
            Assert.Contains("connection refused", caps.Unavailable);
        }
    }

    [Fact]
    public async Task Synthesis_ReadsItsFormatFromTheResponseHeaders()
    {
        // The headers are the authority on what came back. Trusting the request instead would
        // silently mistime every cue if the server's decoder or rate differed.
        var pcm = PcmCodecTests.SyntheticTone(4410, 44100, 0.3);
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/pcm");
            response.Headers.TryAddWithoutValidation("X-Audio-Sample-Rate", "44100");
            response.Headers.TryAddWithoutValidation("X-Audio-Channels", "1");
            response.Headers.TryAddWithoutValidation("X-Audio-Format", "float32");
            response.Headers.TryAddWithoutValidation("X-Seed", "7");
            response.Headers.TryAddWithoutValidation("X-Decoder", "full_mr");
            return response;
        });

        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(SpeakerEmbeddingId: "default_abc"),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var result = await tts.SynthesizeAsync(new TtsSynthesisRequest(
                "Probability of A given B.", "default_abc", "en-GB", AudioFormat.Mono44100Float32));

            Assert.Equal(44100, result.Format.SampleRate);
            Assert.Equal(SampleFormat.Float32LittleEndian, result.Format.SampleFormat);
            // One frame shorter than sent: the provider trims leading silence, and the tone's first
            // sample is exactly zero. The format metadata is what this test is really about.
            Assert.InRange(result.FrameCount, 4405, 4410);
            Assert.Equal(7L, result.SeedUsed);
            Assert.Equal("full_mr", result.DecoderId);
            Assert.Empty(result.Timings);
            Assert.Equal(TimingProvenance.None, result.TimingProvenance);

            // The request body must carry the operator-tuned settings and buffered PCM, not streaming.
            var body = Assert.Single(handler.Requests, r => r.Path == "/v2/generate").Body!;
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("pcm", doc.RootElement.GetProperty("format").GetString());
            Assert.Equal("default_abc", doc.RootElement.GetProperty("speaker_embedding_id").GetString());
            Assert.Equal(1.5, doc.RootElement.GetProperty("speaker_cfg_scale").GetDouble(), 3);
            Assert.Equal(-18, doc.RootElement.GetProperty("condition_values").GetProperty("loudness").GetDouble(), 3);
        }
    }

    [Fact]
    public async Task Synthesis_RejectsAnEmptyBodyRatherThanPlayingNothing()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            response.Headers.TryAddWithoutValidation("X-Audio-Sample-Rate", "44100");
            return response;
        });
        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(SpeakerEmbeddingId: "v"),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tts.SynthesizeAsync(
                new TtsSynthesisRequest("text", "v", "en-GB", AudioFormat.Mono44100Float32)));
            Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(400, "rejected")]
    [InlineData(422, "malformed")]
    [InlineData(500, "failed while generating")]
    public async Task HttpFailures_AreExplainedNotSwallowed(int status, string expectedFragment)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"detail\":\"boom\"}", Encoding.UTF8, "application/json"),
        });
        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(SpeakerEmbeddingId: "v"),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tts.SynthesizeAsync(
                new TtsSynthesisRequest("text", "v", "en-GB", AudioFormat.Mono44100Float32)));
            Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("boom", ex.Message);
        }
    }

    [Fact]
    public async Task Synthesis_WithoutAConfiguredSpeaker_FailsWithAClearMessage()
    {
        var handler = new StubHandler(_ => Json(new { status = "ok" }));
        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(SpeakerEmbeddingId: null),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tts.SynthesizeAsync(
                new TtsSynthesisRequest("text", "", "en-GB", AudioFormat.Mono44100Float32)));
            Assert.Contains("speaker", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SelfTest_DetectsASilentDecoder()
    {
        // This is a real observed failure mode, not a hypothetical: on the reference machine the
        // ZONOS3 Vulkan decoder outputs silence (peak ~0.0017) while the CPU path works.
        var silent = new byte[4410 * 4];
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/health" => Json(new { status = "ok" }),
            "/v2/capabilities" => Json(new { audio_codec = "test" }),
            "/v2/speakers" => Json(new[] { new { id = "v", label = "Voice en British F 1", is_default = true } }),
            _ => SilentAudio(silent),
        });
        var tts = new ZonosHttpTtsProvider(new ZonosProviderSettings(SpeakerEmbeddingId: "v"),
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") });
        await using (tts)
        {
            var test = await tts.SelfTestAsync("v");
            Assert.False(test.Succeeded);
            Assert.True(test.IsEffectivelySilent);
            Assert.Contains("silent", test.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }
}
