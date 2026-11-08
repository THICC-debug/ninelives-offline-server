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
    partial class EditorServer
    {
        const string Off = ".disabled";
        static readonly Regex PackName = new Regex(@"^[A-Za-z0-9 _\-]{1,60}$");
        static readonly string[] Mods = { "TravelMod", "QuestLoader", "ContentLoader", "NPCLoader", "EnemyLoader", "WorldLoader", "ScriptLoader", "DevTools" };

        // kind -> (folder under Mods, list key in a pack, or null)
        static readonly Dictionary<string, string[]> Kinds = new Dictionary<string, string[]>
        {
            { "quests", new[] { "Quests", "quests" } },
            { "content", new[] { "Content", null } },
            { "npcs", new[] { "NPCs", "npcs" } },
            { "enemies", new[] { "Enemies", "enemies" } },
            { "world", new[] { "World", "objects" } },
            { "scripts", new[] { "Scripts", "rules" } },
        };

        static readonly Dictionary<string, string> Pages = new Dictionary<string, string>
        {
            { "/", "index.html" }, { "/index.html", "index.html" }, { "/home", "home.html" }, { "/overview", "overview.html" },
            { "/quests", "quests.html" }, { "/content", "content.html" }, { "/npcs", "npcs.html" }, { "/enemies", "enemies.html" }, { "/monsters.js", "monsters.js" }, { "/world", "world.html" }, { "/scripts", "scripts.html" }, { "/map", "map.html" }, { "/dialogs", "dialogs.html" }, { "/steps.js", "steps.js" }, { "/gameview.js", "gameview.js" }, { "/outliner", "outliner.html" }, { "/assets", "assets.html" }, { "/popout", "popout.html" }, { "/style.css", "style.css" },
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
            ThreadPool.QueueUserWorkItem(_ => InstallDropped());   // mods dropped into the Mods folder (Share.cs)
        }

        public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }

        void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (ObjectDisposedException) { return; }   // stopped
                catch (Exception) { Thread.Sleep(50); continue; }   // a one-off socket error mustn't end the server
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
                    // Only the editor's own pages: a web page elsewhere (in your browser) could otherwise send
                    // requests here and change packs. Pages from here have this Host and this Origin.
                    string host, origin;
                    bool hostOk = headers.TryGetValue("Host", out host) && (host == "127.0.0.1:" + Port || host == "localhost:" + Port);
                    bool originOk = !headers.TryGetValue("Origin", out origin) || origin == "http://127.0.0.1:" + Port || origin == "http://localhost:" + Port;
                    string site;   // browsers say where a request comes from: only this editor's pages (or opening it) count
                    if (headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin" && site != "none") originOk = false;
                    if (!hostOk || !originOk)
                    {
                        var no = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        s.Write(no, 0, no.Length); return;
                    }
                    int q = target.IndexOf('?');
                    var path = q < 0 ? target : target.Substring(0, q);
                    var query = ParseQuery(q < 0 ? "" : target.Substring(q + 1));
                    Response r;
                    try { r = path.StartsWith("/api/live/") ? Live(method, target.Substring("/api/live".Length), body)
                                                            : method == "POST" ? (path.StartsWith("/api/play/") ? Post(path, query, body) : PostLocked(path, query, body))
                                                            : path.Contains("/pack") ? GetLocked(path, query) : Get(path, query); }
                    catch (Exception e) { r = Error(500, e.Message); }
                    var head = "HTTP/1.1 " + r.Code + " " + (r.Code == 200 ? "OK" : "Error") + "\r\n" +
                               "Content-Type: " + r.Type + "\r\nContent-Length: " + r.Body.Length + "\r\n" +
                               (r.Disposition != null ? "Content-Disposition: " + r.Disposition + "\r\n" : "") +
                               "Cache-Control: " + (r.Cache ? "max-age=3600" : "no-store") + "\r\nConnection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(head);
                    s.Write(hb, 0, hb.Length);
                    s.Write(r.Body, 0, r.Body.Length);
                }
                catch (IOException) { }
                catch (Exception) { }   // never let one bad request take the editor down (this runs on a pool thread)
            }
        }

        // Saves, toggles and deletes one at a time: two at once could lose one, or leave a pack both on and off.
        readonly object writeGate = new object();
        Response PostLocked(string path, Dictionary<string, string> q, byte[] body) { lock (writeGate) return Post(path, q, body); }
        // Reading a pack waits for a save or toggle in progress (mid-toggle the file is neither on nor off).
        Response GetLocked(string path, Dictionary<string, string> q) { lock (writeGate) return Get(path, q); }

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
                int n;
                if (!int.TryParse(cl, out n) || n < 0 || n > 64 * 1024 * 1024) return false;
                body = new byte[n];
                int got = 0;
                while (got < n) { int r = s.Read(body, got, n - got); if (r <= 0) break; got += r; }
                if (got < n) return false;   // cut short: don't act on half a request
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

        class Response { public int Code = 200; public string Type = "application/json; charset=utf-8"; public byte[] Body; public bool Cache; public string Disposition; }
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
                    text = text.Trim();
                    if (!text.StartsWith("{")) return JsonText(text);
                    var rest = text.Substring(1).TrimStart();
                    return JsonText("{\"connected\":true" + (rest.StartsWith("}") ? "" : ",") + rest);
                }
            }
            catch (WebException e) when (e.Status == WebExceptionStatus.Timeout)
            {
                return JsonObj(new Dictionary<string, object> { { "connected", true }, { "busy", true }, { "error", "The game is busy (loading?). Try again in a moment." } });
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
        string ExportRaw(string mod, string file, string fallback)
        {
            var t = Json.ReadText(ExportPath(mod, file));
            if (string.IsNullOrWhiteSpace(t)) return fallback;
            t = t.Trim();
            if (!(t.StartsWith("{") && t.EndsWith("}")) && !(t.StartsWith("[") && t.EndsWith("]"))) return fallback;   // being written by the game
            try { Json.Parse(t); } catch { return fallback; }
            return t;
        }

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
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { var n = f.Substring(0, f.Length - 5); if (PackName.IsMatch(n)) found[n] = true; }
                else if (f.EndsWith(".json" + Off, StringComparison.OrdinalIgnoreCase))
                {
                    var n = f.Substring(0, f.Length - 5 - Off.Length);
                    if (PackName.IsMatch(n) && !found.ContainsKey(n)) found[n] = false;
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
                        { "x", Json.Get(n, "x") ?? 0 }, { "y", Json.Get(n, "y") ?? 0 }, { "z", Json.Get(n, "z") ?? 0 }, { "rotation", Json.Get(n, "rotation") ?? 0 },
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
                return new Response { Body = bytes, Type = page.EndsWith(".css") ? "text/css; charset=utf-8" : page.EndsWith(".js") ? "application/javascript; charset=utf-8" : "text/html; charset=utf-8" };
            }
            var parts = path.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0] != "api") return Error(404, "not found");

            if (parts[1] == "play" && parts.Length > 2 && parts[2] == "status") return JsonObj(GameRunner.Status(game));
            if (parts[1] == "zones")   // every zone and dungeon (from WorldLoader's scene index), for the 3D view's zone list
            {
                var dir = ModsDir(Path.Combine("WorldLoader", "batches"));
                var zoneNames = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f))
                    .Where(n => n.StartsWith("Zone_") || n.StartsWith("Dungeon_")).OrderBy(n => n.StartsWith("Dungeon_")).ThenBy(n => n).ToList() : new List<string>();
                return JsonObj(zoneNames);
            }
            if (parts[1] == "overview")
            {
                var justNow = InstallDropped();   // mods dropped into the Mods folder since (Share.cs)
                var cfg = ModCfg();
                return JsonObj(new Dictionary<string, object> {
                    { "game", game },
                    { "mods", Mods.Select(m => new Dictionary<string, object> {
                        { "id", m }, { "installed", File.Exists(ModsDir(m + ".dll")) }, { "enabled", !cfg.ContainsKey(m) || cfg[m] } }).ToList() },
                    { "packs", Kinds.Keys.ToDictionary(k => k, k => (object)Packs(k).Select(p => new Dictionary<string, object> {
                        { "name", p.Key }, { "enabled", p.Value }, { "size", PackSize(k, p.Key) },
                        { "dialogs", k == "scripts" ? (object)((Json.List(Json.Get(Json.Load(PackFile(k, p.Key)), "dialogs")) ?? new object[0]).Count) : null } }).ToList()) },
                    { "added", AddedMods(justNow) },
                    { "exported", new Dictionary<string, object> {
                        { "quests", File.Exists(ExportPath("QuestLoader", "quests.json")) },
                        { "content", File.Exists(ExportPath("ContentLoader", "loot.json")) } } },
                });
            }
            if (parts[1] == "index") return JsonObj(SearchIndex());
            if (parts[1] == "share") return ShareGet(parts, q);   // export a mod (Share.cs)
            if (parts[1] == "prefs")   // the editor's own settings (layout, panel sizes…), kept in a file
            {
                var f = PrefsFile;
                return JsonText(File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : "{}");
            }
            if (parts[1] == "icon" && parts.Length == 3)   // an item's icon, exported by ContentLoader: /api/icon/icon_90_3
            {
                var name = parts[2];
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^icon_[0-9]{1,6}_[0-9]{1,6}$")) return Error(400, "bad icon name");
                var png = Path.Combine(ExportPath("ContentLoader", "icons"), name + ".png");
                if (!File.Exists(png)) return Error(404, "no such icon");
                return new Response { Body = File.ReadAllBytes(png), Type = "image/png", Cache = true };
            }
            if (parts[1] == "monster" && parts.Length == 3)   // a monster's picture, taken by ContentLoader: /api/monster/m_1a2b3c4d
            {
                var name = parts[2];
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^m_[0-9a-f]{8}$")) return Error(400, "bad picture name");
                var png = Path.Combine(ExportPath("ContentLoader", "monsters"), name + ".png");
                if (!File.Exists(png)) return Error(404, "no such picture");
                return new Response { Body = File.ReadAllBytes(png), Type = "image/png" };
            }
            if (parts[1] == "map") return Map(parts.Length > 2 ? parts[2] : "", q);

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
                    raw("monsters", ExportRaw("ContentLoader", "monsters.json", "[]"));
                    raw("maps", Json.Write(MapBounds()));
                    raw("items", ExportRaw("QuestLoader", "items.json", "[]"));
                    raw("npcs", Json.Write(GameNpcs().Concat(CustomNpcs().Select(n => { var c = new Dictionary<string, object>(n); c["name"] = n["name"] + " (custom)"; return c; })).ToList()));
                }
                else if (kind == "enemies")
                {
                    raw("monsters", ExportRaw("ContentLoader", "monsters.json", "[]"));
                    raw("maps", Json.Write(MapBounds()));
                    raw("loot", ExportRaw("ContentLoader", "loot.json", "{}"));
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
                else if (kind == "scripts")
                {
                    // Everything a rule can point at.
                    raw("npcs", Json.Write(GameNpcs().Concat(CustomNpcs()).ToList()));
                    raw("items", ExportRaw("QuestLoader", "items.json", "[]"));
                    var orig = Json.List(Json.Load(ExportPath("QuestLoader", "quests.json")));
                    raw("quests", Json.Write((orig == null ? new List<object>() : orig.Cast<object>().Select(o => (object)new Dictionary<string, object> {
                        { "id", Json.Get(o, "id") }, { "title", Json.Str(o, "title") }, { "clearFlag", Json.Get(o, "clearFlag") } }).ToList())
                        .Concat(PackQuests().Select(q => (object)new Dictionary<string, object> { { "id", q["id"] }, { "title", q["title"] }, { "pack", q["pack"] } })).ToList()));
                    raw("monsters", ExportRaw("ContentLoader", "monsters.json", "[]"));
                    var things = new List<object>();
                    foreach (var p in Packs("world")) foreach (var o in PackItems("world", p.Key))
                        things.Add(new Dictionary<string, object> { { "id", Json.Str(o, "id") }, { "name", Json.Str(o, "name") }, { "zone", Json.Str(o, "zone") } });
                    raw("things", Json.Write(things));
                    // Every dialogue (for "Start a dialogue").
                    var dlgs = new List<object>();
                    foreach (var p in Packs("scripts"))
                    {
                        var l = Json.List(Json.Get(Json.Load(PackFile("scripts", p.Key)), "dialogs"));
                        if (l != null) foreach (var d in l) dlgs.Add(new Dictionary<string, object> { { "id", Json.Str(d, "id") }, { "name", Json.Str(d, "name") }, { "pack", p.Key }, { "npc", Json.Get(d, "npc") } });
                    }
                    raw("dialogs", Json.Write(dlgs));
                }
                else if (kind == "world")
                {
                    // Ids already used in every world pack (ids must be unique).
                    var ids = new List<object>();
                    foreach (var p in Packs("world")) foreach (var o in PackItems("world", p.Key)) ids.Add(Json.Str(o, "id"));
                    raw("ids", Json.Write(ids));
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
                    // For vendors: every item's usual price, and what each of the game's vendors always stocks (to start from).
                    raw("items", ExportRaw("ContentLoader", "items.json", "{}"));
                    raw("itemNames", ExportRaw("QuestLoader", "items.json", "[]"));
                    raw("icons", ExportRaw("ContentLoader", "icons.json", "{}"));
                    var stock = new Dictionary<string, object>();
                    if (shops != null)
                        foreach (var kv in shops)
                        {
                            var counts = new Dictionary<string, int>(); var order = new List<string>();
                            foreach (var s in Json.List(Json.Get(kv.Value, "always")) ?? new List<object>())
                            {
                                long n;
                                var id = Convert.ToString(Json.Get(s, "id"), System.Globalization.CultureInfo.InvariantCulture);
                                if (!long.TryParse(id, out n)) continue;
                                if (!counts.ContainsKey(id)) { counts[id] = 0; order.Add(id); }
                                counts[id]++;
                            }
                            if (order.Count > 0) stock[kv.Key] = order.Select(id => (object)new object[] { long.Parse(id), counts[id] }).ToList();
                        }
                    raw("vendorStock", Json.Write(stock));
                }
                return JsonText(sb.Append('}').ToString());
            }
            if (parts.Length == 3 && parts[2] == "pack")
            {
                var name = Q(q, "name");
                var file = PackFile(kind, name);
                if (file == null) return Error(400, "bad pack name");
                if (File.Exists(file))
                {
                    for (int i = 0; ; i++)
                    {
                        try { return JsonText(File.ReadAllText(file, Encoding.UTF8).TrimStart('\uFEFF')); }
                        catch (IOException e) { if (i >= 4) return Error(503, "The pack file is in use (" + e.Message + "). Try again in a moment."); System.Threading.Thread.Sleep(150); }
                    }
                }
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
            {
                int before = idx.Count;
                add("myquest", p["id"], (string)p["title"], "Your quest · pack " + p["pack"] + (who(Json.Num(p, "giver")) != null ? " · from " + who(Json.Num(p, "giver")) : ""));
                if (idx.Count > before) idx[idx.Count - 1]["pack"] = p["pack"];
            }

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
            foreach (var p in Packs("scripts"))
            {
                foreach (var o in PackItems("scripts", p.Key))
                {
                    int before = idx.Count;
                    add("rule", Json.Str(o, "id"), Json.Str(o, "name") ?? Json.Str(o, "id"), "Script rule · pack " + p.Key);
                    if (idx.Count > before) idx[idx.Count - 1]["pack"] = p.Key;
                }
                var dl = Json.List(Json.Get(Json.Load(PackFile("scripts", p.Key)), "dialogs"));
                if (dl != null)
                    foreach (var d in dl)
                    {
                        int before = idx.Count;
                        var dn = Json.Str(d, "name"); if (string.IsNullOrEmpty(dn)) dn = Json.Str(d, "id");
                        add("dlg", Json.Str(d, "id"), dn, "Dialogue · pack " + p.Key + (Json.Num(d, "npc") > 0 ? " · " + (who(Json.Num(d, "npc")) ?? "") : ""));
                        if (idx.Count > before) idx[idx.Count - 1]["pack"] = p.Key;
                    }
            }
            foreach (var p in Packs("world"))
                foreach (var o in PackItems("world", p.Key))
                {
                    add("obj", Json.Str(o, "id"), Json.Str(o, "name") ?? Json.Str(o, "id"), (true.Equals(Json.Get(o, "hide")) ? "Hidden thing" : "Placed thing") + " · pack " + p.Key + " · " + (Json.Str(o, "zone") ?? "").Replace("Zone_", ""));
                    if (idx.Count > 0 && (string)idx[idx.Count - 1]["k"] == "obj" && !idx[idx.Count - 1].ContainsKey("pack")) idx[idx.Count - 1]["pack"] = p.Key;
                }
            foreach (var n in GameNpcs())
            {
                long c = Json.Num(n, "cid");
                if (givers.ContainsKey(c)) add("giver", c, Json.Str(n, "name"), "NPC · gives " + givers[c] + " quest(s) · " + (Json.Str(n, "zone") ?? "").Replace("Zone_", ""));
            }
            return idx;
        }

        // ------------------------------------------------------------ map
        /// <summary>Each zone's map picture area (Mods\maps\&lt;zone&gt;.json), for drawing small maps.</summary>
        Dictionary<string, object> MapBounds()
        {
            var d = new Dictionary<string, object>();
            var dir = ModsDir("maps");
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var z = Path.GetFileNameWithoutExtension(f);
                    if (File.Exists(Path.Combine(dir, z + ".png"))) d[z] = Json.Load(f);
                }
            return d;
        }

        Response Map(string what, Dictionary<string, string> q)
        {
            var dir = ModsDir("maps");
            var zone = Q(q, "zone");
            if (zone.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || zone.Contains("..")) return Error(400, "bad zone");
            if (what == "zones")
            {
                var zones = new SortedDictionary<string, Dictionary<string, object>>();
                Action<string> seen = z => { if (!string.IsNullOrEmpty(z) && !zones.ContainsKey(z)) zones[z] = new Dictionary<string, object> { { "zone", z }, { "map", false } }; };
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        var z = Path.GetFileNameWithoutExtension(f); seen(z);
                        zones[z]["map"] = File.Exists(Path.Combine(dir, z + ".png"));
                        zones[z]["taken"] = Json.Str(Json.Load(f), "taken");
                    }
                foreach (var n in GameNpcs()) seen(Json.Str(n, "zone"));
                foreach (var n in CustomNpcs()) seen((string)n["zone"]);
                return JsonObj(zones.Values.ToList());
            }
            if (what == "image")
            {
                var png = Path.Combine(dir, zone + ".png");
                if (!File.Exists(png)) return Error(404, "no map yet");
                return new Response { Body = File.ReadAllBytes(png), Type = "image/png" };
            }
            if (what == "data")
            {
                // Everything to draw on the map of one zone.
                var givers = new Dictionary<long, int>();
                var orig = Json.List(Json.Load(ExportPath("QuestLoader", "quests.json")));
                if (orig != null) foreach (var o in orig) { long g = Json.Num(o, "giver"); givers[g] = (givers.ContainsKey(g) ? givers[g] : 0) + 1; }
                foreach (var pq in PackQuests()) { long g = Json.Num(pq, "giver"); givers[g] = (givers.ContainsKey(g) ? givers[g] : 0) + 1; }
                var things = new List<object>();
                foreach (var p in Packs("world"))
                    foreach (var o in PackItems("world", p.Key))
                        if (Json.Str(o, "zone") == zone) things.Add(new Dictionary<string, object> {
                            { "id", Json.Str(o, "id") }, { "name", Json.Str(o, "name") }, { "pack", p.Key }, { "on", p.Value && !false.Equals(Json.Get(o, "enabled")) },
                            { "hide", true.Equals(Json.Get(o, "hide")) }, { "x", Json.Get(o, "x") ?? 0 }, { "z", Json.Get(o, "z") ?? 0 }, { "y", Json.Get(o, "y") ?? 0 }, { "ry", Json.Get(o, "ry") ?? 0 } });
                var areas = new List<object>();
                foreach (var p in Packs("scripts"))
                    foreach (var r in PackItems("scripts", p.Key))
                    {
                        var w = Json.Get(r, "when"); var type = Json.Str(w, "type");
                        if ((type == "enterArea" || type == "leaveArea") && Json.Str(w, "zone") == zone)
                            areas.Add(new Dictionary<string, object> { { "kind", "area" }, { "rule", Json.Str(r, "id") }, { "name", Json.Str(r, "name") ?? Json.Str(r, "id") }, { "pack", p.Key },
                                { "x", Json.Get(w, "x") ?? 0 }, { "z", Json.Get(w, "z") ?? 0 }, { "y", Json.Get(w, "y") ?? 0 }, { "radius", Json.Get(w, "radius") ?? 5 }, { "leave", type == "leaveArea" } });
                        var steps = Json.List(Json.Get(r, "then"));
                        if (steps != null)
                            for (int si = 0; si < steps.Count; si++)
                            {
                                var a = steps[si];
                                // a teleport target belongs on the map of the zone it sends you to (its own zone, or the rule's)
                                var to = Json.Str(a, "zone"); if (string.IsNullOrEmpty(to)) to = Json.Str(w, "zone");
                                if (Json.Str(a, "type") == "teleport" && to == zone)
                                    areas.Add(new Dictionary<string, object> { { "kind", "teleport" }, { "rule", Json.Str(r, "id") }, { "step", si }, { "name", Json.Str(r, "name") ?? Json.Str(r, "id") }, { "pack", p.Key },
                                        { "x", Json.Get(a, "x") ?? 0 }, { "z", Json.Get(a, "z") ?? 0 }, { "y", Json.Get(a, "y") ?? 0 } });
                            }
                    }
                return JsonObj(new Dictionary<string, object> {
                    { "zone", zone }, { "bounds", Json.Load(Path.Combine(dir, zone + ".json")) },
                    { "npcs", GameNpcs().Where(n => Json.Str(n, "zone") == zone).Select(n => (object)new Dictionary<string, object> {
                        { "cid", Json.Get(n, "cid") }, { "name", Json.Str(n, "name") }, { "x", Json.Get(n, "x") }, { "z", Json.Get(n, "z") }, { "y", Json.Get(n, "y") },
                        { "quests", givers.ContainsKey(Json.Num(n, "cid")) ? givers[Json.Num(n, "cid")] : 0 } }).ToList() },
                    { "custom", CustomNpcs().Where(n => (string)n["zone"] == zone).ToList() },
                    { "things", things }, { "areas", areas },
                });
            }
            return Error(404, "not found");
        }

        // ------------------------------------------------------------ POST
        static string PrefsFile { get { return Path.Combine(Resources.AppData, "prefs.json"); } }

        Response Post(string path, Dictionary<string, string> q, byte[] body)
        {
            var parts = path.Trim('/').Split('/');
            if (parts.Length < 3 || parts[0] != "api") return Error(404, "not found");

            if (parts[1] == "prefs" && parts[2] == "save")
            {
                // The pages' settings live in the browser's storage, which goes with the address, and this
                // server's port changes every run: so they're also kept here and put back at the start.
                var text = Encoding.UTF8.GetString(body ?? new byte[0]);
                if (text.Length > 512 * 1024 || !(Json.Parse(text) is Dictionary<string, object>)) return Error(400, "not a settings object");
                Directory.CreateDirectory(Path.GetDirectoryName(PrefsFile));
                File.WriteAllText(PrefsFile, text, Encoding.UTF8);
                return JsonObj(new Dictionary<string, object> { { "ok", true } });
            }
            if (parts[1] == "play")   // the 3D view: the game in the background
            {
                if (parts[2] == "start") return JsonObj(GameRunner.Start(game));
                if (parts[2] == "stop") return JsonObj(GameRunner.Stop(game));
                if (parts[2] == "show") return JsonObj(GameRunner.Show(game, Q(q, "on") == "1"));
            }
            if (parts[1] == "share") return SharePost(parts, q, body);   // look inside / import a mod (Share.cs)
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
                bool recycle = false;
                try { recycle = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(file))).DriveType == DriveType.Fixed; } catch { }
                string trash = null;
                foreach (var f in new[] { file, file + ".bak" })
                {
                    if (!File.Exists(f)) continue;
                    if (recycle)
                    {
                        try
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                            if (!File.Exists(f)) continue;
                        }
                        catch { }
                    }
                    // No Recycle Bin there (network or USB drive): keep it in Mods\.trash instead of deleting it for good.
                    trash = ModsDir(".trash"); Directory.CreateDirectory(trash);
                    File.Move(f, Path.Combine(trash, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + kind + " " + Path.GetFileName(f)));
                }
                return JsonObj(new Dictionary<string, object> { { "ok", true }, { "trash", trash } });
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
                var tmp = cfgPath + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(cfgPath)) File.Replace(tmp, cfgPath, null); else File.Move(tmp, cfgPath);
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
                var tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    File.WriteAllText(tmp, Json.Pretty(pack), new UTF8Encoding(false));
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                var lk = Kinds[k2][1];
                return JsonObj(new Dictionary<string, object> {
                    { "saved", file }, { "enabled", !file.EndsWith(Off) },
                    { "count", lk == null ? (object)null : Json.List(Json.Get(pack, lk)).Count } });
            }
            return Error(404, "not found");
        }
    }
}
