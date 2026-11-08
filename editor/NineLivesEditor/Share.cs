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
        const long MaxPackBytes = 16L * 1024 * 1024;
        static readonly Dictionary<string, string> KindMod = new Dictionary<string, string>
        {
            { "quests", "QuestLoader" }, { "content", "ContentLoader" }, { "npcs", "NPCLoader" },
            { "enemies", "EnemyLoader" }, { "world", "WorldLoader" }, { "scripts", "ScriptLoader" },
        };
        // Things with ids of their own, which must be unique across packs: kind -> list key.
        static readonly Dictionary<string, string> IdLists = new Dictionary<string, string>
        {
            { "quests", "quests" }, { "npcs", "npcs" }, { "enemies", "enemies" },
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
        // A mod someone sent you: drop its .zip into <game>\Mods. When the editor opens (and whenever Packs & mods
        // loads), and when the offline server starts (dropmods.py, the same rules), its packs are unpacked into the
        // pack folders and the zip moves to Mods\Installed with a note of what came from it. The packs are then
        // ordinary packs: they load in game and open in the editor. Only pack files, never code.
        class Incoming { public string Kind, Name; public object Data; public string Problem; }
        static readonly Regex CodeFile = new Regex(@"\.(dll|exe|bat|cmd|ps1|vbs|js|scr|msi)$", RegexOptions.IgnoreCase);
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

        /// <summary>Unpack every zip dropped straight into Mods. Returns the notes of the ones installed now.</summary>
        List<Dictionary<string, object>> InstallDropped()
        {
            var now = new List<Dictionary<string, object>>();
            string[] zips;
            try { zips = Directory.Exists(ModsDir()) ? Directory.GetFiles(ModsDir(), "*.zip") : new string[0]; } catch { return now; }
            lock (writeGate)
                foreach (var z in zips.OrderBy(f => f))
                {
                    try { var n = InstallZip(z); if (n != null) now.Add(n); }
                    catch (IOException) { }   // still being copied in: next time
                    catch (UnauthorizedAccessException) { }
                    catch (Exception) { }
                }
            return now;
        }

        Dictionary<string, object> InstallZip(string path)
        {
            var zname = Path.GetFileName(path);
            byte[] bytes;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { bytes = new byte[f.Length]; int got = 0; while (got < bytes.Length) { int r = f.Read(bytes, got, bytes.Length - got); if (r <= 0) break; got += r; } }
            Dictionary<string, object> manifest; List<Incoming> packs; List<string> ignored;
            try { Read(bytes, out manifest, out packs, out ignored); }
            catch (InvalidDataException) { return null; }   // not a zip (or not finished copying)
            if (ignored.Any(n => CodeFile.IsMatch(n)) || packs.Count == 0) return null;   // a code mod, or no packs: Setup's business, left alone
            var done = new List<object>(); var warnings = new List<string>();
            foreach (var p in packs)
            {
                var folder = Kinds[p.Kind][0];
                if (p.Problem != null)
                {
                    done.Add(new Dictionary<string, object> { { "folder", folder }, { "name", p.Name }, { "result", "skipped" }, { "problem", p.Problem } });
                    warnings.Add(folder + "\\" + p.Name + " " + p.Problem.Replace("it's ", "is ").Replace("it has ", "has ") + ", so it was left out");
                    continue;
                }
                var on = Path.Combine(Folder(p.Kind), p.Name + ".json");
                var mine = File.Exists(on) ? on : File.Exists(on + Off) ? on + Off : null;
                if (mine != null && Json.Pretty(Json.Load(mine)) == Json.Pretty(p.Data))
                {
                    done.Add(new Dictionary<string, object> { { "folder", folder }, { "name", p.Name }, { "result", "same" } });
                    continue;
                }
                if (mine != null) { File.Copy(mine, on + ".bak", true); if (mine != on) File.Delete(mine); }   // a newer version of it: yours is kept as .bak
                var tmp = on + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try { File.WriteAllText(tmp, Json.Pretty(p.Data), new UTF8Encoding(false)); if (File.Exists(on)) File.Replace(tmp, on, null); else File.Move(tmp, on); }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                done.Add(new Dictionary<string, object> { { "folder", folder }, { "name", p.Name }, { "result", mine != null ? "replaced" : "added" } });
                var ids = new HashSet<long>(IdsIn(p.Kind, p.Data));
                if (ids.Count > 0)
                    foreach (var other in Packs(p.Kind).Select(x => x.Key).Where(x => !x.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var same = IdsIn(p.Kind, Json.Load(PackFile(p.Kind, other))).Where(ids.Contains).Distinct().OrderBy(x => x).ToList();
                        if (same.Count > 0) warnings.Add(folder + "\\" + p.Name + " uses the same id" + (same.Count > 1 ? "s" : "") + " as your pack " + other + " (" + string.Join(", ", same.Take(6).Select(x => x.ToString()).ToArray()) + "): one of them won't work in game while both are on");
                    }
            }
            Directory.CreateDirectory(InstalledDir);
            var note = new Dictionary<string, object>
            {
                { "name", Json.Str(manifest, "name") ?? Path.GetFileNameWithoutExtension(zname) }, { "author", Json.Str(manifest, "author") ?? "" },
                { "description", Json.Str(manifest, "description") ?? "" }, { "made", Json.Str(manifest, "created") ?? "" },
                { "madeWith", Json.Get(manifest, "mods") ?? new Dictionary<string, object>() }, { "zip", zname },
                { "installed", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss") }, { "packs", done }, { "warnings", warnings },
                { "notUnpacked", ignored.Take(20).ToList() },
            };
            File.WriteAllText(Path.Combine(InstalledDir, Path.GetFileNameWithoutExtension(zname) + ".json"), Json.Pretty(note), new UTF8Encoding(false));
            var dest = Path.Combine(InstalledDir, zname);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(path, dest);
            return note;
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

        static void Read(byte[] body, out Dictionary<string, object> manifest, out List<Incoming> packs, out List<string> ignored)
        {
            manifest = null; packs = new List<Incoming>(); ignored = new List<string>();
            using (var zip = new ZipArchive(new MemoryStream(body ?? new byte[0]), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    var path = e.FullName.Replace('\\', '/').TrimStart('/');
                    if (path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || path.EndsWith("/" + ManifestName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { manifest = Json.Obj(Json.Parse(ReadEntry(e))); } catch { }
                        continue;
                    }
                    if (path.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) continue;
                    // Mods/<Folder>/<name>.json, or <Folder>/<name>.json; a zip of a folder may put it all one level down
                    var m = Regex.Match(path, @"(?:^|/)(?:Mods/)?(Quests|Content|NPCs|Enemies|World|Scripts)/([^/]+)\.json(\.disabled)?$", RegexOptions.IgnoreCase);
                    var kind = m.Success ? KindOfFolder(m.Groups[1].Value) : null;
                    if (kind == null) { ignored.Add(path); continue; }
                    // a name with other characters is tidied up (they become -)
                    var nm = Regex.Replace(m.Groups[2].Value, @"[^A-Za-z0-9 _\-]", "-").Trim(' ', '-');
                    if (nm.Length > 60) nm = nm.Substring(0, 60);
                    var inc = new Incoming { Kind = kind, Name = nm.Length > 0 ? nm : "imported" };
                    if (e.Length > MaxPackBytes) inc.Problem = "it's too big (over 16 MB)";
                    else
                    {
                        try
                        {
                            inc.Data = Json.Parse(ReadEntry(e));
                            if (Json.Obj(inc.Data) == null) throw new Exception("expected a JSON object");
                            var lk = Kinds[kind][1];
                            if (lk != null && Json.List(Json.Get(inc.Data, lk)) == null) throw new Exception("expected {name, " + lk + ": [...]}");
                        }
                        catch (Exception ex) { inc.Problem = "it has a mistake in it: " + ex.Message; }
                    }
                    if (packs.Any(p => p.Kind == inc.Kind && p.Name.Equals(inc.Name, StringComparison.OrdinalIgnoreCase))) continue;   // on and off copies: the first
                    packs.Add(inc);
                }
            }
        }

        static string ReadEntry(ZipArchiveEntry e) { using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd(); }

        static IEnumerable<long> IdsIn(string kind, object data)
        {
            string lk; if (!IdLists.TryGetValue(kind, out lk)) yield break;
            var l = Json.List(Json.Get(data, lk)); if (l == null) yield break;
            foreach (var it in l) { var id = Json.Num(it, "id"); if (id != 0) yield return id; }
        }
    }
}
