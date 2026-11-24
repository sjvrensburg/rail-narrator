using RailNarrator.App.Composition;

namespace RailNarrator.Cli;

internal static class Program
{
    /// <summary>Runs <c>narrate</c> or <c>export</c>, exactly as <c>rail-narrator narrate</c> does on Linux.</summary>
    public static int Main(string[] args)
    {
        if (!HeadlessNarration.IsCommand(args))
        {
            Console.Error.WriteLine(HeadlessNarration.Usage);
            return 2;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        return HeadlessNarration.RunAsync(args, Console.Out, Console.Error, stop.Token).GetAwaiter().GetResult();
    }
}
