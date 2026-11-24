using System.Net;
using System.Text.RegularExpressions;
using RailNarrator.Infrastructure;
using Xunit;

namespace RailNarrator.Infrastructure.Tests;

/// <summary>
/// Phonon's model and voices downloaded from inside the application: the same files, revisions and
/// digests as tools/setup-phonon.sh, the layout the provider reads, and nothing left looking
/// installed when a download fails.
/// </summary>
public sealed class PhononModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rn-phonon-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static string SetupScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string script = Path.Combine(dir.FullName, "tools", "setup-phonon.sh");
            if (File.Exists(script) && File.Exists(Path.Combine(dir.FullName, "RailNarrator.slnx"))) return File.ReadAllText(script);
        }
        throw new InvalidOperationException("tools/setup-phonon.sh not found above the test's folder.");
    }

    [Fact]
    public void TheApplicationFetchesWhatTheSetupScriptFetches()
    {
        string script = SetupScript();
        Assert.Equal(PhononModel.ModelRevision, Regex.Match(script, "model_rev=\"([0-9a-f]+)\"").Groups[1].Value);
        Assert.Equal(PhononModel.VoicesRevision, Regex.Match(script, "voices_rev=\"([0-9a-f]+)\"").Groups[1].Value);

        var modelFiles = Regex.Matches(script, @"resolve/\$model_rev/(\S+)"" ""\$model/\S+"" ([0-9a-f]{64})")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(modelFiles, PhononModel.ModelFiles.Select(f => (f.File, f.Sha256)).ToList());

        string table = script.Split("<<'VOICES'")[1].Split("\nVOICES")[0];
        var voices = Regex.Matches(table, @"^(\S+) (voices/\S+) ([0-9a-f]{64}) (\S+) (\S+)$", RegexOptions.Multiline)
            .Select(m => new PhononVoice(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value))
            .ToList();
        Assert.Equal(25, voices.Count);
        Assert.Equal(voices, PhononModel.Voices);
        Assert.Contains(PhononModel.Voices, v => v.Name == "Maeve");
    }

    [Fact]
    public void FilesGoWhereTheProviderReadsThem()
    {
        Assert.Equal(29, PhononModel.Assets.Count);
        Assert.All(PhononModel.Assets, a => Assert.StartsWith("https://huggingface.co/gradium/", a.Url));
        Assert.Contains(PhononModel.Assets, a => a.FileName == "model.q8.gguf" && a.Url.Contains(PhononModel.ModelRevision));
        Assert.Contains(PhononModel.Assets, a => a.FileName == Path.Combine("voices", "Maeve.safetensors") && a.Url.Contains(PhononModel.VoicesRevision));
        Assert.Equal(71_454_489, PhononModel.TotalBytes);   // the 29 files as downloaded and verified
        Assert.EndsWith(Path.Combine("rail-narrator", "phonon", "phonon-31m-en"), PhononModel.DefaultDirectory);
    }

    [Fact]
    public void TheVoiceListIsWhatTheSetupScriptWrites_AndMarksTheInstallComplete()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "voices"));
        foreach (var asset in PhononModel.Assets) File.WriteAllText(Path.Combine(_dir, asset.FileName), "x");
        Assert.False(PhononModel.IsInstalled(_dir));   // every file, but no voice list yet

        PhononModel.WriteVoiceIndex(_dir);

        Assert.True(PhononModel.IsInstalled(_dir));
        string[] lines = File.ReadAllLines(Path.Combine(_dir, "voices.tsv"));
        Assert.Equal(25, lines.Length);
        Assert.Equal("Zoey\ten-us\tfemale", lines[0]);
        Assert.Contains("Maeve\ten-gb\tfemale", lines);
        Assert.False(File.Exists(Path.Combine(_dir, "voices.tsv.part")));
    }

    [Fact]
    public async Task AFileThatIsNotTheOneExpected_IsRefusedAndNothingLooksInstalled()
    {
        using var http = new HttpClient(new Serve(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4634]) }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => PhononModel.InstallAsync(_dir, http));

        Assert.False(PhononModel.IsInstalled(_dir));
        Assert.False(File.Exists(Path.Combine(_dir, "config.json")));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.part", SearchOption.AllDirectories));
    }

    private sealed class Serve(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
