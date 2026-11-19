using System.Diagnostics;
using System.Text.Json;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>All resources are local paths. Alignment never downloads models or sends audio off-box.</summary>
/// <param name="G2pModelPath">
/// Optional MFA G2P model, in the dictionary's phone set, that proposes pronunciations for words the
/// dictionary lacks. Without it such a clip is aligned only if its unknown words are all-capital
/// acronyms (spelled from the dictionary's letter names), and is otherwise highlighted whole.
/// </param>
public sealed record MfaAlignerSettings(string Executable, string DictionaryPath, string AcousticModelPath,
    string WorkDirectory, string RuntimeRevision = "3.3.9", int TimeoutSeconds = 120, string? G2pModelPath = null);

/// <summary>A resident local MFA/Kalpy worker, serialized to protect its model and lexicon state.</summary>
public sealed class MfaForcedAligner : IForcedAligner, IAsyncDisposable
{
    private readonly MfaAlignerSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<string> _revision;
    private Process? _worker;
    private Task? _stderrTask;
    private string? _workerDirectory;
    private readonly object _errorGate = new();
    private string _errorTail = "";
    private bool _disposed;
    private readonly CancellationTokenSource _closing = new();

    public MfaForcedAligner(MfaAlignerSettings settings)
    {
        _settings = settings;
        // worker-3: pronunciations generated for words the dictionary lacks, and silent marks no
        // longer counted as words. The G2P model is part of the revision because it decides which
        // clips align at all.
        _revision = new Lazy<string>(() => $"mfa/{settings.RuntimeRevision}/kalpy-0.10.5/worker-3:"
            + CacheKey.Sha256Hex(FileFingerprint(settings.DictionaryPath) + FileFingerprint(settings.AcousticModelPath)
                + (G2pPath(settings) is { } g2p ? "g2p:" + FileFingerprint(g2p) : "no-g2p")));
    }

    private static string FileFingerprint(string path) => File.Exists(path) ? CacheKey.Sha256HexOfFile(path) : "missing:" + path;

    /// <summary>The G2P model when one is configured and present; a missing file means none.</summary>
    private static string? G2pPath(MfaAlignerSettings settings)
        => settings.G2pModelPath is { Length: > 0 } path && File.Exists(path) ? Path.GetFullPath(path) : null;
    public string Revision => _revision.Value;
    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(Path.Combine(Path.GetDirectoryName(_settings.Executable) ?? "", "python")) && File.Exists(_settings.Executable) && File.Exists(_settings.DictionaryPath)
            && File.Exists(_settings.AcousticModelPath));
    }

    public async Task<WordAlignment> AlignAsync(ReadOnlyMemory<byte> pcm, AudioFormat format, string script, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Word alignment needs a local MFA environment, pronunciation dictionary and acoustic model.");
        if (format.Channels != 1 || !format.IsValid || pcm.Length % format.BytesPerFrame != 0)
            throw new ArgumentException("Alignment requires complete mono PCM frames.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string directory = Path.Combine(Path.GetFullPath(_settings.WorkDirectory), "requests", Guid.NewGuid().ToString("N"));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(directory);
            string wav = Path.Combine(directory, "speech.wav");
            var int16 = Pcm.ConvertTo(pcm.Span, format, SampleFormat.Int16LittleEndian);
            await File.WriteAllBytesAsync(wav, WavCodec.Write(format with { SampleFormat = SampleFormat.Int16LittleEndian }, int16), ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            string json;
            try
            {
                await EnsureWorkerAsync(timeout.Token).ConfigureAwait(false);
                var tokens = SpokenWord.Tokenize(script);
                // The worker matches on the key; the written form tells it which unknown words are
                // acronyms the voice spells out.
                string request = JsonSerializer.Serialize(new
                {
                    wav,
                    words = tokens.Select(w => SpokenWord.MatchKey(w.Text)).ToArray(),
                    forms = tokens.Select(w => w.Text).ToArray(),
                });
                await _worker!.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                json = await ReadReplyAsync(timeout.Token, request: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await StopWorkerAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"Word alignment exceeded {_settings.TimeoutSeconds} seconds.");
            }
            catch (AlignmentRejectedException) { throw; } // Worker handled this request and is ready for the next one.
            catch
            {
                await StopWorkerAsync().ConfigureAwait(false);
                throw;
            }
            return ParseAlignment(json, script, format, pcm.Length / format.BytesPerFrame);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_disposed || !await IsAvailableAsync(ct).ConfigureAwait(false)) return;
        // Disposal cancels a warm-up rather than waiting for a worker that is still starting.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        ct = linked.Token;
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            await EnsureWorkerAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled, or the worker could not start: the first alignment starts it again and
            // reports why it cannot.
            await StopWorkerAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureWorkerAsync(CancellationToken ct)
    {
        if (_worker is { HasExited: false }) return;
        await StopWorkerAsync().ConfigureAwait(false);
        _workerDirectory = Path.Combine(Path.GetFullPath(_settings.WorkDirectory), "workers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workerDirectory);
        string script = Path.Combine(_workerDirectory, "alignment_worker.py");
        using (var resource = typeof(MfaForcedAligner).Assembly.GetManifestResourceStream("RailNarrator.Speech.alignment_worker.py")
            ?? throw new InvalidOperationException("The packaged alignment worker is missing."))
        using (var file = File.Create(script)) await resource.CopyToAsync(file, ct).ConfigureAwait(false);
        string bin = Path.GetDirectoryName(Path.GetFullPath(_settings.Executable))!;
        var start = new ProcessStartInfo(Path.Combine(bin, "python"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = _workerDirectory,
        };
        foreach (string arg in new[] { "-u", script, Path.GetFullPath(_settings.DictionaryPath), Path.GetFullPath(_settings.AcousticModelPath),
            Path.Combine(Path.GetFullPath(_settings.WorkDirectory), "lexicon", CacheKey.Sha256Hex(Revision)),
            G2pPath(_settings) ?? "" }) start.ArgumentList.Add(arg);
        start.Environment["MFA_ROOT_DIR"] = Path.Combine(_workerDirectory, "mfa");
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["OPENBLAS_NUM_THREADS"] = "1";
        start.Environment["OMP_NUM_THREADS"] = "1";
        _worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the alignment worker.");
        var process = _worker;
        _errorTail = "";
        _stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (_errorGate) { _errorTail += line + "\n"; if (_errorTail.Length > 2000) _errorTail = _errorTail[^2000..]; }
        });
        string ready = await ReadReplyAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(ready);
        if (!document.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean())
            throw new InvalidDataException("Alignment worker did not report readiness.");
    }

    private sealed class AlignmentRejectedException(string message) : Exception(message);

    private async Task<string> ReadReplyAsync(CancellationToken ct, bool request = false)
    {
        string? line = await _worker!.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null)
        {
            string detail;
            lock (_errorGate) detail = _errorTail;
            throw new IOException("Alignment worker exited unexpectedly. " + detail);
        }
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            if (request) throw new AlignmentRejectedException(error.GetString() ?? "Alignment failed.");
            throw new InvalidDataException(error.GetString());
        }
        return line;
    }

    private async Task StopWorkerAsync()
    {
        if (_worker is { } worker)
        {
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await worker.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (_stderrTask is not null) await _stderrTask.ConfigureAwait(false);
            worker.Dispose();
            _worker = null;
            _stderrTask = null;
        }
        if (_workerDirectory is { } dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _workerDirectory = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; await StopWorkerAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Match the entire token sequence in order; never find words by searching for their text.</summary>
    public static WordAlignment ParseAlignment(string json, string script, AudioFormat format, long frameCount)
    {
        using var document = JsonDocument.Parse(json);
        var tiers = document.RootElement.GetProperty("tiers");
        var wordTiers = tiers.ValueKind == JsonValueKind.Object
            ? tiers.EnumerateObject().Where(p => p.Name == "words" || p.Name.EndsWith(" - words", StringComparison.Ordinal)).Select(p => p.Value).ToArray()
            : tiers.EnumerateArray().Where(t => t.GetProperty("name").GetString() is { } name
                && (name == "words" || name.EndsWith(" - words", StringComparison.Ordinal))).ToArray();
        if (wordTiers.Length != 1) throw new InvalidDataException("Expected exactly one word tier from MFA.");
        var expected = SpokenWord.Tokenize(script);
        var words = new List<AlignedWord>();
        foreach (var entry in wordTiers[0].GetProperty("entries").EnumerateArray())
        {
            string label = entry[2].GetString() ?? "";
            // Kalpy exports optional-silence words as <eps>; praatio also inserts blank gaps.
            // <unk> is deliberately NOT silence and must reject the alignment.
            if (string.IsNullOrWhiteSpace(label) || label == "<eps>") continue;
            if (words.Count >= expected.Count || SpokenWord.MatchKey(label) != SpokenWord.MatchKey(expected[words.Count].Text))
                throw new InvalidDataException($"Alignment word {words.Count + 1} ('{label}') does not match the spoken script; using sentence highlighting.");
            double start = entry[0].GetDouble(), end = entry[1].GetDouble();
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > format.ToSeconds(frameCount) + 0.001)
                throw new InvalidDataException("Alignment interval is outside the audio.");
            long first = format.ToFrames(start), last = Math.Min(frameCount, format.ToFrames(end));
            if (words.Count > 0 && first < words[^1].EndSample) throw new InvalidDataException("Overlapping alignment intervals.");
            words.Add(new AlignedWord(expected[words.Count].Range, first, last));
        }
        if (words.Count != expected.Count || words.Count == 0)
            throw new InvalidDataException("Alignment did not cover every spoken word; using sentence highlighting.");
        // MFA does not export calibrated word confidence. Do not invent probabilities.
        return new WordAlignment(words, GeneratedPronunciationNotes(document.RootElement));
    }

    /// <summary>
    /// Says which words were aligned against a generated pronunciation rather than one from the
    /// dictionary, so the timing status does not claim more than the evidence.
    /// </summary>
    private static IReadOnlyList<string> GeneratedPronunciationNotes(JsonElement root)
    {
        if (!root.TryGetProperty("generated", out var generated) || generated.ValueKind != JsonValueKind.Object) return [];
        var spelled = new List<string>();
        var guessed = new List<string>();
        foreach (var word in generated.EnumerateObject())
        {
            var sources = word.Value.EnumerateArray().Select(c => c[1].GetString()).ToHashSet();
            (sources.Contains("spelled") ? spelled : guessed).Add(word.Name);
        }
        var notes = new List<string>();
        if (spelled.Count > 0) notes.Add($"Not in the pronunciation dictionary, aligned as letters (or a generated pronunciation): {string.Join(", ", spelled)}.");
        if (guessed.Count > 0) notes.Add($"Not in the pronunciation dictionary, aligned against a generated pronunciation: {string.Join(", ", guessed)}.");
        return notes;
    }
}
