using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NineLivesPatch;

namespace NineLivesSetup
{
    /// <summary>One mod in the release (a line of files\mods.txt).</summary>
    public class ModEntry
    {
        public string Id, Version, Zip, Description, EditorName, EditorScript;
        public bool Default;
        public string Dll { get { return Path.Combine("Mods", Id + ".dll"); } }
        public bool InstalledIn(string game) { return File.Exists(Path.Combine(game, Dll)); }
        public string EditorLauncher { get { return EditorName == null ? null : Path.Combine("Mods", "Open " + EditorName + ".bat"); } }
    }

    /// <summary>
    /// What Setup does, without any UI (the window and the command line both call this).
    ///
    /// Layout next to Setup.exe:
    ///   files\loader\   NineLivesModLoader.dll, 0Harmony.dll
    ///   files\server\   ninelives_server.py, aes.py, recipes.json, docs
    ///   files\python\   the official Python embeddable package (runs the server and editors)
    ///   files\mods\     one release zip per mod, laid out to unzip into the game folder
    ///   files\mods.txt  id | version | zip | default | description | editor name | editor script
    ///
    /// Installed into the game folder:
    ///   Ninelives_Data\Managed\*   patched (originals backed up to Ninelives_Data\OfflineBackup)
    ///   OfflineServer\             server, its Python, and the save (save.json)
    ///   Mods\                      mod loader's folder, the chosen mods, editor launchers
    ///   Play Nine Lives Offline.bat
    /// </summary>
    public static class Installer
    {
        public const string ServerFolder = "OfflineServer";
        public const string PlayLauncher = "Play Nine Lives Offline.bat";
        public const string ShortcutName = "Nine Lives (Offline).lnk";
        static readonly string[] SaveFiles = { "save.json" };

        public static string Version
        {
            get { var v = typeof(Installer).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        public static string Payload { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files"); } }

        public static string PayloadProblem()
        {
            foreach (var p in new[] { "loader\\NineLivesModLoader.dll", "loader\\0Harmony.dll", "server\\ninelives_server.py", "python\\python.exe", "mods.txt" })
                if (!File.Exists(Path.Combine(Payload, p)))
                    return "Setup can't find its files (" + p + "). Unzip the whole download first, then run Setup.exe from the unzipped folder.";
            return null;
        }

        public static List<ModEntry> LoadMods()
        {
            var list = new List<ModEntry>();
            var path = Path.Combine(Payload, "mods.txt");
            if (!File.Exists(path)) return list;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var f = line.Split('|').Select(x => x.Trim()).ToArray();
                if (f.Length < 5) continue;
                list.Add(new ModEntry
                {
                    Id = f[0], Version = f[1], Zip = f[2], Default = f[3] == "1", Description = f[4],
                    EditorName = f.Length > 6 && f[5].Length > 0 ? f[5] : null,
                    EditorScript = f.Length > 6 && f[6].Length > 0 ? f[6] : null,
                });
            }
            return list;
        }

        // ---------- finding the game ----------

        static string LastGameFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NineLivesOffline", "last-game.txt"); }
        }

        /// <summary>Best guess at the game folder: next to Setup, a parent, a sibling "Ninelives", last time's, common places.</summary>
        public static string FindGame()
        {
            var tries = new List<string>();
            try { if (File.Exists(LastGameFile)) tries.Add(File.ReadAllText(LastGameFile).Trim()); } catch { }
            var d = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(d); i++)
            {
                tries.Add(d);
                tries.Add(Path.Combine(d, "Ninelives"));
                tries.Add(Path.Combine(d, "Nine Lives"));
                d = Path.GetDirectoryName(d);
            }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "C:\\Games", "D:\\Games" })
                if (!string.IsNullOrEmpty(root)) { tries.Add(Path.Combine(root, "Ninelives")); tries.Add(Path.Combine(root, "Nine Lives")); }
            foreach (var t in tries)
                if (GamePatcher.LooksLikeGame(t)) return Path.GetFullPath(t);
            return null;
        }

        // ---------- state ----------

        public static bool GameRunning() { return Process.GetProcessesByName("Ninelives").Length > 0; }

        /// <summary>
        /// Stop the offline server and editors started from this game folder (they lock the Python files).
        /// Only Setup's own Python (OfflineServer\python\python.exe) is touched. The server saves after
        /// every request, so nothing is lost.
        /// </summary>
        static void StopOurPython(string game, Action<string> log)
        {
            var ours = Path.Combine(game, ServerFolder, "python").TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName("python"))
            {
                try
                {
                    if (!p.MainModule.FileName.StartsWith(ours, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                    log("Stopped the offline server / editor that was running (process " + p.Id + ")");
                }
                catch { }   // not ours, or already gone
            }
        }

        public static string InstalledVersion(string game)
        {
            var f = Path.Combine(game, ServerFolder, "version.txt");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }

        /// <summary>A short description of the game folder, for the window and --status.</summary>
        public static string Describe(string game)
        {
            if (!GamePatcher.LooksLikeGame(game)) return "Not a Nine Lives folder (no Ninelives.exe here).";
            var ver = InstalledVersion(game);
            if (ver != null) return "Nine Lives Offline " + ver + " is installed here. Install again to update or change mods.";
            try
            {
                if (GamePatcher.IsPatched(game))
                {
                    var b = GamePatcher.Backup(game);
                    if (GamePatcher.Originals.Keys.All(k => File.Exists(Path.Combine(b, k))))
                        return "Found Nine Lives, already patched by the developer patcher. Installing will redo it cleanly.";
                    return "Found Nine Lives, but its files are not the original 0.24.3 ones. Setup can't patch it.";
                }
            }
            catch (Exception e) { return "Couldn't read the game files: " + e.Message; }
            return "Found Nine Lives 0.24.3. Ready to install.";
        }

        public static bool CanWrite(string game)
        {
            try
            {
                var t = Path.Combine(game, ".nlo-write-test");
                File.WriteAllText(t, "x");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        // ---------- install ----------

        public static void Install(string game, ICollection<string> modIds, bool shortcut, Action<string> log)
        {
            game = Path.GetFullPath(game);
            if (!GamePatcher.LooksLikeGame(game)) throw new InvalidOperationException("Ninelives.exe isn't in " + game + ".");
            var problem = PayloadProblem();
            if (problem != null) throw new InvalidOperationException(problem);
            if (GameRunning()) throw new InvalidOperationException("Nine Lives is running. Close the game, then try again.");
            var mods = LoadMods();
            var unknown = modIds.Where(id => !mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) throw new InvalidOperationException("Unknown mod: " + string.Join(", ", unknown));

            log("Installing Nine Lives Offline " + Version + " into " + game);
            StopOurPython(game, log);

            // 1) Patch the game and install the mod loader.
            GamePatcher.Patch(game, Path.Combine(Payload, "loader", "NineLivesModLoader.dll"), log);

            // 2) Server + its Python. Saves (save.json) are never touched.
            var server = Path.Combine(game, ServerFolder);
            Directory.CreateDirectory(server);
            foreach (var f in Directory.GetFiles(Path.Combine(Payload, "server")))
                File.Copy(f, Path.Combine(server, Path.GetFileName(f)), true);
            var py = Path.Combine(server, "python");
            if (Directory.Exists(py)) Directory.Delete(py, true);
            CopyDir(Path.Combine(Payload, "python"), py);
            File.WriteAllText(Path.Combine(server, "version.txt"), Version);
            log("Offline server installed in " + server);

            // 3) Mods: unzip the chosen ones, take the DLL out of ones that were unticked.
            foreach (var m in mods)
            {
                bool want = modIds.Any(id => id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
                if (want)
                {
                    bool update = m.InstalledIn(game);
                    Unzip(Path.Combine(Payload, "mods", m.Zip), game, update);
                    if (m.EditorLauncher != null) WriteEditorLauncher(game, m);
                    log((update ? "Updated " : "Installed ") + m.Id + " " + m.Version);
                }
                else if (m.InstalledIn(game))
                {
                    RemoveMod(game, m);
                    log("Removed " + m.Id + " (its packs and settings are kept)");
                }
            }

            // 4) Launcher, shortcut, remember the folder.
            WritePlayLauncher(game);
            log("Made \"" + PlayLauncher + "\" in the game folder");
            if (shortcut)
            {
                try { MakeShortcut(game); log("Desktop shortcut: " + Path.GetFileNameWithoutExtension(ShortcutName)); }
                catch (Exception e) { log("Couldn't make the desktop shortcut: " + e.Message); }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastGameFile));
                File.WriteAllText(LastGameFile, game);
            }
            catch { }
            log("Done. Start the game with \"" + PlayLauncher + "\"" + (shortcut ? " or the desktop shortcut." : "."));
        }

        static void RemoveMod(string game, ModEntry m)
        {
            var dll = Path.Combine(game, m.Dll);
            if (File.Exists(dll)) File.Delete(dll);
            var readme = Path.Combine(game, "Mods", m.Id + "-README.md");
            if (File.Exists(readme)) File.Delete(readme);
            var editor = Path.Combine(game, "Mods", m.Id, "editor");
            if (Directory.Exists(editor)) Directory.Delete(editor, true);
            var modDir = Path.Combine(game, "Mods", m.Id);   // only exports are left in it; those are regenerated
            if (Directory.Exists(modDir) && !Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories).Any(f => !f.Contains("\\export\\")))
                Directory.Delete(modDir, true);
            if (m.EditorLauncher != null)
            {
                var bat = Path.Combine(game, m.EditorLauncher);
                if (File.Exists(bat)) File.Delete(bat);
            }
        }

        /// <summary>
        /// Unzip a mod into the game folder. On an update, data files the player may have changed or
        /// deleted on purpose (example packs: *.json outside lib\ and editor\) are left alone.
        /// </summary>
        static void Unzip(string zip, string game, bool update)
        {
            var root = Path.GetFullPath(game).TrimEnd('\\') + "\\";
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;
                    var dest = Path.GetFullPath(Path.Combine(game, e.FullName.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the game folder
                    var rel = dest.Substring(root.Length).ToLowerInvariant();
                    bool isData = rel.EndsWith(".json") && !rel.Contains("\\editor\\") && !rel.StartsWith("mods\\lib\\");
                    if (update && isData) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static void WriteBat(string path, string text)
        {
            // cmd.exe reads .bat files in the console code page; keep them plain ASCII with CRLF.
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.ASCII);
        }

        static void WritePlayLauncher(string game)
        {
            WriteBat(Path.Combine(game, PlayLauncher),
@"@echo off
rem Nine Lives Offline: starts the offline server (minimized), then the game.
rem The server stops by itself when the game closes. Your save is OfflineServer\save.json.
cd /d ""%~dp0""
tasklist /FI ""IMAGENAME eq Ninelives.exe"" | find /I ""Ninelives.exe"" >nul
if not errorlevel 1 (
  echo Nine Lives is already running.
  pause
  exit /b
)
start ""Nine Lives offline server"" /min ""%~dp0OfflineServer\python\python.exe"" ""%~dp0OfflineServer\ninelives_server.py"" --exit-with-game
timeout /t 2 /nobreak >nul
start """" ""%~dp0Ninelives.exe""
");
        }

        static void WriteEditorLauncher(string game, ModEntry m)
        {
            WriteBat(Path.Combine(game, m.EditorLauncher),
@"@echo off
rem Opens the " + m.EditorName + @" in your browser. Close this window to stop it.
cd /d ""%~dp0..""
""OfflineServer\python\python.exe"" """ + m.EditorScript + @""" --game ""%CD%""
pause
");
        }

        static string DesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName); }
        }

        static void MakeShortcut(string game)
        {
            // WScript.Shell is on every Windows; late-bound so no COM interop assembly is needed.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(shellType);
            var lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { DesktopShortcut });
            var t = lnk.GetType();
            Action<string, object> set = (n, v) => t.InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, lnk, new[] { v });
            set("TargetPath", Path.Combine(game, PlayLauncher));
            set("WorkingDirectory", game);
            set("IconLocation", Path.Combine(game, "Ninelives.exe") + ",0");
            set("WindowStyle", 7);   // minimized: the .bat window closes right away anyway
            set("Description", "Play Nine Lives with the offline server");
            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
        }

        // ---------- uninstall ----------

        /// <summary>
        /// Put the original game files back and remove what Setup added. Without deleteData, the save
        /// (OfflineServer\save*.json) and the player's mod packs/settings in Mods\ are kept.
        /// </summary>
        public static void Uninstall(string game, bool deleteData, Action<string> log)
        {
            game = Path.GetFullPath(game);
            if (!GamePatcher.LooksLikeGame(game)) throw new InvalidOperationException("Ninelives.exe isn't in " + game + ".");
            if (GameRunning()) throw new InvalidOperationException("Nine Lives is running. Close the game, then try again.");
            log("Uninstalling Nine Lives Offline from " + game);
            StopOurPython(game, log);

            GamePatcher.Restore(game, log);

            var server = Path.Combine(game, ServerFolder);
            if (Directory.Exists(server))
            {
                if (deleteData) Directory.Delete(server, true);
                else
                {
                    foreach (var f in Directory.GetFiles(server))
                    {
                        var n = Path.GetFileName(f).ToLowerInvariant();
                        if (!(n.StartsWith("save") && n.EndsWith(".json"))) File.Delete(f);
                    }
                    foreach (var d in Directory.GetDirectories(server)) Directory.Delete(d, true);
                    if (!Directory.EnumerateFileSystemEntries(server).Any()) Directory.Delete(server);
                    else log("Kept your save in " + server);
                }
            }

            var modsDir = Path.Combine(game, "Mods");
            if (Directory.Exists(modsDir))
            {
                if (deleteData) { Directory.Delete(modsDir, true); log("Deleted the Mods folder"); }
                else
                {
                    // Remove the mods Setup put there (DLLs, editors, launchers) and Harmony. Packs, settings
                    // and any mods the player added by hand stay.
                    foreach (var m in LoadMods())
                    {
                        RemoveMod(game, m);
                        var ed = Path.Combine(modsDir, m.Id, "editor");
                        if (Directory.Exists(ed)) Directory.Delete(ed, true);
                    }
                    var harmony = Path.Combine(modsDir, "lib", "0Harmony.dll");
                    if (File.Exists(harmony)) File.Delete(harmony);
                    var lib = Path.GetDirectoryName(harmony);
                    if (Directory.Exists(lib) && !Directory.EnumerateFileSystemEntries(lib).Any()) Directory.Delete(lib);
                    log("Removed the mods (your packs and settings in Mods\\ are kept)");
                }
            }

            var play = Path.Combine(game, PlayLauncher);
            if (File.Exists(play)) File.Delete(play);
            if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
            log("Done. Nine Lives is back to the original 0.24.3 files.");
        }
    }
}
