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
        failed.Dispose();
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
