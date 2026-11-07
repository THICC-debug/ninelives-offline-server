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
        public string Id, Version, Zip, Description;
        public bool Default;
        public string Dll { get { return Path.Combine("Mods", Id + ".dll"); } }
        public bool InstalledIn(string game) { return File.Exists(Path.Combine(game, Dll)); }
    }

    /// <summary>
    /// What Setup does, without any UI (the window and the command line both call this).
    ///
    /// Layout next to Setup.exe:
    ///   files\loader\   NineLivesModLoader.dll, 0Harmony.dll
    ///   files\server\   ninelives_server.py, aes.py, recipes.json, docs
    ///   files\python\   the official Python embeddable package (runs the server)
    ///   files\mods\     one release zip per mod, laid out to unzip into the game folder
    ///   files\editor\   the Nine Lives Editor (with the mods)
    ///   files\mods.txt  id | version | zip | default | description
    ///
    /// Installed into the game folder:
    ///   Ninelives_Data\Managed\*   patched (originals backed up to Ninelives_Data\OfflineBackup)
    ///   OfflineServer\             server, its Python, and the save (save.json)
    ///   Mods\                      mod loader's folder and the chosen mods
    ///   Nine Lives Editor.exe      the editor (one exe, no Python needed)
    ///   Play Nine Lives Offline.bat
    /// </summary>
    public static class Installer
    {
        public const string ServerFolder = "OfflineServer";
        public const string PlayLauncher = "Play Nine Lives Offline.bat";
        public const string ShortcutName = "Nine Lives (Offline).lnk";
        public const string EditorExe = "Nine Lives Editor.exe";
        public const string EditorShortcutName = "Nine Lives Editor.lnk";
        // Editors from earlier versions: the per-mod ones (1.0.x) and the Python-based all-in-one.
        static readonly string[] OldEditorFiles = { "Mods\\Open Quest Editor.bat", "Mods\\Open Content Editor.bat", "Open Nine Lives Editor.bat" };
        static readonly string[] OldEditorFolders = { "Mods\\QuestLoader\\editor", "Mods\\ContentLoader\\editor", "Editor" };
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

        /// <summary>Is this game's editor open? (It may have unsaved changes, so Setup asks instead of closing it.)</summary>
        static bool EditorRunning(string game)
        {
            var exe = Path.Combine(game, EditorExe);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(EditorExe)))
                try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            return false;
        }

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
            if (EditorRunning(game)) throw new InvalidOperationException("The Nine Lives Editor is open. Save your changes and close it, then try again.");
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
                    log((update ? "Updated " : "Installed ") + m.Id + " " + m.Version);
                }
                else if (m.InstalledIn(game))
                {
                    RemoveMod(game, m);
                    log("Removed " + m.Id + " (its packs and settings are kept)");
                }
            }

            // 4) The Nine Lives Editor (shipped with the mods), replacing the old per-mod editors.
            RemoveOldEditors(game);
            var editorSrc = Path.Combine(Payload, "editor", "NineLivesEditor.exe");
            if (File.Exists(editorSrc))
            {
                File.Copy(editorSrc, Path.Combine(game, EditorExe), true);
                log("Nine Lives Editor installed: \"" + EditorExe + "\" in the game folder");
                if (shortcut)
                {
                    try { MakeShortcut(Path.Combine(DesktopDir, EditorShortcutName), Path.Combine(game, EditorExe), game, Path.Combine(game, "Ninelives.exe") + ",0", "Edit quests, items, loot, shops and NPCs", 1); }
                    catch (Exception e) { log("Couldn't make the editor's desktop shortcut: " + e.Message); }
                }
            }

            // 5) Launcher, shortcut, remember the folder.
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
            var modDir = Path.Combine(game, "Mods", m.Id);   // only exports are left in it; those are regenerated
            if (Directory.Exists(modDir) && !Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories).Any(f => !f.Contains("\\export\\")))
                Directory.Delete(modDir, true);
        }

        /// <summary>
        /// Unzip a mod into the game folder. On an update, data files the player may have changed or
        /// deleted on purpose (example packs: *.json and *.json.disabled outside lib\) are left alone.
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
                    bool isData = (rel.EndsWith(".json") || rel.EndsWith(".json.disabled")) && !rel.StartsWith("mods\\lib\\");
                    if (update && isData) continue;
                    // A switched-off example: skip it if the player already has it, switched on or off.
                    if (rel.EndsWith(".json.disabled") && (File.Exists(dest) || File.Exists(dest.Substring(0, dest.Length - ".disabled".Length)))) continue;
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

        static void RemoveOldEditors(string game)
        {
            foreach (var old in OldEditorFiles) { var f = Path.Combine(game, old); if (File.Exists(f)) File.Delete(f); }
            foreach (var old in OldEditorFolders) { var d = Path.Combine(game, old); if (Directory.Exists(d)) Directory.Delete(d, true); }
        }

        static string DesktopDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }
        static string DesktopShortcut { get { return Path.Combine(DesktopDir, ShortcutName); } }

        static void MakeShortcut(string game)
        {
            // Minimized: the .bat's own window closes right away anyway.
            MakeShortcut(DesktopShortcut, Path.Combine(game, PlayLauncher), game, Path.Combine(game, "Ninelives.exe") + ",0", "Play Nine Lives with the offline server", 7);
        }

        static void MakeShortcut(string lnkPath, string target, string workDir, string icon, string description, int windowStyle)
        {
            // WScript.Shell is on every Windows; late-bound so no COM interop assembly is needed.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(shellType);
            var lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            var t = lnk.GetType();
            Action<string, object> set = (n, v) => t.InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, lnk, new[] { v });
            set("TargetPath", target);
            set("WorkingDirectory", workDir);
            set("IconLocation", icon);
            set("WindowStyle", windowStyle);
            set("Description", description);
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
            if (EditorRunning(game)) throw new InvalidOperationException("The Nine Lives Editor is open. Save your changes and close it, then try again.");
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

            RemoveOldEditors(game);
            var editorExe = Path.Combine(game, EditorExe);
            if (File.Exists(editorExe)) File.Delete(editorExe);
            var editorLnk = Path.Combine(DesktopDir, EditorShortcutName);
            if (File.Exists(editorLnk)) File.Delete(editorLnk);

            var play = Path.Combine(game, PlayLauncher);
            if (File.Exists(play)) File.Delete(play);
            if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
            log("Done. Nine Lives is back to the original 0.24.3 files.");
        }
    }
}
