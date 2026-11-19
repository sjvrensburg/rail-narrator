using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RailNarrator.Audio;

/// <summary>
/// Hushes what libasound and libjack print to the terminal while PortAudio initialises.
/// </summary>
/// <remarks>
/// <para>
/// <c>Pa_Initialize</c> probes every host API and every ALSA device configuration the system
/// declares. On a PipeWire desktop with no JACK server that printed some thirty lines at every
/// start: ALSA's "Unknown PCM cards.pcm.rear", "Found no matching channel map", "Cannot open
/// device /dev/dsp", and JACK's "jack server is not running or cannot be started". None is an
/// error for this application; PortAudio reports what matters through its return codes, which
/// <see cref="PortAudioOutputDevice.Initialise"/> turns into a message.
/// </para>
/// <para>
/// Only the probe is silenced. ALSA's own printer is put back afterwards; JACK's has no getter, so
/// JACK messages go through a handler of ours that drops them during the probe and writes them to
/// standard error, as JACK would, at any other time.
/// </para>
/// </remarks>
internal static unsafe class NativeAudioChatter
{
    private static readonly object Gate = new();
    private static int _quiet;
    private static bool _jackHooked;

    /// <summary>Silences ALSA and JACK until the returned scope is disposed.</summary>
    public static IDisposable Quiet()
    {
        lock (Gate)
        {
            if (_quiet++ == 0)
            {
                SetAlsaHandler((nint)(delegate* unmanaged[Cdecl]<nint, int, nint, int, nint, void>)&SilentAlsa);
                HookJack();
            }
        }
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gate)
                if (--_quiet == 0) SetAlsaHandler(0);   // null puts ALSA's default printer back
        }
    }

    private static void SetAlsaHandler(nint handler)
    {
        if (!NativeLibrary.TryLoad("libasound.so.2", out var alsa)) return;
        if (!NativeLibrary.TryGetExport(alsa, "snd_lib_error_set_handler", out var set)) return;
        ((delegate* unmanaged[Cdecl]<nint, int>)set)(handler);
    }

    private static void HookJack()
    {
        if (_jackHooked) return;
        _jackHooked = true;
        if (!NativeLibrary.TryLoad("libjack.so.0", out var jack)) return;
        var handler = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&JackMessage;
        foreach (string name in new[] { "jack_set_error_function", "jack_set_info_function" })
            if (NativeLibrary.TryGetExport(jack, name, out var set))
                ((delegate* unmanaged[Cdecl]<nint, void>)set)(handler);
    }

    /// <summary>
    /// ALSA's handler is variadic (file, line, function, err, fmt, ...). The fixed arguments are
    /// passed as for any C call and the rest are never read, so a five-argument callee is sound.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SilentAlsa(nint file, int line, nint function, int err, nint fmt) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void JackMessage(nint message)
    {
        if (Volatile.Read(ref _quiet) > 0 || message == 0) return;
        try { Console.Error.WriteLine(Marshal.PtrToStringUTF8(message)); }
        catch (Exception) { /* nothing may escape into native code */ }
    }
}
