using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NineLivesEditor
{
    /// <summary>
    /// Sharing mods: export some packs as one .zip; such a .zip dropped into the Mods folder is installed.
    ///
    /// The zip unzips straight into the game folder too (Mods\Quests\x.json …), so it works without the
    /// editor. At its root: nine-lives-mod.json (name, author, what's in it, which mods it was made with)
    /// and README.txt. Only pack files (.json in the six pack folders) are ever imported, never code.
    /// </summary>
    partial class EditorServer
    {
        const string ManifestName = "nine-lives-mod.json";
        static readonly Dictionary<string, string> KindMod = new Dictionary<string, string>
        {
            { "quests", "QuestLoader" }, { "content", "ContentLoader" }, { "npcs", "NPCLoader" },
            { "enemies", "EnemyLoader" }, { "world", "WorldLoader" }, { "scripts", "ScriptLoader" },
        };

        string ModVersion(string mod)
        {
            var dll = ModsDir(mod + ".dll");
            if (!File.Exists(dll)) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(dll).FileVersion;
                var parts = (v ?? "").Split('.');
                return parts.Length >= 3 ? string.Join(".", parts.Take(3).ToArray()) : v;
            }
            catch { return "?"; }
        }

        static int CompareVersions(string a, string b)
        {
            Func<string, int[]> p = s => (s ?? "0").Split('.').Select(x => { int n; return int.TryParse(x, out n) ? n : 0; }).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
            var x1 = p(a); var y1 = p(b);
            for (int i = 0; i < 3; i++) if (x1[i] != y1[i]) return x1[i].CompareTo(y1[i]);
            return 0;
        }

        static string KindOfFolder(string folder) { return Kinds.Where(k => k.Value[0].Equals(folder, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).FirstOrDefault(); }

        // ------------------------------------------------------------ export
        Response ShareGet(string[] parts, Dictionary<string, string> q)
        {
            if (parts.Length < 3) return Error(404, "not found");
            if (parts[2] == "related") return JsonObj(Related(SelectedPacks(Q(q, "packs"))));
            if (parts[2] != "export") return Error(404, "not found");

            var picked = SelectedPacks(Q(q, "packs"));
            if (picked.Count == 0) return Error(400, "Pick at least one pack");
            var name = Q(q, "name"); if (name.Length == 0) name = picked[0].Value;
            if (name.Length > 80) name = name.Substring(0, 80);
            var author = Q(q, "author"); if (author.Length > 80) author = author.Substring(0, 80);
            var description = Q(q, "description"); if (description.Length > 2000) description = description.Substring(0, 2000);

            var used = new SortedDictionary<string, object>();
            var list = new List<object>();
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var p in picked)
                {
                    var file = PackFile(p.Key, p.Value);
                    if (file == null || !File.Exists(file)) return Error(404, "No pack \"" + p.Value + "\" in " + Kinds[p.Key][0]);
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    try { Json.Parse(text); } catch (Exception e) { return Error(400, "The pack \"" + p.Value + "\" has a mistake in it, so it can't be shared yet: " + e.Message); }
                    var path = "Mods/" + Kinds[p.Key][0] + "/" + p.Value + ".json";   // always switched on in the zip
                    using (var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false))) w.Write(text);
                    list.Add(new Dictionary<string, object> { { "kind", p.Key }, { "name", p.Value }, { "file", path }, { "count", PackSize(p.Key, p.Value) } });
                    var mod = KindMod[p.Key];
                    if (!used.ContainsKey(mod)) used[mod] = ModVersion(mod) ?? "";
                }
                var manifest = new Dictionary<string, object>
                {
                    { "format", "nine-lives-mod" }, { "formatVersion", 1 },
                    { "name", name }, { "author", author }, { "description", description },
                    { "created", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
                    { "editor", typeof(EditorServer).Assembly.GetName().Version.ToString(3) },
                    { "mods", used }, { "packs", list },
                };
                using (var w = new StreamWriter(zip.CreateEntry(ManifestName).Open(), new UTF8Encoding(false))) w.Write(Json.Pretty(manifest));
                var readme = new StringBuilder();
                readme.AppendLine(name + (author.Length > 0 ? " - by " + author : "")).AppendLine();
                if (description.Length > 0) readme.AppendLine(description).AppendLine();
                readme.AppendLine("A mod for Nine Lives Offline (https://github.com/THICC-debug/ninelives-offline-server).").AppendLine();
                readme.AppendLine("To install: open the Nine Lives Editor, go to Packs & mods and click Import a mod, then pick this zip.");
                readme.AppendLine("Or unzip it into the game folder (the one with Ninelives.exe).").AppendLine();
                readme.AppendLine("It needs these mods (install them with Setup.exe): " + string.Join(", ", used.Select(u => u.Key + (((string)u.Value).Length > 0 ? " " + u.Value : "")).ToArray()));
                readme.AppendLine("Inside:");
                foreach (var p in picked) readme.AppendLine("  Mods\\" + Kinds[p.Key][0] + "\\" + p.Value + ".json");
                using (var w = new StreamWriter(zip.CreateEntry("README.txt").Open(), new UTF8Encoding(false))) w.Write(readme.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
            }
            var fileName = Regex.Replace(name, @"[^A-Za-z0-9 _\-]", "").Trim();
            if (fileName.Length == 0) fileName = "nine-lives-mod";
            return new Response { Body = ms.ToArray(), Type = "application/zip", Disposition = "attachment; filename=\"" + fileName + ".zip\"" };
        }

        /// <summary>"quests:My pack,npcs:Town folk" -> (kind, name), known kinds and valid names only.</summary>
        List<KeyValuePair<string, string>> SelectedPacks(string spec)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var part in (spec ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = part.IndexOf(':'); if (c <= 0) continue;
                string kind = part.Substring(0, c), name = part.Substring(c + 1);
                if (Kinds.ContainsKey(kind) && PackName.IsMatch(name) && !list.Any(p => p.Key == kind && p.Value == name)) list.Add(new KeyValuePair<string, string>(kind, name));
            }
            return list;
        }

        /// <summary>Packs the picked ones lean on but that aren't picked: the NPC packs of quest givers and enders.</summary>
        List<object> Related(List<KeyValuePair<string, string>> picked)
        {
            var npcIds = new HashSet<long>();
            foreach (var p in picked.Where(x => x.Key == "quests"))
                foreach (var qu in PackItems("quests", p.Value))
                    foreach (var k in new[] { "giver", "ender" }) { var id = Json.Num(qu, k); if (id >= 90000) npcIds.Add(id); }
            var out_ = new List<object>();
            foreach (var pk in Packs("npcs"))
            {
                if (picked.Any(x => x.Key == "npcs" && x.Value == pk.Key)) continue;
                var who = PackItems("npcs", pk.Key).Where(n => npcIds.Contains(Json.Num(n, "id"))).Select(n => Json.Str(n, "name") ?? "?").ToList();
                if (who.Count > 0) out_.Add(new Dictionary<string, object> { { "kind", "npcs" }, { "name", pk.Key }, { "why", "has " + string.Join(", ", who.ToArray()) + (who.Count == 1 ? ", who gives or ends" : ", who give or end") + " your quests" } });
            }
            return out_;
        }

        // ------------------------------------------------------------ mods dropped into the Mods folder
        // A mod someone sent you: drop its .zip into <game>\Mods. The offline server installs it before the game
        // starts (dropmods.py), and the editor runs the same dropmods.py when it opens and while Packs & mods is
        // open: its packs go into the pack folders (renumbered where they'd clash with yours) and the zip moves to
        // Mods\Installed with a note of what came from it. Only pack files, never code.
        string InstalledDir { get { return ModsDir("Installed"); } }

        Response SharePost(string[] parts, Dictionary<string, string> q, byte[] body)
        {
            if (parts[2] == "openfolder")   // Packs & mods: "Open the Mods folder" (to drop a mod in)
            {
                Directory.CreateDirectory(ModsDir());
                Process.Start("explorer.exe", "\"" + ModsDir() + "\"");
                return JsonObj(new Dictionary<string, object> { { "ok", true } });
            }
            return Error(404, "not found");
        }

        /// <summary>
        /// Install every zip dropped straight into Mods. The rules (what's unpacked, renumbering so mods don't
        /// clash) live in one place, the offline server's dropmods.py, run here with the server's own Python.
        /// Returns the notes of the ones installed now.
        /// </summary>
        List<Dictionary<string, object>> InstallDropped()
        {
            var now = new List<Dictionary<string, object>>();
            try { if (!Directory.Exists(ModsDir()) || Directory.GetFiles(ModsDir(), "*.zip").Length == 0) return now; } catch { return now; }
            var server = Path.Combine(game, "OfflineServer");
            var py = Path.Combine(server, "python", "python.exe");
            var script = Path.Combine(server, "dropmods.py");
            if (!File.Exists(py) || !File.Exists(script)) return now;   // installed the next time you play instead
            lock (writeGate)
            {
                var before = Notes().ToDictionary(n => Json.Str(n, "zip") ?? "", n => Json.Str(n, "installed") ?? "");
                try
                {
                    using (var p = Process.Start(new ProcessStartInfo(py, "-I \"" + script + "\" \"" + game + "\"")
                        { WorkingDirectory = server, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                    {
                        p.StandardError.ReadToEndAsync();
                        p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(30000)) try { p.Kill(); } catch { }
                    }
                }
                catch { return now; }
                foreach (var n in Notes())
                {
                    string was;
                    if (!before.TryGetValue(Json.Str(n, "zip") ?? "", out was) || was != (Json.Str(n, "installed") ?? "")) now.Add(n);
                }
            }
            return now;
        }

        IEnumerable<Dictionary<string, object>> Notes()
        {
            if (!Directory.Exists(InstalledDir)) yield break;
            foreach (var f in Directory.GetFiles(InstalledDir, "*.json"))
            {
                var n = Json.Obj(Json.Load(f));
                if (n != null) yield return n;
            }
        }

        /// <summary>
        /// Quest numbers and flags other packs use: {packs: [{pack, quests, flags}], known: [quest ids the game has
        /// seen from packs]}. The quest editor gives new quests numbers no pack uses (dropmods.py renumbers on install too).
        /// </summary>
        object TakenQuestIds()
        {
            var packs = new List<object>();
            Func<object, long> num = o => { try { return Convert.ToInt64(o); } catch { return 0; } };
            foreach (var p in Packs("quests"))
            {
                var ids = new List<long>(); var flags = new List<long>();
                foreach (var qu in PackItems("quests", p.Key))
                {
                    ids.Add(Json.Num(qu, "id"));
                    var cf = Json.Num(qu, "clearFlag"); if (cf > 0) flags.Add(cf);
                    foreach (var f in (Json.List(Json.Get(qu, "setFlagsOnClear")) ?? new object[0])) { var v = Json.Num(f, "flag"); if (v > 0) flags.Add(v); }
                }
                packs.Add(new Dictionary<string, object> { { "pack", p.Key }, { "quests", ids }, { "flags", flags } });
            }
            var scriptFlags = new List<long>();
            foreach (var p in Packs("scripts"))
            {
                var text = Json.ReadText(PackFile("scripts", p.Key)) ?? "";
                foreach (Match m in Regex.Matches(text, @"""type""\s*:\s*""setFlag""[^{}]*?""flag""\s*:\s*(\d+)|""flag""\s*:\s*(\d+)[^{}]*?""type""\s*:\s*""setFlag"""))
                    scriptFlags.Add(long.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            }
            packs.Add(new Dictionary<string, object> { { "pack", "" }, { "quests", new List<long>() }, { "flags", scriptFlags } });
            var known = Json.Obj(Json.Load(ExportPath("QuestLoader", "known_quests.json")));
            return new Dictionary<string, object> { { "packs", packs },
                { "known", known == null ? new List<object>() : known.Select(k => (object)new Dictionary<string, object> { { "id", num(k.Key) }, { "pack", Json.Str(known, k.Key) ?? "" } }).ToList() } };
        }

        /// <summary>The mods you've added (Mods\Installed\*.json), newest first, with the ones just installed marked.</summary>
        object AddedMods(List<Dictionary<string, object>> justNow)
        {
            var list = new List<object>();
            if (Directory.Exists(InstalledDir))
                foreach (var f in Directory.GetFiles(InstalledDir, "*.json"))
                {
                    var n = Json.Obj(Json.Load(f)); if (n == null) continue;
                    n["new"] = justNow.Any(j => Json.Str(j, "zip") == Json.Str(n, "zip"));
                    list.Add(n);
                }
            return list.OrderByDescending(n => Json.Str(n, "installed") ?? "").ToList();
        }
    }
}
