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
