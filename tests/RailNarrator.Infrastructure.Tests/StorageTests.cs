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
