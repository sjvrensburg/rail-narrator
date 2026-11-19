using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Infrastructure.Tests;

/// <summary>
/// Disk speech-cache behaviour: atomic-enough writes, length-validated reads, and invalidation
/// that must never wipe unrelated documents' audio.
/// </summary>
public sealed class DiskSpeechAssetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rn-storage-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Store_ThenLoad_RoundTripsMetadataAndPcm()
    {
        var store = new DiskSpeechAssetStore(_root);
        var asset = SyntheticNarration.Clip("s0", frames: 2400);
        var pcm = SyntheticNarration.TonePcm(2400);

        await store.StoreAsync(asset, pcm);

        // Field-wise, not record equality: Timings deserialises as a List where the fixture used
        // an array, and record equality compares collections by reference.
        var roundTripped = await store.TryGetAsync(asset.Id);
        Assert.NotNull(roundTripped);
        Assert.Equal(asset.Id, roundTripped.Id);
        Assert.Equal(asset.FrameCount, roundTripped.FrameCount);
        Assert.Equal(asset.Format, roundTripped.Format);
        Assert.Equal(asset.VoiceId, roundTripped.VoiceId);
        var loaded = await store.TryLoadPcmAsync(asset.Id);
        Assert.NotNull(loaded);
        Assert.Equal(pcm, loaded.Value.ToArray());
    }

    [Fact]
    public async Task TryLoadPcm_RejectsAudioWhoseLengthNoLongerMatchesTheMetadata()
    {
        var store = new DiskSpeechAssetStore(_root);
        var asset = SyntheticNarration.Clip("s0", frames: 2400);
        await store.StoreAsync(asset, SyntheticNarration.TonePcm(2400));

        // Simulate a torn/foreign write: the PCM on disk no longer matches the asset's frame count.
        string pcmPath = Directory.EnumerateFiles(_root, "audio.pcm", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(pcmPath, SyntheticNarration.TonePcm(1200));

        Assert.Null(await store.TryGetAsync(asset.Id));
        Assert.Null(await store.TryLoadPcmAsync(asset.Id));
    }

    [Fact]
    public async Task TryLoadPcm_RejectsSameLengthReplacementWhenTimingsReferToAnotherWaveform()
    {
        var store = new DiskSpeechAssetStore(_root);
        var pcm = SyntheticNarration.TonePcm(2400);
        var asset = SyntheticNarration.Clip("s0", frames: 2400) with { PcmHash = CacheKey.Sha256Hex(pcm) };
        await store.StoreAsync(asset, pcm);
        string pcmPath = Directory.EnumerateFiles(_root, "audio.pcm", SearchOption.AllDirectories).Single();
        pcm[0] ^= 1;
        await File.WriteAllBytesAsync(pcmPath, pcm);
        Assert.Null(await store.TryLoadPcmAsync(asset.Id));
    }

    [Fact]
    public async Task Invalidate_NeverDeletesCachedSpeech()
    {
        // Cache keys are content-addressed (script revision + spoken text), so an edited segment
        // addresses a new asset. Invalidation previously deleted every asset of every document.
        var store = new DiskSpeechAssetStore(_root);
        var a = SyntheticNarration.Clip("s0", frames: 2400);
        var b = SyntheticNarration.Clip("s1", frames: 2400);
        await store.StoreAsync(a, SyntheticNarration.TonePcm(2400));
        await store.StoreAsync(b, SyntheticNarration.TonePcm(2400));

        await store.InvalidateAsync("some-document-hash");

        Assert.NotNull(await store.TryGetAsync(a.Id));
        Assert.NotNull(await store.TryGetAsync(b.Id));
        Assert.NotNull(await store.TryLoadPcmAsync(a.Id));
    }
}

/// <summary>
/// The two old follow modes became one. A settings file naming either must load as following, not
/// fail and reset every other preference to its default.
/// </summary>
public sealed class SettingsStoreFollowModeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rn-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("KeepVisible", FollowMode.Follow)]
    [InlineData("ReadingRail", FollowMode.Follow)]
    [InlineData("Follow", FollowMode.Follow)]
    [InlineData("Off", FollowMode.Off)]
    public void AnOldFollowModeName_StillLoads_AndTheOtherSettingsSurvive(string saved, FollowMode expected)
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""{ "FollowMode": "{{saved}}", "PlaybackRate": 1.5 }""");
        var store = new RailNarrator.Infrastructure.SettingsStore(path);

        var loaded = store.Load();

        Assert.Null(store.LastError);
        Assert.Equal(expected, loaded.FollowMode);
        Assert.Equal(1.5, loaded.PlaybackRate);
    }

    [Fact]
    public void FollowMode_IsSavedByName()
    {
        string path = Path.Combine(_dir, "settings.json");
        var store = new RailNarrator.Infrastructure.SettingsStore(path);
        store.Save(RailNarrator.Infrastructure.RailNarratorSettings.Default with { FollowMode = FollowMode.Off });
        Assert.Contains("\"FollowMode\": \"Off\"", File.ReadAllText(path));
        Assert.Equal(FollowMode.Off, store.Load().FollowMode);
    }

    [Fact]
    public void TheMathsProviders_AreOpenAiCompatibleUnlessChosen_AndSavedByName()
    {
        string path = Path.Combine(_dir, "settings.json");
        var store = new RailNarrator.Infrastructure.SettingsStore(path);
        // A settings file from before the choice existed keeps its meaning.
        File.WriteAllText(path, """{ "MathLlmBaseUrl": "http://127.0.0.1:8088/v1", "MathLlmModel": "m" }""");
        Assert.Equal(RailNarrator.Infrastructure.LlmProvider.OpenAiCompatible, store.Load().MathLlmProvider);
        Assert.Equal(RailNarrator.Infrastructure.LlmProvider.OpenAiCompatible, store.Load().VlmProvider);

        store.Save(store.Load() with { MathLlmProvider = RailNarrator.Infrastructure.LlmProvider.Anthropic });
        Assert.Contains("\"MathLlmProvider\": \"Anthropic\"", File.ReadAllText(path));
        Assert.Equal(RailNarrator.Infrastructure.LlmProvider.Anthropic, store.Load().MathLlmProvider);
        Assert.Equal(RailNarrator.Infrastructure.LlmProvider.OpenAiCompatible, store.Load().VlmProvider);
    }
}

/// <summary>Which layout model runs, and on which file per device (ADR-0008).</summary>
public sealed class LayoutModelResolverTests : IDisposable
{
    private readonly string _app = Directory.CreateTempSubdirectory("rn-models-").FullName;
    private readonly string _rr2 = Directory.CreateTempSubdirectory("rr2-models-").FullName;

    public void Dispose()
    {
        Directory.Delete(_app, recursive: true);
        Directory.Delete(_rr2, recursive: true);
    }

    private string Put(string dir, string file) { string p = Path.Combine(dir, file); File.WriteAllText(p, "x"); return p; }

    private LayoutModelChoice? Resolve(string? setting, string? explicitPath = null)
        => LayoutModelResolver.Resolve(setting, explicitPath, [_app, _rr2]);

    private const string HeronFp32 = "docling-layout-heron.onnx", HeronInt8 = "docling-layout-heron-int8.onnx", V3 = "PP-DocLayoutV3.onnx";

    [Theory]
    [InlineData("auto")]
    [InlineData("text-layout")]   // the old default that settings files still carry
    [InlineData(null)]
    public void Automatic_PrefersHeron_FP32OnTheGpu_Int8OnTheCpu(string? setting)
    {
        string fp32 = Put(_rr2, HeronFp32), int8 = Put(_rr2, HeronInt8);
        Put(_app, V3);

        var choice = Resolve(setting);

        Assert.Equal(new LayoutModelChoice("heron", fp32, int8), choice);
    }

    [Fact]
    public void Automatic_FallsBackToPPDocLayoutV3_ThenToNone()
    {
        Assert.Null(Resolve("auto"));
        string v3 = Put(_app, V3);
        Assert.Equal(new LayoutModelChoice("pp-doclayout-v3", v3, v3), Resolve("auto"));
    }

    [Fact]
    public void NamingAModel_UsesOnlyThatModel()
    {
        Put(_app, HeronInt8);
        Assert.Null(Resolve("pp-doclayout-v3"));
        string v3 = Put(_app, V3);
        Assert.Equal("pp-doclayout-v3", Resolve("pp-doclayout-v3")!.Id);
        Assert.Equal("heron", Resolve("heron")!.Id);
        Assert.Null(Resolve("none"));
        _ = v3;
    }

    [Fact]
    public void HeronWithOnlyOneFile_UsesWhatIsThere()
    {
        string int8 = Put(_app, HeronInt8);
        Assert.Equal(new LayoutModelChoice("heron", null, int8), Resolve("heron"));

        File.Delete(int8);
        string fp32 = Put(_app, HeronFp32);
        Assert.Equal(new LayoutModelChoice("heron", fp32, fp32), Resolve("heron"));
    }

    [Fact]
    public void TheApplicationsModelsComeBeforeRailReader2s()
    {
        string mine = Put(_app, HeronInt8);
        Put(_rr2, HeronInt8);
        Assert.Equal(mine, Resolve("heron")!.CpuModelPath);
    }

    [Fact]
    public void AnExplicitFile_IsUsedOnBothDevices_ItsArchitectureFromTheSettingOrItsName()
    {
        string heron = Put(_app, "my-heron-export.onnx");
        string other = Put(_app, "layout.onnx");
        Assert.Equal(new LayoutModelChoice("heron", heron, heron), Resolve("auto", heron));
        Assert.Equal("pp-doclayout-v3", Resolve("auto", other)!.Id);
        Assert.Equal("heron", Resolve("heron", other)!.Id);
        Assert.Null(Resolve("auto", Path.Combine(_app, "missing.onnx")));
    }
}

/// <summary>
/// An API key may be typed into the Settings dialogue and stored in the settings file, so the file
/// is readable by its owner only, and a stored key wins over the environment variable.
/// </summary>
public sealed class SettingsStoreKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rn-settings-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void TheSettingsFile_IsReadableByItsOwnerOnly_EvenIfItWasNot()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var store = new RailNarrator.Infrastructure.SettingsStore(path);
        store.Save(RailNarrator.Infrastructure.RailNarratorSettings.Default with { MathLlmApiKey = "sk-test" });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("sk-test", store.Load().MathLlmApiKey);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AStoredKey_WinsOverTheEnvironmentVariable_WhichIsUsedOtherwise()
    {
        string variable = "RN_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "from-environment");
        try
        {
            var settings = RailNarrator.Infrastructure.RailNarratorSettings.Default with { MathLlmApiKeyEnvVar = variable };
            Assert.Equal("from-environment", settings.ResolveMathApiKey());
            Assert.Equal("typed", (settings with { MathLlmApiKey = " typed " }).ResolveMathApiKey());
            Assert.Equal("from-environment", (settings with { MathLlmApiKey = "  " }).ResolveMathApiKey());
            Assert.Null((settings with { MathLlmApiKeyEnvVar = variable + "_UNSET" }).ResolveMathApiKey());
            Assert.Null(RailNarrator.Infrastructure.RailNarratorSettings.Default.ResolveVlmApiKey());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
}

/// <summary>The welcome screen's recent documents: newest first, bounded, progress kept in place.</summary>
public sealed class RecentDocumentsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rn-recent-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void Opening_MovesADocumentToTheFront_AndProgressDoesNotReorder()
    {
        var store = new RailNarrator.Infrastructure.RecentDocumentsStore(_dir);
        var t = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        store.Opened("/papers/a.pdf", 10, t);
        store.Opened("/papers/b.pdf", 20, t.AddMinutes(1));
        store.Progressed("/papers/a.pdf", 0.5);
        var list = store.Load();
        Assert.Equal(["/papers/b.pdf", "/papers/a.pdf"], list.Select(d => d.Path));
        Assert.Equal(0.5, list[1].Progress);

        store.Opened("/papers/a.pdf", 0, t.AddMinutes(2));   // page count unknown this time: kept
        list = new RailNarrator.Infrastructure.RecentDocumentsStore(_dir).Load();
        Assert.Equal("/papers/a.pdf", list[0].Path);
        Assert.Equal(10, list[0].PageCount);
        Assert.Equal(0.5, list[0].Progress);
        Assert.Equal("a.pdf", list[0].Name);
    }

    [Fact]
    public void TheListIsBounded_RemovingForgetsADocument_AndADamagedFileIsAnEmptyList()
    {
        var store = new RailNarrator.Infrastructure.RecentDocumentsStore(_dir);
        var t = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 20; i++) store.Opened($"/papers/{i}.pdf", 1, t.AddMinutes(i));
        Assert.Equal(RailNarrator.Infrastructure.RecentDocumentsStore.Capacity, store.Load().Count);
        Assert.Equal("/papers/19.pdf", store.Load()[0].Path);

        store.Remove("/papers/19.pdf");
        Assert.DoesNotContain(store.Load(), d => d.Path == "/papers/19.pdf");
        store.Progressed("/papers/never-opened.pdf", 0.3);   // unknown: ignored, not added
        Assert.DoesNotContain(store.Load(), d => d.Path == "/papers/never-opened.pdf");

        File.WriteAllText(Path.Combine(_dir, "recent.json"), "{ not json");
        Assert.Empty(store.Load());
        Assert.NotEqual(store.ThumbnailPath("/papers/a.pdf"), store.ThumbnailPath("/papers/b.pdf"));
    }
}
