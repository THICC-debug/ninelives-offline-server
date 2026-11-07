using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NineLivesEditor
{
    /// <summary>
    /// The editor's local web server: serves the pages (built into the exe) and the API the pages use
    /// to read the mods' exports and read/write packs. Listens on 127.0.0.1 only.
    /// </summary>
    class EditorServer
    {
        const string Off = ".disabled";
        static readonly Regex PackName = new Regex(@"^[A-Za-z0-9 _\-]{1,60}$");
        static readonly string[] Mods = { "TravelMod", "QuestLoader", "ContentLoader", "NPCLoader", "DevTools" };

        // kind -> (folder under Mods, list key in a pack, or null)
        static readonly Dictionary<string, string[]> Kinds = new Dictionary<string, string[]>
        {
            { "quests", new[] { "Quests", "quests" } },
            { "content", new[] { "Content", null } },
            { "npcs", new[] { "NPCs", "npcs" } },
        };

        static readonly Dictionary<string, string> Pages = new Dictionary<string, string>
        {
            { "/", "index.html" }, { "/index.html", "index.html" }, { "/home", "home.html" }, { "/overview", "overview.html" },
            { "/quests", "quests.html" }, { "/content", "content.html" }, { "/npcs", "npcs.html" }, { "/style.css", "style.css" },
        };

        readonly string game;
        readonly TcpListener listener;
        public int Port { get; private set; }

        public EditorServer(string game, int port)
        {
            this.game = game;
            listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true, Name = "EditorServer" }.Start();
        }

        public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

        void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        // ------------------------------------------------------------ HTTP (just enough of it)
        void Serve(TcpClient client)
        {
            using (client)
            using (var s = client.GetStream())
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    string method, target; Dictionary<string, string> headers; byte[] body;
                    if (!ReadRequest(s, out method, out target, out headers, out body)) return;
                    int q = target.IndexOf('?');
                    var path = q < 0 ? target : target.Substring(0, q);
                    var query = ParseQuery(q < 0 ? "" : target.Substring(q + 1));
                    Response r;
                    try { r = path.StartsWith("/api/live/") ? Live(method, target.Substring("/api/live".Length), body)
                                                            : method == "POST" ? Post(path, query, body) : Get(path, query); }
                    catch (Exception e) { r = Error(500, e.Message); }
                    var head = "HTTP/1.1 " + r.Code + " " + (r.Code == 200 ? "OK" : "Error") + "\r\n" +
                               "Content-Type: " + r.Type + "\r\nContent-Length: " + r.Body.Length + "\r\n" +
                               "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(head);
                    s.Write(hb, 0, hb.Length);
                    s.Write(r.Body, 0, r.Body.Length);
                }
                catch (IOException) { }
            }
        }

        static bool ReadRequest(Stream s, out string method, out string target, out Dictionary<string, string> headers, out byte[] body)
        {
            method = target = null; headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); body = new byte[0];
            var buf = new MemoryStream();
            uint last4 = 0; int b;
            while ((b = s.ReadByte()) >= 0)                     // headers end with \r\n\r\n
            {
                buf.WriteByte((byte)b);
                last4 = (last4 << 8) | (uint)b;
                if (last4 == 0x0D0A0D0A) break;
                if (buf.Length > 65536) return false;
            }
            if (last4 != 0x0D0A0D0A) return false;
            var lines = Encoding.UTF8.GetString(buf.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return false;
            method = first[0].ToUpperInvariant(); target = first[1];
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
            string cl;
            if (headers.TryGetValue("Content-Length", out cl))
            {
                int n = int.Parse(cl);
                if (n > 64 * 1024 * 1024) return false;
                body = new byte[n];
                int got = 0;
                while (got < n) { int r = s.Read(body, got, n - got); if (r <= 0) break; got += r; }
            }
            return true;
        }

        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int e = part.IndexOf('=');
                var k = Uri.UnescapeDataString((e < 0 ? part : part.Substring(0, e)).Replace('+', ' '));
                var v = e < 0 ? "" : Uri.UnescapeDataString(part.Substring(e + 1).Replace('+', ' '));
                d[k] = v.Trim();
            }
            return d;
        }

        class Response { public int Code = 200; public string Type = "application/json; charset=utf-8"; public byte[] Body; }
        static Response JsonText(string json, int code = 200) { return new Response { Code = code, Body = Encoding.UTF8.GetBytes(json) }; }
        static Response JsonObj(object o, int code = 200) { return JsonText(Json.Write(o), code); }
        static Response Error(int code, string msg) { return JsonObj(new Dictionary<string, object> { { "error", msg } }, code); }
        static string Q(Dictionary<string, string> q, string k) { string v; return q.TryGetValue(k, out v) ? v : ""; }

        // ------------------------------------------------------------ live link to the game
        /// <summary>
        /// Pass a request on to NPCLoader's live link inside the running game (127.0.0.1:8099), so the
        /// NPCs tab can show changes in the world as you make them.
        /// </summary>
        static Response Live(string method, string target, byte[] body)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8099" + target);
                req.Method = method;
                req.Timeout = req.ReadWriteTimeout = 10000;
                req.Proxy = null;
                if (method == "POST")
                {
                    req.ContentType = "application/json";
                    req.ContentLength = body.Length;
                    using (var w = req.GetRequestStream()) w.Write(body, 0, body.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var text = r.ReadToEnd();
                    // Mark it as coming from the game, so the page knows it's connected.
                    return JsonText(text.StartsWith("{") ? "{\"connected\":true," + text.Substring(1).TrimStart() : text);
                }
            }
            catch (WebException e)
            {
                var msg = e.Status == WebExceptionStatus.ConnectFailure
                    ? "The game isn't running (or NPCLoader 1.2 isn't installed)."
                    : "The game didn't answer: " + e.Message;
                return JsonObj(new Dictionary<string, object> { { "connected", false }, { "error", msg } });
            }
        }

        // ------------------------------------------------------------ files
        string ModsDir(params string[] parts) { return Path.Combine(new[] { game, "Mods" }.Concat(parts).ToArray()); }
        string ExportPath(string mod, string file) { return ModsDir(mod, "export", file); }
        string ExportRaw(string mod, string file, string fallback) { return Json.ReadText(ExportPath(mod, file)) ?? fallback; }

        string Folder(string kind)
        {
            var d = ModsDir(Kinds[kind][0]);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Every pack of a kind, switched on or off.</summary>
        List<KeyValuePair<string, bool>> Packs(string kind)
        {
            var found = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(Folder(kind)).Select(Path.GetFileName))
            {
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) found[f.Substring(0, f.Length - 5)] = true;
                else if (f.EndsWith(".json" + Off, StringComparison.OrdinalIgnoreCase))
                {
                    var n = f.Substring(0, f.Length - 5 - Off.Length);
                    if (!found.ContainsKey(n)) found[n] = false;
                }
            }
            return found.ToList();
        }

        /// <summary>The pack's file, whichever exists (on or off); a new pack starts switched on.</summary>
        string PackFile(string kind, string name)
        {
            if (!Kinds.ContainsKey(kind) || name == null || !PackName.IsMatch(name)) return null;
            var on = Path.Combine(Folder(kind), name + ".json");
            return !File.Exists(on) && File.Exists(on + Off) ? on + Off : on;
        }

        IEnumerable<object> PackItems(string kind, string name)
        {
            var l = Json.List(Json.Get(Json.Load(PackFile(kind, name)), Kinds[kind][1]));
            return l == null ? Enumerable.Empty<object>() : l.Cast<object>();
        }

        List<Dictionary<string, object>> GameNpcs()
        {
            // QuestLoader's list also picks up mod NPCs it saw in the world: skip those.
            var l = Json.List(Json.Load(ExportPath("QuestLoader", "npcs.json")));
            return l == null ? new List<Dictionary<string, object>>()
                             : l.Cast<object>().Select(Json.Obj).Where(n => n != null && Json.Num(n, "cid") > 0 && Json.Num(n, "cid") < 90000).ToList();
        }

        List<Dictionary<string, object>> CustomNpcs()
        {
            var out_ = new List<Dictionary<string, object>>();
            foreach (var p in Packs("npcs"))
                foreach (var n in PackItems("npcs", p.Key))
                    out_.Add(new Dictionary<string, object> {
                        { "cid", Json.Get(n, "id") }, { "name", Json.Str(n, "name") ?? "?" }, { "zone", Json.Str(n, "zone") ?? "" },
                        { "x", Json.Get(n, "x") ?? 0 }, { "y", Json.Get(n, "y") ?? 0 }, { "z", Json.Get(n, "z") ?? 0 },
                        { "custom", true }, { "pack", p.Key }, { "enabled", p.Value && !false.Equals(Json.Get(n, "enabled")) } });
            return out_;
        }

        List<Dictionary<string, object>> PackQuests()
        {
            var out_ = new List<Dictionary<string, object>>();
            foreach (var p in Packs("quests"))
                foreach (var q in PackItems("quests", p.Key))
                    out_.Add(new Dictionary<string, object> {
                        { "id", Json.Get(q, "id") }, { "title", Json.Str(q, "title") }, { "giver", Json.Get(q, "giver") },
                        { "ender", Json.Get(q, "ender") }, { "pack", p.Key }, { "enabled", p.Value } });
            return out_;
        }

        int PackSize(string kind, string name)
        {
            var d = Json.Load(PackFile(kind, name));
            if (kind != "content") { var l = Json.List(Json.Get(d, Kinds[kind][1])); return l == null ? 0 : l.Count; }
            Func<object, int> count = o => { var x = Json.Obj(o); return x == null ? 0 : x.Count; };
            return count(Json.Get(d, "items")) + new[] { "loot", "shops" }.Sum(t => count(Json.Get(Json.Get(d, t), "drops")) + count(Json.Get(Json.Get(d, t), "groups")));
        }

        Dictionary<string, bool> ModCfg()
        {
            var cfg = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var t = Json.ReadText(ModsDir("mods.cfg"));
            if (t != null)
                foreach (Match m in Regex.Matches(t, @"^\s*([A-Za-z0-9_]+)\s*=\s*(true|false)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    cfg[m.Groups[1].Value] = m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return cfg;
        }

        // ------------------------------------------------------------ GET
        Response Get(string path, Dictionary<string, string> q)
        {
            string page;
            if (Pages.TryGetValue(path, out page))
            {
                var bytes = Resources.Web(page);
                if (bytes == null) return Error(404, "missing page " + page);
                return new Response { Body = bytes, Type = page.EndsWith(".css") ? "text/css; charset=utf-8" : "text/html; charset=utf-8" };
            }
            var parts = path.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0] != "api") return Error(404, "not found");

            if (parts[1] == "overview")
            {
                var cfg = ModCfg();
                return JsonObj(new Dictionary<string, object> {
                    { "game", game },
                    { "mods", Mods.Select(m => new Dictionary<string, object> {
                        { "id", m }, { "installed", File.Exists(ModsDir(m + ".dll")) }, { "enabled", !cfg.ContainsKey(m) || cfg[m] } }).ToList() },
                    { "packs", Kinds.Keys.ToDictionary(k => k, k => (object)Packs(k).Select(p => new Dictionary<string, object> {
                        { "name", p.Key }, { "enabled", p.Value }, { "size", PackSize(k, p.Key) } }).ToList()) },
                    { "exported", new Dictionary<string, object> {
                        { "quests", File.Exists(ExportPath("QuestLoader", "quests.json")) },
                        { "content", File.Exists(ExportPath("ContentLoader", "loot.json")) } } },
                });
            }
            if (parts[1] == "index") return JsonObj(SearchIndex());

            var kind = parts[1];
            if (!Kinds.ContainsKey(kind)) return Error(404, "not found");
            var packs = Packs(kind);
            var names = packs.Select(p => p.Key).ToList();
            var off = packs.Where(p => !p.Value).Select(p => p.Key).ToList();

            if (parts.Length == 3 && parts[2] == "data")
            {
                // Big exports are passed through as they are, without parsing them.
                var sb = new StringBuilder("{");
                Action<string, string> raw = (k, json) => { if (sb.Length > 1) sb.Append(','); sb.Append(Json.Write(k)).Append(':').Append(json); };
                raw("game", Json.Write(game)); raw("packs", Json.Write(names)); raw("packsOff", Json.Write(off));
                if (kind == "quests")
                {
                    raw("originals", ExportRaw("QuestLoader", "quests.json", "[]"));
                    raw("items", ExportRaw("QuestLoader", "items.json", "[]"));
                    raw("npcs", Json.Write(GameNpcs().Concat(CustomNpcs().Select(n => { var c = new Dictionary<string, object>(n); c["name"] = n["name"] + " (custom)"; return c; })).ToList()));
                }
                else if (kind == "content")
                {
                    raw("items", ExportRaw("ContentLoader", "items.json", "{}"));
                    raw("loot", ExportRaw("ContentLoader", "loot.json", "{}"));
                    raw("variants", ExportRaw("ContentLoader", "variants.json", "{}"));
                    raw("shops", ExportRaw("ContentLoader", "shops.json", "{}"));
                    raw("monsters", ExportRaw("ContentLoader", "monsters.json", "[]"));
                    raw("npcs", Json.Write(GameNpcs().Concat(CustomNpcs()).ToList()));
                    raw("itemNames", ExportRaw("QuestLoader", "items.json", "[]"));
                }
                else
                {
                    raw("npcs", Json.Write(GameNpcs()));
                    raw("custom", Json.Write(CustomNpcs()));
                    var orig = Json.List(Json.Load(ExportPath("QuestLoader", "quests.json")));
                    raw("originals", Json.Write(orig == null ? new List<object>() : orig.Cast<object>().Select(o => (object)new Dictionary<string, object> {
                        { "id", Json.Get(o, "id") }, { "title", Json.Str(o, "title") }, { "giver", Json.Get(o, "giver") }, { "ender", Json.Get(o, "ender") } }).ToList()));
                    raw("packQuests", Json.Write(PackQuests()));
                    var shops = Json.Obj(Json.Get(Json.Load(ExportPath("ContentLoader", "shops.json")), "drops"));
                    raw("shops", Json.Write(shops == null ? new List<string>() : shops.Keys.OrderBy(k => { long n; return long.TryParse(k, out n) ? n : 0; }).ToList()));
                }
                return JsonText(sb.Append('}').ToString());
            }
            if (parts.Length == 3 && parts[2] == "pack")
            {
                var name = Q(q, "name");
                var file = PackFile(kind, name);
                if (file == null) return Error(400, "bad pack name");
                var text = Json.ReadText(file);
                if (text != null) return JsonText(text);
                var empty = new Dictionary<string, object> { { "name", name } };
                if (Kinds[kind][1] != null) empty[Kinds[kind][1]] = new object[0];
                return JsonObj(empty);
            }
            return Error(404, "not found");
        }

        /// <summary>Everything the search box can find: quests, items, loot tables (by monster), shops, NPCs.</summary>
        List<Dictionary<string, object>> SearchIndex()
        {
            var idx = new List<Dictionary<string, object>>();
            Action<string, object, string, string> add = (k, id, n, s) => { if (!string.IsNullOrEmpty(n)) idx.Add(new Dictionary<string, object> { { "k", k }, { "id", id }, { "n", n }, { "s", s } }); };
            var npcName = new Dictionary<long, string>();
            foreach (var n in GameNpcs()) if (!npcName.ContainsKey(Json.Num(n, "cid"))) npcName[Json.Num(n, "cid")] = Json.Str(n, "name");
            var custom = CustomNpcs();
            foreach (var n in custom) npcName[Json.Num(n, "cid")] = (string)n["name"];
            Func<long, string> who = c => { string s; return npcName.TryGetValue(c, out s) ? s : (c > 0 ? "NPC " + c : null); };

            var givers = new Dictionary<long, int>();
            var orig = Json.List(Json.Load(ExportPath("QuestLoader", "quests.json")));
            if (orig != null)
                foreach (var o in orig)
                {
                    long g = Json.Num(o, "giver");
                    givers[g] = (givers.ContainsKey(g) ? givers[g] : 0) + 1;
                    add("quest", Json.Get(o, "id"), Json.Str(o, "title"), "Game quest" + (who(g) != null ? " · from " + who(g) : ""));
                }
            foreach (var p in PackQuests())
                add("myquest", p["id"], (string)p["title"], "Your quest · pack " + p["pack"] + (who(Json.Num(p, "giver")) != null ? " · from " + who(Json.Num(p, "giver")) : ""));

            var items = Json.Obj(Json.Load(ExportPath("ContentLoader", "items.json")));
            if (items != null)
                foreach (var kv in items) add("item", kv.Key, Json.Str(kv.Value, "name"), "Item #" + kv.Key + (Json.Get(kv.Value, "price") != null ? " · " + Json.Str(kv.Value, "price") + " gold" : ""));
            else
            {
                var names = Json.List(Json.Load(ExportPath("QuestLoader", "items.json")));
                if (names != null) foreach (var i in names) add("item", Json.Str(i, "id"), Json.Str(i, "name"), "Item #" + Json.Str(i, "id"));
            }

            var monsters = Json.List(Json.Load(ExportPath("ContentLoader", "monsters.json")));
            if (monsters != null)
            {
                var seen = new HashSet<string>();
                foreach (var m in monsters)
                {
                    var key = Json.Str(m, "name") + "|" + Json.Num(m, "dropId");
                    if (!seen.Add(key)) continue;
                    var zone = (Json.Str(m, "zone") ?? "").Replace("Zone_", "");
                    add("loot", Json.Num(m, "dropId"), Json.Str(m, "name"), "Monster · drops from loot table " + Json.Num(m, "dropId") + (zone.Length > 0 ? " · " + zone : ""));
                }
            }
            var shops = Json.Obj(Json.Get(Json.Load(ExportPath("ContentLoader", "shops.json")), "drops"));
            if (shops != null)
                foreach (var k in shops.Keys) { long id; long.TryParse(k, out id); add("shop", k, who(id) ?? "Shop " + k, "Shop " + k); }
            foreach (var n in custom) add("npc", n["cid"], (string)n["name"], "Your NPC · pack " + n["pack"]);
            foreach (var n in GameNpcs())
            {
                long c = Json.Num(n, "cid");
                if (givers.ContainsKey(c)) add("giver", c, Json.Str(n, "name"), "NPC · gives " + givers[c] + " quest(s) · " + (Json.Str(n, "zone") ?? "").Replace("Zone_", ""));
            }
            return idx;
        }

        // ------------------------------------------------------------ POST
        Response Post(string path, Dictionary<string, string> q, byte[] body)
        {
            var parts = path.Trim('/').Split('/');
            if (parts.Length < 3 || parts[0] != "api") return Error(404, "not found");

            if (parts[1] == "packs" && parts[2] == "toggle")
            {
                string kind = Q(q, "kind"), name = Q(q, "name"); bool on = Q(q, "on") == "1";
                var file = PackFile(kind, name);
                if (file == null || !File.Exists(file)) return Error(404, "no such pack");
                var want = Path.Combine(Folder(kind), name + ".json" + (on ? "" : Off));
                if (!string.Equals(file, want, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(want)) return Error(409, "both " + Path.GetFileName(file) + " and " + Path.GetFileName(want) + " exist; remove one");
                    File.Move(file, want);
                }
                return JsonObj(new Dictionary<string, object> { { "ok", true }, { "enabled", on } });
            }

            if (parts[1] == "packs" && parts[2] == "delete")
            {
                // To the Recycle Bin (with its .bak), so a mistake can be undone there.
                string kind = Q(q, "kind"), name = Q(q, "name");
                var file = PackFile(kind, name);
                if (file == null || !File.Exists(file)) return Error(404, "no such pack");
                foreach (var f in new[] { file, file + ".bak" })
                    if (File.Exists(f))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                         Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return JsonObj(new Dictionary<string, object> { { "ok", true } });
            }

            if (parts[1] == "mods" && parts[2] == "toggle")
            {
                string mod = Q(q, "id"); bool on = Q(q, "on") == "1";
                if (!Mods.Contains(mod)) return Error(400, "unknown mod");
                var cfgPath = ModsDir("mods.cfg");
                var lines = File.Exists(cfgPath) ? File.ReadAllLines(cfgPath).ToList()
                    : new List<string> { "# Which mods load. Set to false to switch a mod off (or use F11 in game). Applies on next start." };
                var key = mod.ToLowerInvariant(); bool found = false;
                for (int i = 0; i < lines.Count; i++)
                    if (Regex.IsMatch(lines[i], @"^\s*" + Regex.Escape(key) + @"\s*=", RegexOptions.IgnoreCase)) { lines[i] = key + " = " + (on ? "true" : "false"); found = true; }
                if (!found) lines.Add(key + " = " + (on ? "true" : "false"));
                File.WriteAllLines(cfgPath, lines);
                return JsonObj(new Dictionary<string, object> { { "ok", true } });
            }

            var k2 = parts[1];
            if (Kinds.ContainsKey(k2) && parts[2] == "pack")
            {
                var file = PackFile(k2, Q(q, "name"));
                if (file == null) return Error(400, "Pack names can use letters, numbers, spaces, - and _");
                object pack;
                try
                {
                    pack = Json.Parse(Encoding.UTF8.GetString(body));
                    if (Json.Obj(pack) == null) throw new Exception("expected a JSON object");
                    var listKey = Kinds[k2][1];
                    if (listKey != null && Json.List(Json.Get(pack, listKey)) == null) throw new Exception("expected {name, " + listKey + ": [...]}");
                }
                catch (Exception e) { return Error(400, "Couldn't read the pack: " + e.Message); }
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);       // one step of undo
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, Json.Pretty(pack), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                var lk = Kinds[k2][1];
                return JsonObj(new Dictionary<string, object> {
                    { "saved", file }, { "enabled", !file.EndsWith(Off) },
                    { "count", lk == null ? (object)null : Json.List(Json.Get(pack, lk)).Count } });
            }
            return Error(404, "not found");
        }
    }
}
