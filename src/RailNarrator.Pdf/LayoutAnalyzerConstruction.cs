using RailReader.Core.Analysis.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailNarrator.Pdf;

/// <summary>Which device a layout backend runs its model on, and why when it is not the one asked for.</summary>
/// <param name="Id">Stable identifier (<c>cpu</c> or <c>webgpu</c>), folded into the extraction cache key.</param>
/// <param name="Description">For people: "CPU" or the GPU's name.</param>
/// <param name="FallbackReason">Why the GPU was asked for but not used, or null.</param>
public sealed record LayoutDevice(string Id, string Description, string? FallbackReason = null)
{
    public static LayoutDevice Cpu { get; } = new("cpu", "CPU");

    public bool IsGpu => Id == "webgpu";
}

/// <summary>
/// Builds PP-DocLayoutV3 analyzers on the CPU or on a GPU through RailReaderCore's WebGPU plugin.
/// </summary>
/// <remarks>
/// <para>
/// Core applies the GPU through <see cref="LayoutAnalyzer.ConfigureSession"/>, process-wide static
/// state. Every construction, a CPU one included (it relies on the hook being null), therefore holds
/// <see cref="WebGpuAccelerator.ConstructionLock"/> for the whole enable → construct → disable
/// sequence, as Core's documentation requires. The hook is never left installed outside the lock.
/// </para>
/// <para>
/// OCR stays on the CPU: Core measured the PP-OCRv5 Latin tier the app uses as slower on a GPU than
/// on a CPU.
/// </para>
/// </remarks>
internal static class LayoutAnalyzerConstruction
{
    private const LayoutModelArchitecture Architecture = LayoutModelArchitecture.PPDocLayoutV3;

    /// <summary>
    /// An exception as one short line for a person. ONNX Runtime's messages lead with source paths
    /// and nest provider errors, and end with the part that says what went wrong ("Failed to get a
    /// WebGPU adapter: No supported adapters"), so a long message keeps its tail.
    /// </summary>
    internal static string Brief(Exception ex)
    {
        const int Limit = 160;
        string message = ex.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= Limit ? message : "…" + message[^Limit..].TrimStart();
    }

    public static LayoutAnalyzer OnCpu(string modelPath)
    {
        lock (WebGpuAccelerator.ConstructionLock) return new LayoutAnalyzer(modelPath);
    }

    /// <summary>
    /// Builds the analyzer on the GPU, or returns null with the reason it could not. Never throws for a
    /// missing device, runtime or driver: the GPU is additive, never required.
    /// </summary>
    public static LayoutAnalyzer? TryOnGpu(string modelPath, out LayoutDevice device)
    {
        lock (WebGpuAccelerator.ConstructionLock)
        {
            bool available;
            try { available = WebGpuAccelerator.IsAvailable; }
            catch (Exception ex)
            {
                device = LayoutDevice.Cpu with { FallbackReason = $"the WebGPU runtime could not be loaded ({Brief(ex)})" };
                return null;
            }
            if (!available)
            {
                device = LayoutDevice.Cpu with { FallbackReason = "no WebGPU-capable GPU was found" };
                return null;
            }

            string name = WebGpuAccelerator.DeviceDescription ?? "GPU";
            if (!WebGpuAccelerator.TryEnable(Architecture))
            {
                device = LayoutDevice.Cpu with { FallbackReason = $"WebGPU could not be enabled on {name}" };
                return null;
            }
            try
            {
                var analyzer = new LayoutAnalyzer(modelPath);
                device = new LayoutDevice("webgpu", name);
                return analyzer;
            }
            catch (Exception ex)
            {
                device = LayoutDevice.Cpu with { FallbackReason = $"the layout model could not be loaded on {name} ({Brief(ex)})" };
                return null;
            }
            finally
            {
                WebGpuAccelerator.Disable(Architecture);
            }
        }
    }
}

/// <summary>
/// The analyzer one extraction runs on, with a single switch to the backend's CPU fallback when the
/// primary analyzer cannot be built or throws. The switch is recorded as a note for the next page's
/// warnings, so a snapshot says when part of it was analysed on a different device.
/// </summary>
internal sealed class AnalyzerSlot : IDisposable
{
    private readonly Func<ILayoutAnalyzer>? _fallback;
    private readonly string _primaryName;
    private string? _pendingNote;
    private bool _switched;

    private AnalyzerSlot(ILayoutAnalyzer current, Func<ILayoutAnalyzer>? fallback, string primaryName, string? note)
    {
        Current = current;
        _fallback = fallback;
        _primaryName = primaryName;
        _pendingNote = note;
        _switched = note is not null;
    }

    public ILayoutAnalyzer Current { get; private set; }

    public static AnalyzerSlot Create(ExtractionBackend backend)
    {
        string name = backend.ResolvedDevice.Description;
        try
        {
            return new AnalyzerSlot(backend.AnalyzerFactory(), backend.FallbackAnalyzerFactory, name, null);
        }
        catch (Exception ex) when (backend.FallbackAnalyzerFactory is { } fallback)
        {
            return new AnalyzerSlot(fallback(), null, name,
                $"The layout model could not be started on {name} ({LayoutAnalyzerConstruction.Brief(ex)}); this document was analysed on the CPU.");
        }
    }

    /// <summary>True once any part of the extraction ran on the fallback rather than the backend's device.</summary>
    public bool UsedFallback => _switched;

    /// <summary>True while a fallback exists and has not been used.</summary>
    public bool CanSwitchToFallback => !_switched && _fallback is not null;

    /// <summary>Switches to the fallback, once.</summary>
    public void SwitchToFallback(Exception error)
    {
        if (_switched || _fallback is not { } fallback) throw new InvalidOperationException("There is no unused fallback analyzer.");
        _switched = true;
        var failed = Current;
        Current = fallback();
        // A shared analyzer that failed must not go back to the pool for the next document.
        if (failed is SharedAnalyzer.Lease lease) lease.Discard();
        else failed.Dispose();
        _pendingNote = $"Layout analysis failed on {_primaryName} ({error.GetType().Name}: {LayoutAnalyzerConstruction.Brief(error)}); " +
                       "this page and the rest of the document were analysed on the CPU.";
    }

    /// <summary>The note describing a switch not yet reported, once.</summary>
    public string? TakePendingNote()
    {
        var note = _pendingNote;
        _pendingNote = null;
        return note;
    }

    public void Dispose() => Current.Dispose();
}

/// <summary>
/// One layout analyzer kept for the whole process and lent to each extraction in turn.
/// </summary>
/// <remarks>
/// Building PP-DocLayoutV3's session costs about 6 s on the GPU (WebGPU) and its first inference
/// another 2.3 s of shader compilation; steady inference is 0.6 s a page (i7-12700H with an RTX
/// A2000). Start-up already builds one to learn the device and capabilities, and every extraction
/// used to build another, so opening a document paid about 8 s for a session it had just thrown
/// away. The start-up analyzer is now the first one lent. An extraction that finds it already lent
/// (two at once) gets a private analyzer, disposed with that extraction, as before. An analyzer that
/// failed is discarded, not returned, so the next extraction builds a fresh one.
/// </remarks>
public sealed class SharedAnalyzer
{
    private readonly Func<ILayoutAnalyzer> _build;
    private readonly object _gate = new();
    private ILayoutAnalyzer? _analyzer;
    private bool _lent;

    /// <param name="initial">An analyzer already built (start-up's probe), or null to build on first use.</param>
    /// <param name="build">Builds a new analyzer; may throw, as the backend's factory may.</param>
    public SharedAnalyzer(ILayoutAnalyzer? initial, Func<ILayoutAnalyzer> build)
    {
        _analyzer = initial;
        _build = build ?? throw new ArgumentNullException(nameof(build));
    }

    /// <summary>How many analyzers this has built, start-up's excluded. For tests and diagnostics.</summary>
    public int Builds { get; private set; }

    /// <summary>The shared analyzer if it is free, else a private one for this extraction.</summary>
    public ILayoutAnalyzer Rent()
    {
        lock (_gate)
        {
            if (!_lent)
            {
                if (_analyzer is null)
                {
                    _analyzer = _build();
                    Builds++;
                }
                _lent = true;
                return new Lease(this, _analyzer);
            }
            Builds++;
        }
        return _build();
    }

    private void Return(ILayoutAnalyzer analyzer)
    {
        lock (_gate) if (ReferenceEquals(_analyzer, analyzer)) _lent = false;
    }

    private void Drop(ILayoutAnalyzer analyzer)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_analyzer, analyzer))
            {
                _analyzer = null;
                _lent = false;
            }
        }
        analyzer.Dispose();
    }

    /// <summary>The shared analyzer as one extraction holds it. Disposing returns it to the pool.</summary>
    public sealed class Lease(SharedAnalyzer owner, ILayoutAnalyzer inner) : ILayoutAnalyzer
    {
        private int _done;

        public LayoutModelCapabilities Capabilities => inner.Capabilities;

        public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
            IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
            => inner.RunAnalysis(rgbBytes, pxW, pxH, pageW, pageH, charBoxes, ct);

        /// <summary>The extraction is over: the analyzer goes back for the next one.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Return(inner);
        }

        /// <summary>The analyzer failed: release it and let the next extraction build a fresh one.</summary>
        public void Discard()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Drop(inner);
        }
    }
}
