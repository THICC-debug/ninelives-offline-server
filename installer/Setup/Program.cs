using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NineLivesSetup
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            // Mono.Cecil is embedded in Setup.exe (see Setup.csproj); load it from there.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                using (var res = typeof(Program).Assembly.GetManifestResourceStream(name + ".dll"))
                {
                    if (res == null) return null;
                    var bytes = new byte[res.Length];
                    res.Read(bytes, 0, bytes.Length);
                    return Assembly.Load(bytes);
                }
            };
            bool cli = args.Any(a => new[] { "--install", "--uninstall", "--status", "--help", "/?" }.Contains(a.ToLowerInvariant()));
            if (cli) return CommandLine(args);
            int g = Array.IndexOf(args, "--game");
            return Window(g >= 0 && g + 1 < args.Length ? args[g + 1] : null);
        }

        static int Window(string game)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(game));
            return 0;
        }

        /// <summary>
        /// Setup.exe --status    [--game DIR]
        /// Setup.exe --install   [--game DIR] [--mods default|all|none|Id,Id,...] [--no-shortcut]
        /// Setup.exe --uninstall [--game DIR] [--delete-data]
        /// </summary>
        static int CommandLine(string[] args)
        {
            AttachConsole(-1);   // print into the console Setup was started from (if any)
            Func<string, string> opt = name =>
            {
                int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            };
            Func<string, bool> has = name => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (has("--help") || has("/?"))
            {
                Console.WriteLine("Nine Lives Offline Setup " + Installer.Version);
                Console.WriteLine("  Setup.exe                    open the setup window");
                Console.WriteLine("  Setup.exe --status    [--game DIR]");
                Console.WriteLine("  Setup.exe --install   [--game DIR] [--mods default|all|none|Id,Id] [--no-shortcut]");
                Console.WriteLine("  Setup.exe --uninstall [--game DIR] [--delete-data]");
                return 0;
            }
            var game = opt("--game") ?? Installer.FindGame();
            if (game == null) { Console.WriteLine("Can't find the game. Pass --game \"path\\to\\Ninelives\"."); return 1; }
            try
            {
                if (has("--uninstall")) { Installer.Uninstall(game, has("--delete-data"), Console.WriteLine); return 0; }
                if (has("--install"))
                {
                    var mods = Installer.LoadMods();
                    var which = (opt("--mods") ?? "default").Trim();
                    List<string> ids;
                    if (which.Equals("all", StringComparison.OrdinalIgnoreCase)) ids = mods.Select(m => m.Id).ToList();
                    else if (which.Equals("none", StringComparison.OrdinalIgnoreCase)) ids = new List<string>();
                    else if (which.Equals("default", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep what's installed when updating; otherwise the release's defaults.
                        var installed = mods.Where(m => m.InstalledIn(game)).ToList();
                        ids = (installed.Count > 0 ? installed : mods.Where(m => m.Default)).Select(m => m.Id).ToList();
                    }
                    else ids = which.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    Installer.Install(game, ids, !has("--no-shortcut"), Console.WriteLine);
                    return 0;
                }
                Console.WriteLine(game);
                Console.WriteLine(Installer.Describe(game));
                foreach (var m in Installer.LoadMods())
                    Console.WriteLine("  [" + (m.InstalledIn(game) ? "x" : " ") + "] " + m.Id + " " + m.Version + " - " + m.Description);
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
                return 1;
            }
        }
    }
}
