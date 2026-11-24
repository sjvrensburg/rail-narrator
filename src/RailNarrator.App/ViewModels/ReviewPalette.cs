using Avalonia.Media;
using RailNarrator.Infrastructure;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailNarrator.App.ViewModels;

/// <summary>
/// The five annotation colours (Core's palette, so RailReader2 shows the same colours) and what
/// each means to the reader.
/// </summary>
public static class ReviewPalette
{
    public static int Count => AnnotationInteractionHandler.AnnotationColors.Length;

    /// <summary>What the colours mean until the reader says otherwise: yellow, green, red, blue, black.</summary>
    public static IReadOnlyList<string> DefaultNames { get; } = ["Note", "Good", "Error", "Question", "Other"];

    public static string Hex(int index) => AnnotationInteractionHandler.AnnotationColors[Math.Clamp(index, 0, Count - 1)];

    public static string NameOf(RailNarratorSettings settings, int index)
        => settings.AnnotationColourNames is { } names && index < names.Count && !string.IsNullOrWhiteSpace(names[index])
            ? names[index]
            : DefaultNames[index];

    /// <summary>A markup tool's colour before the reader chooses one: Core's (highlight yellow, underline and squiggly green, strikeout red).</summary>
    public static int DefaultIndexFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Underline or AnnotationTool.Squiggly => 1,
        AnnotationTool.StrikeOut or AnnotationTool.Pen => 2,
        AnnotationTool.Rectangle => 3,
        AnnotationTool.FreeText => 4,
        _ => 0,
    };

    /// <summary>Core's opacity for a tool: a highlight is a translucent fill, a note nearly solid, the line markups and text solid.</summary>
    public static float OpacityFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlight => 0.35f,
        AnnotationTool.TextNote => 0.9f,
        AnnotationTool.Pen => 0.8f,
        AnnotationTool.Rectangle => 0.5f,
        _ => 1f,
    };

    /// <summary>Core's three line thicknesses, in points: thin, normal, thick.</summary>
    public static IReadOnlyList<float> Thicknesses => AnnotationInteractionHandler.ThicknessPresets;

    public static IBrush Brush(int index) => new SolidColorBrush(Color.Parse(Hex(index)));
}

/// <summary>One colour of the palette as the Settings dialogue shows it: its swatch and an editable name.</summary>
public sealed class ColourNameSetting(int index, string hex, string name, Action<int, string> save)
{
    private string _name = name;

    public IBrush Swatch { get; } = new SolidColorBrush(Color.Parse(hex));

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            save(index, value);
        }
    }
}

/// <summary>One colour as the toolbar offers it.</summary>
public sealed record PaletteColour(int Index, string Hex, string Name)
{
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(Hex));

    public string Key => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Tip => $"{Name} ({Key})";
}
