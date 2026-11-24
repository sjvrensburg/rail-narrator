using RailNarrator.Speech;
using Xunit;

namespace RailNarrator.Speech.Tests;

public sealed class KokoroVoiceLocationTests
{
    /// <summary>
    /// A release carries the voices beside the application and has no NuGet cache. Before they were
    /// looked for there, Kokoro on a reader's machine found no voices at all. The test's own output
    /// folder has them (KokoroSharp's build target copies them), as a release does.
    /// </summary>
    [Fact]
    public void TheVoicesBesideTheApplicationAreUsedBeforeThePackageCache()
    {
        string? found = KokoroProviderSettings.LocatePackageVoices();
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "voices"), found);
        Assert.NotEmpty(Directory.EnumerateFiles(found!, "*.npy"));
    }
}
