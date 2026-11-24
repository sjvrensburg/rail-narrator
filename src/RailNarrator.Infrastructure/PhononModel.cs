using System.Text;

namespace RailNarrator.Infrastructure;

/// <summary>One of Phonon's voices: its name, its file in Gradium's voice catalog, and how it is listed.</summary>
public sealed record PhononVoice(string Name, string CatalogPath, string Sha256, string Locale, string Gender);

/// <summary>
/// Phonon's model and voices (ADR-0009), downloaded from inside the application on the reader's
/// approval, like the other models. A release carries Phonon's runtime beside the application;
/// this is everything else it needs.
/// </summary>
/// <remarks>
/// The same files, at the same revisions and digests, that <c>tools/setup-phonon.sh</c> installs,
/// in the same layout: <c>phonon-31m-en/</c> with the model files, <c>voices/&lt;name&gt;.safetensors</c>
/// and <c>voices.tsv</c>. <c>PhononModelTests</c> holds this table to the script's, so the two
/// cannot drift apart. Sizes are of the files downloaded and verified on the reference machine.
/// </remarks>
public static class PhononModel
{
    public const string ModelRevision = "92741c5b07d085ff840e26fea8ce43110b87852f";
    public const string VoicesRevision = "da6c9ec0d47be8df5c87c730a3aa97187f3229c3";
    public const string Licence = "Apache-2.0 (Gradium's phonon-31m-en and voices)";
    private const string Hub = "https://huggingface.co/gradium";

    /// <summary>Where the application looks for Phonon's model when no setting says otherwise.</summary>
    public static string DefaultDirectory => Path.Combine(XdgPaths.Data, "phonon", "phonon-31m-en");

    /// <summary>The model's own files: name, size, SHA-256.</summary>
    public static IReadOnlyList<(string File, long Bytes, string Sha256)> ModelFiles { get; } =
    [
        new("config.json", 4_634, "b5d0f88353638370d94035b7d73d7d6577c0554d78f8d7ecfd40490aae39ae85"),
        new("tokenizer.json", 277_967, "691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936"),
        new("model.q8.gguf", 64_512_352, "a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae"),
        new("default-voice.safetensors", 256_136, "dc537478eaadc1a5257fcda2e3501ab81e14c111a5d95a5838f350db2afd0f1a"),
    ];

    /// <summary>The 25 British and American English voices, Maeve (the default) among them.</summary>
    public static IReadOnlyList<PhononVoice> Voices { get; } =
    [
        new("Zoey", "voices/NbpkqMVS3CJeq2j8.safetensors", "97c7f8c253413574406ab80dd23f49c3ebb2517163b00a9bf86b9c4cf43e7e23", "en-us", "female"),
        new("Juniper", "voices/NIWIOv5vTi5eRMj8.safetensors", "444378eb06b7c492b86e8fc7d13e4db1a3c0cf3e68891a960bf6f0a41a13ef35", "en-us", "female"),
        new("Everly", "voices/vjFyp2BcCyTlUmYb.safetensors", "958ea94da14f46dd48c7f64adac11ed2ad2aa45f7ccd165dfebab5272664402e", "en-us", "female"),
        new("Harper", "voices/4SZHfMpw-p46Ywgs.safetensors", "6b3da6e2d796a20c51af892c482bd6509fb88267a221aff83c870843dee2a123", "en-us", "female"),
        new("Hadley", "voices/bXwrkHhURL1VFJdG.safetensors", "a879938aa9a2df0a8220bdadef88137929f71e887bd91e7db205bd5fa11661d2", "en-us", "female"),
        new("Delia", "voices/nGzMHMPAWGaJFHOo.safetensors", "a0c3dcb91b2247eca56536be9ae8e8becc1c807b1136c5fe101552d6a155b24a", "en-us", "female"),
        new("Easton", "voices/LMDIYE0Oa377Qh6Z.safetensors", "45c582673cb20edb04ddcca2b5a8865ddfb0a82599efadb269021e87d20fbdd4", "en-us", "male"),
        new("Micah", "voices/40QrMVgAxFiUnP9f.safetensors", "0de1098d1051686a138d068e83ff09deb5053328bc74ec9541a1fdb90bfc24da", "en-us", "male"),
        new("Trevor", "voices/0zLApsZfVLLNaEGl.safetensors", "dc1341c8937af5534e6bbcc5c0fc5e7c10312229b59061228074f93683fe2261", "en-us", "male"),
        new("Russell", "voices/_6Aslh2DxfmnRLmP.safetensors", "beae9e4030efce0a935fcb0f807dc0784d598bcaa87ad4aacb047436b1eaa411", "en-us", "male"),
        new("Preston", "voices/qIxlZ7Ei8KvWlyQH.safetensors", "6e04b84d3ae8751b22bca0a4f63936f9cb648f4f2182b2d0788071ea1c0b68a1", "en-us", "male"),
        new("Miles", "voices/d9XCyhjo4JhkHF1d.safetensors", "eab20e4f159b48cfbc01b1885804e24f681f80e7a64f909182763a4e3361bee8", "en-us", "male"),
        new("Maeve", "voices/6PWnV0Nq4wu7RVBT.safetensors", "56494ee67d533ad227ff0dfd0b1c13ff5aa91f1ad6e596e66c2986ba3005e844", "en-gb", "female"),
        new("Freya", "voices/GgfEkEJtxZR7gnpy.safetensors", "eb3cd47418349e618408c16bea7153988b3ad04b41a6033039dd474a9a4dcd23", "en-gb", "female"),
        new("Pippa", "voices/uem82D50GRv2Dwma.safetensors", "d41a8f01d6e33450f1ce062d332914da10beee99e585dab531750706f187c48a", "en-gb", "female"),
        new("Elodie-Rose", "voices/y9bQqIDnyxwqK01t.safetensors", "21eade9d83b2e66b9364e1b6cd999a3a6704c000a71f27f34e05ace5cdfd58bb", "en-gb", "female"),
        new("Imogen", "voices/gDT1nz7Ie36ZhL-C.safetensors", "1980ab3c35fa98a25b5f3b5e643fd852e14ca8a12a4a26b06aaf1abf2a8c68b1", "en-gb", "female"),
        new("Lottie", "voices/m9MxpY6IUGXtWp76.safetensors", "a906ebf17e43fded12b3d6bd077abb2df33bc86eff51c1beeb82e5d416527db3", "en-gb", "female"),
        new("Alfie", "voices/X6NceqF285Gr04gb.safetensors", "4de75497fff3030168e8aa6dca23ffeb332b1bc8c25d0bf3507ceadac953ae9c", "en-gb", "male"),
        new("Hugh", "voices/YyKcxPG7iIgS3jsK.safetensors", "3169d74b47a54d1eb15e4c243f56393abe9fe90c7e1c308ecf96ee67618b152a", "en-gb", "male"),
        new("Edwin", "voices/zUCxzrOrugTSis03.safetensors", "ff1d0735b8b45710462b1d1f846da040f970c6eb62c7c2f36dbfb8e5bde74f1e", "en-gb", "male"),
        new("Spencer", "voices/JOW0vchyswkQUMDS.safetensors", "cc9ac1107b0ffce282374a19beb7bfa95847724e6b7afbac991b4ae4277d5e6d", "en-gb", "male"),
        new("Toby", "voices/dME3IWyZBvmh1n1q.safetensors", "09ab315c7f4b72b4d71e66cb756184cedfb1f14d222d096e115bf95bfe7b29dc", "en-gb", "male"),
        new("Teddy", "voices/Zn7g6NxLqXlJb7Qi.safetensors", "baea0ea9506f3237a5ebfc61afb7c72dbd189c5603b199ef42fbb00dbda379e0", "en-gb", "male"),
        new("Bryony", "voices/gfP44RlHwr2kEwk7.safetensors", "bcc207a73b7885e45b49ab633d3377a10158292e3a7cef9cd6764ad107f1cb9b", "en-gb", "female"),
    ];

    private const long VoiceBytes = 256_136;

    /// <summary>Every file to download, as assets the <see cref="ModelDownloader"/> fetches and verifies, into the model folder.</summary>
    public static IReadOnlyList<ModelAsset> Assets { get; } =
    [
        .. ModelFiles.Select(f => new ModelAsset(
            Id: "phonon-31m-en/" + f.File,
            Description: "Phonon phonon-31m-en: " + f.File,
            Url: $"{Hub}/phonon-31m-en/resolve/{ModelRevision}/{f.File}",
            Revision: "gradium/phonon-31m-en @ " + ModelRevision,
            Bytes: f.Bytes, Sha256: f.Sha256, Licence: Licence, Required: false, FileName: f.File)),
        .. Voices.Select(v => new ModelAsset(
            Id: "phonon-voice/" + v.Name,
            Description: $"Phonon voice {v.Name} ({v.Locale})",
            Url: $"{Hub}/phonon-voices/resolve/{VoicesRevision}/{v.CatalogPath}",
            Revision: "gradium/phonon-voices @ " + VoicesRevision,
            Bytes: VoiceBytes, Sha256: v.Sha256, Licence: Licence, Required: false,
            FileName: Path.Combine("voices", v.Name + ".safetensors"))),
    ];

    public static long TotalBytes => Assets.Sum(a => a.Bytes);

    /// <summary>
    /// Whether the model is installed in <paramref name="directory"/>: every file present, and the
    /// voice list, which is written last. Presence only; the download verifies each file.
    /// </summary>
    public static bool IsInstalled(string directory)
        => File.Exists(Path.Combine(directory, "voices.tsv"))
           && Assets.All(a => File.Exists(Path.Combine(directory, a.FileName)));

    /// <summary>The voice list the provider reads (<c>voices.tsv</c>: name, locale, gender), written atomically.</summary>
    public static void WriteVoiceIndex(string directory)
    {
        var text = new StringBuilder();
        foreach (var v in Voices) text.Append(v.Name).Append('\t').Append(v.Locale).Append('\t').Append(v.Gender).Append('\n');
        string path = Path.Combine(directory, "voices.tsv"), temp = path + ".part";
        File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Downloads and verifies every file into <paramref name="directory"/>, then writes the voice
    /// list. Files already there and verified are kept; nothing is fetched without the caller asking.
    /// </summary>
    public static async Task InstallAsync(string directory, HttpClient? http = null,
        IProgress<(int Done, int Total, DownloadProgress File)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.Combine(directory, "voices"));
        var downloader = new ModelDownloader(directory, http);
        try
        {
            int done = 0;
            foreach (var asset in Assets)
            {
                ct.ThrowIfCancellationRequested();
                int index = done;
                await downloader.DownloadAsync(asset,
                    new Progress<DownloadProgress>(p => progress?.Report((index, Assets.Count, p))), ct).ConfigureAwait(false);
                done++;
            }
        }
        finally { downloader.Dispose(); }
        WriteVoiceIndex(directory);
    }
}
