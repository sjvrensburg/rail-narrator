# Rail Narrator

Rail Narrator reads a PDF aloud. While it reads, it highlights the passage being spoken in the
page itself and keeps that passage in view. It is made for papers and technical documents,
mathematics included: instead of reading out symbols, it can say what an equation *means*, in the
paper's own terms.

It is a companion to [RailReader2](https://github.com/sjvrensburg/railreader2) and is built on the
same PDF engine, [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore).

> **Early release (v0.1).** It works end to end on Linux, where it was developed. The Windows
> build is a **preview**: it is built and tested automatically, but nobody has yet run it on a
> Windows PC. Please [report what you find](https://github.com/sjvrensburg/rail-narrator/issues).

## What it does

- **Reads aloud with the page in front of you.** The sentence being read is highlighted where it
  is printed. With a voice that times its words, the highlight moves word by word. The view
  follows the reading, at any zoom.
- **Reads maths for meaning.** Point it at a language model, either one running on your own
  computer (any OpenAI-compatible server) or Claude. The model looks at each equation and each
  sentence with maths in it, and the narrator then says what the maths means. Without a model,
  an optional formula model reads display equations aloud as notation ("x sub t equals …"), and
  without that the narrator says that an equation is there.
- **Skips what you would skip.** It passes over contents pages and the affiliations on a first
  page. With the layout model installed, it also leaves out running heads, page numbers,
  footnotes, references, figures and table cells.
- **Lets you take notes as you listen.** Press N to note what was just read, or H to highlight
  it. Annotate mode (Ctrl+E) gives you highlighters, underline, strikeout, sticky notes, a pen
  and boxes. Your notes stay beside the PDF, never inside it. You can export them as Markdown, as
  an annotated copy of the PDF, or for RailReader2, and they follow you to a revised version of
  the same paper.
- **Narrates a whole document ahead of time.** File ▸ Save narration with document… prepares
  every passage once and keeps the result beside the PDF. From then on it plays with no language
  model and no speech engine. File ▸ Export audiobook (M4B)… turns it into an audiobook with a
  chapter at each heading.
- **Keeps your work on your computer.** Speech, layout and formula models run locally. Nothing
  leaves your machine unless you set up a remote maths endpoint yourself.

## Install

Download the latest version from the
[releases page](https://github.com/sjvrensburg/rail-narrator/releases/latest).

### Linux (x64)

You need PortAudio for sound: `sudo apt install libportaudio2` on Debian and Ubuntu, or
`sudo dnf install portaudio` on Fedora.

```bash
tar -xzf rail-narrator-0.1.0-linux-x64.tar.gz
cd rail-narrator-0.1.0-linux-x64
usr/bin/rail-narrator            # or: usr/bin/rail-narrator paper.pdf
```

To install it for your user, with a menu entry and PDF file association:

```bash
cp -r usr/. ~/.local/
```

### Windows (x64), preview

Unzip `rail-narrator-0.1.0-win-x64.zip` anywhere and run `rail-narrator.exe`. The download is
not code-signed, so Windows SmartScreen may warn you. Choose **More info**, then **Run anyway**.

## Getting started

1. **Get a voice.** Rail Narrator comes without a speech model; you choose and download one, and
   it is never fetched without you asking. You have two choices:
   - **Kokoro:** open *Models and endpoints* ▸ *Download Kokoro speech model* (164 MB) in the
     app. For word-by-word highlighting, also download *Kokoro with word timing* (163 MB) and set
     *Settings* ▸ *Reading* ▸ *Word timing* to *The speech engine's own timing*.
   - **Phonon:** a faster voice, with British and American accents and word-by-word timing
     built in. Run `install-phonon.sh` from the folder you unpacked (about 70 MB; it needs
     `curl`). On Windows, run it from Git Bash.

   Then choose the voice in *Settings* ▸ *Speech* ▸ *Engine*, and restart when asked. If both
   are installed, Phonon is used.
2. **Install the layout model (recommended).** Choose *Models and endpoints* ▸ *Download layout
   model* (69 MB). It finds headings, figures, tables and equations, so the reading skips what
   it should.
3. **Open a PDF.** Use *File* ▸ *Open PDF…* (Ctrl+O), or drop the file on the window. Press
   **Space** to start. A document is never read aloud until you ask.

### Reading maths

To have maths read for its meaning, set up a language model in *Settings* ▸ *Maths*:

- **On your own computer:** any OpenAI-compatible server (for example llama.cpp's
  `llama-server`). Give its address and model name. A model that can read images works best,
  because it is shown the equation as printed.
- **Claude:** choose Claude and give an Anthropic API key. Each passage with maths is then sent
  to Anthropic, as an image of the page area with the text around it.

*Models and endpoints* ▸ *Download formula model* (591 MB) adds a model that reads display
equations on your computer. The language model is given what it reads alongside the page. With
no language model at all, it is what the narrator reads equations from.

### Keys worth knowing

| Key | Does |
|---|---|
| Space | Play / pause |
| Ctrl+→ / Ctrl+← | Next / previous passage |
| Ctrl+R | Replay the passage |
| Ctrl+F | Follow the reading on or off |
| F9 | Resume following after you scroll away |
| N / H | Note / highlight what was just read |
| Ctrl+E | Annotate mode |
| C | Next colour filter (high contrast, amber, invert) |
| F6 | Sidebar: outline, script, maths review, notes |
| F1 | All keyboard shortcuts |

### From the command line

```bash
rail-narrator narrate paper.pdf        # prepares paper.narration beside the PDF
rail-narrator export paper.pdf         # writes paper.m4b, an audiobook with chapters
```

On Windows, use `rail-narrator-cli.exe` in place of `rail-narrator`. Exporting an audiobook
needs [ffmpeg](https://ffmpeg.org/), which you install yourself (`sudo apt install ffmpeg`, or
`winget install ffmpeg` on Windows). Rail Narrator does not include it.

## Where your things are kept

| | Linux | Windows |
|---|---|---|
| Settings | `~/.config/rail-narrator` | `%APPDATA%\rail-narrator` |
| Models, notes, reading positions | `~/.local/share/rail-narrator` | `%LOCALAPPDATA%\rail-narrator` |
| Caches (safe to delete) | `~/.cache/rail-narrator` | `%LOCALAPPDATA%\rail-narrator\cache` |

Your PDFs are never changed. Highlights and notes are kept separately, by the PDF's content, and
*Review* ▸ *Export* ▸ *Save an annotated copy of the PDF…* writes them into a copy. An API key
typed into Settings is stored in the settings file, which only your user can read.

## Known limitations

- English only, for now.
- The Windows build has not yet been run on a Windows PC.
- The optional forced aligner, which measures word timing more closely, is Linux-only and is
  installed from the source code ([how](docs/word-highlighting.md)).
- Pages are shown one at a time, with no continuous scrolling.
- Scanned PDFs are read through OCR, which has been tried on only a few scans.
- How quickly a highlight follows the sound has been checked against the audio device's own
  clock, not measured with a microphone. Expect small delays on Bluetooth headphones.

[docs/status.md](docs/status.md) lists what has been verified, how it was measured, and what has
not.

## Licence and credits

Rail Narrator is released under the [MIT licence](LICENSE). It is built on RailReaderCore and
uses PDFium, SkiaSharp, Avalonia, ONNX Runtime and PortAudio, with Kokoro and Phonon (Gradium)
for speech, Docling Heron for layout, and PP-FormulaNet for formulas. Each is under its own
licence: see [THIRD-PARTY-NOTICES.txt](packaging/THIRD-PARTY-NOTICES.txt). Icons are from Lucide
(ISC).

## Building from source

See [docs/development.md](docs/development.md) for building, testing, how the project is laid out,
and how releases are made.
