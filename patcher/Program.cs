using System;
using System.IO;
using System.Linq;
using NineLivesPatch;

// Nine Lives offline patcher (for developers; players use Setup.exe from the release).
// Usage:  dotnet run --project patcher -- "<path to Ninelives folder>" [--loader <NineLivesModLoader.dll>]
//         dotnet run --project patcher -- "<path to Ninelives folder>" --restore
// The patch logic lives in PatchCore.cs, shared with Setup.exe.

if (args.Length < 1)
{
    Console.WriteLine("Usage: patcher <Ninelives folder> [--loader <NineLivesModLoader.dll>] [--restore]");
    return 1;
}
var game = args[0];
if (!GamePatcher.LooksLikeGame(game))
{
    Console.WriteLine("Can't find Ninelives.exe and Ninelives_Data\\Managed in " + game + " - point this at the game folder.");
    return 1;
}
try
{
    if (args.Contains("--restore")) { GamePatcher.Restore(game, Console.WriteLine); return 0; }
    string? loader = null;
    int li = Array.IndexOf(args, "--loader");
    if (li >= 0 && li + 1 < args.Length) loader = Path.GetFullPath(args[li + 1]);
    GamePatcher.Patch(game, loader, Console.WriteLine);
    Console.WriteLine("Done. Start the server, then the game.");
    return 0;
}
catch (Exception e)
{
    Console.WriteLine(e.Message);
    return 1;
}
