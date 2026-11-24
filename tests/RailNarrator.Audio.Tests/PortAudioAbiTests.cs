using System.Runtime.InteropServices;
using Xunit;

namespace RailNarrator.Audio.Tests;

/// <summary>
/// PortAudio's structures as the application declares them, against their C layout. Each holds on
/// Linux and on Windows: C <c>unsigned long</c> is eight bytes on one and four on the other, and
/// <see cref="CULong"/> follows it, so these sizes are the ones both compilers give PortAudio.
/// </summary>
public sealed class PortAudioAbiTests
{
    [Fact]
    public void StreamParameters_HasTheCLayout()
    {
        // int device, int channelCount, unsigned long sampleFormat (padded to 8 on Windows),
        // double suggestedLatency, void* hostApiSpecificStreamInfo.
        Assert.Equal(32, Marshal.SizeOf<PortAudioNative.StreamParameters>());
        Assert.Equal(8, (int)Marshal.OffsetOf<PortAudioNative.StreamParameters>(nameof(PortAudioNative.StreamParameters.sampleFormat)));
        Assert.Equal(16, (int)Marshal.OffsetOf<PortAudioNative.StreamParameters>(nameof(PortAudioNative.StreamParameters.suggestedLatency)));
        Assert.Equal(24, (int)Marshal.OffsetOf<PortAudioNative.StreamParameters>(nameof(PortAudioNative.StreamParameters.hostApiSpecificStreamInfo)));
    }

    [Fact]
    public void SampleFormat_IsTheWidthOfACLong()
        => Assert.Equal(OperatingSystem.IsWindows() ? 4 : IntPtr.Size, Marshal.SizeOf<CULong>());

    [Fact]
    public void WasapiStreamInfo_IsTheSizePortAudioChecks()
    {
        // PortAudio v19.7.0 refuses the structure unless size == sizeof(PaWasapiStreamInfo), which
        // MSVC makes 56 on x64: five 4-byte fields (20, padded to 24), two pointers (to 40), three
        // 4-byte enums (to 52, padded to 56).
        Assert.Equal(56, Marshal.SizeOf<PortAudioNative.WasapiStreamInfo>());
        Assert.Equal(24, (int)Marshal.OffsetOf<PortAudioNative.WasapiStreamInfo>(nameof(PortAudioNative.WasapiStreamInfo.hostProcessorOutput)));
        Assert.Equal(40, (int)Marshal.OffsetOf<PortAudioNative.WasapiStreamInfo>(nameof(PortAudioNative.WasapiStreamInfo.threadPriority)));
    }

    [Fact]
    public void HostApiInfo_HasTheCLayout()
    {
        // int structVersion, int type, const char* name, int deviceCount, int defaultInputDevice,
        // int defaultOutputDevice (+ padding).
        Assert.Equal(32, Marshal.SizeOf<PortAudioNative.HostApiInfo>());
        Assert.Equal(24, (int)Marshal.OffsetOf<PortAudioNative.HostApiInfo>(nameof(PortAudioNative.HostApiInfo.defaultOutputDevice)));
    }

    [Fact]
    public void TheLibraryIsLookedForUnderThisPlatformsNames()
    {
        var names = PortAudioNative.CandidateNames();
        if (OperatingSystem.IsWindows())
            Assert.Equal("portaudio.dll", names[0]);
        else if (OperatingSystem.IsLinux())
            Assert.Equal("libportaudio.so.2", names[0]);
        Assert.All(names, n => Assert.Contains("portaudio", n, StringComparison.Ordinal));
    }
}
