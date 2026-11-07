using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NineLivesModLoader
{
    /// <summary>A request from the Nine Lives Editor.</summary>
    public sealed class LiveRequest
    {
        public string Method, Path, Query, Body;

        /// <summary>A value from the query string (?id=5), or null.</summary>
        public string Get(string key)
        {
            foreach (var part in (Query ?? "").Split('&'))
            {
                int e = part.IndexOf('=');
                if (e > 0 && Uri.UnescapeDataString(part.Substring(0, e)) == key) return Uri.UnescapeDataString(part.Substring(e + 1).Replace('+', ' '));
            }
            return null;
        }
    }

    /// <summary>
    /// The live link: a tiny local server inside the game (127.0.0.1:8099, nothing outside your PC can
    /// reach it) that lets the Nine Lives Editor show changes in the world as you make them.
    /// Mods add requests with <see cref="Route"/>; handlers run on the game's main thread and return JSON.
    /// Built in: GET /status (where you are), GET /here (the spot in front of you), POST /goto {x,y,z,rotation}.
    /// </summary>
    public static class LiveLink
    {
        public const int Port = 8099;

        class Job
        {
            public LiveRequest Req; public string Result;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        static readonly Dictionary<string, Func<LiveRequest, string>> routes = new Dictionary<string, Func<LiveRequest, string>>();
        static readonly Queue<Job> jobs = new Queue<Job>();
        static TcpListener listener;
        public static bool Running { get; private set; }

        /// <summary>
        /// Answer requests for a path ("/npcs"), or for everything under a prefix ending in "/" ("/world/").
        /// The handler runs on the main thread and returns JSON.
        /// </summary>
        public static void Route(string path, Func<LiveRequest, string> handler) { lock (routes) routes[path] = handler; }

        internal static void Start()
        {
            if (Running) return;
            Route("/status", Status);
            Route("/here", Here);
            Route("/goto", Goto);
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                try { listener.ExclusiveAddressUse = true; } catch { }
                listener.Start();
            }
            catch (Exception e) { ModLoader.Log("Live link for the editor is off (port " + Port + ": " + e.Message + ")"); return; }
            Running = true;
            new Thread(Accept) { IsBackground = true, Name = "Nine Lives live link" }.Start();
            ModLoader.Log("Live link for the Nine Lives Editor on 127.0.0.1:" + Port);
        }

        /// <summary>Run waiting requests. Called every frame from the main thread.</summary>
        internal static void Pump()
        {
            while (true)
            {
                Job j;
                lock (jobs) { if (jobs.Count == 0) return; j = jobs.Dequeue(); }
                try { j.Result = Dispatch(j.Req); }
                catch (Exception e) { j.Result = "{\"error\":" + Quote(e.Message) + "}"; ModLoader.Log("live link " + j.Req.Path + ": " + e); }
                j.Done.Set();
            }
        }

        static string Dispatch(LiveRequest r)
        {
            Func<LiveRequest, string> h = null;
            lock (routes)
            {
                if (!routes.TryGetValue(r.Path, out h))
                    h = routes.Where(kv => kv.Key.EndsWith("/") && r.Path.StartsWith(kv.Key, StringComparison.Ordinal))
                              .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
            }
            if (h == null) return "{\"error\":" + Quote("Nothing in the game answers " + r.Path + " (is the mod for it installed and on?)") + "}";
            return h(r) ?? "{}";
        }

        // ------------------------------------------------------------ built-in requests
        static string F(float v) { return Math.Round(v, 2).ToString(CultureInfo.InvariantCulture); }

        static string Status(LiveRequest r)
        {
            var mods = string.Join(",", ModLoader.Mods.Where(m => m.Loaded).Select(m => Quote(m.Id)).ToArray());
            if (!Game.InWorld) return "{\"inWorld\":false,\"mods\":[" + mods + "]}";
            var t = Game.Player.transform;
            return "{\"inWorld\":true,\"zone\":" + Quote(Game.Zone) + ",\"x\":" + F(t.position.x) + ",\"y\":" + F(t.position.y) + ",\"z\":" + F(t.position.z) +
                   ",\"facing\":" + F(t.eulerAngles.y) + ",\"mods\":[" + mods + "]}";
        }

        /// <summary>A spot 2.5 m in front of the player, on the ground, and the rotation that faces the player.</summary>
        static string Here(LiveRequest r)
        {
            if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";
            var t = Game.Player.transform;
            var pos = Ground(t.position + t.forward * 2.5f);
            return "{\"zone\":" + Quote(Game.Zone) + ",\"x\":" + F(pos.x) + ",\"y\":" + F(pos.y) + ",\"z\":" + F(pos.z) +
                   ",\"rotation\":" + F((t.eulerAngles.y + 180f) % 360f) + "}";
        }

        /// <summary>Stand 3 m in front of a spot (where something there facing "rotation" would face you).</summary>
        static string Goto(LiveRequest r)
        {
            if (!Game.InWorld) return "{\"error\":\"Not in the world yet.\"}";
            float x = Num(r.Body, "x"), y = Num(r.Body, "y"), z = Num(r.Body, "z"), rot = Num(r.Body, "rotation");
            var front = Quaternion.Euler(0, rot, 0) * Vector3.forward;
            Game.Teleport(Ground(new Vector3(x, y, z) + front * 3f));
            return "{\"ok\":true}";
        }

        /// <summary>The ground (or floor, platform) under a spot.</summary>
        public static Vector3 Ground(Vector3 spot)
        {
            RaycastHit hit;
            if (Physics.Raycast(spot + Vector3.up * 3f, Vector3.down, out hit, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.05f;
            return spot;
        }

        /// <summary>A number field from a small flat JSON object ({"x":1.5,...}); 0 if missing.</summary>
        public static float Num(string json, string key)
        {
            if (json == null) return 0;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return 0;
            i = json.IndexOf(':', i) + 1;
            int j = i;
            while (j < json.Length && "-+.0123456789eE ".IndexOf(json[j]) >= 0) j++;
            float v; float.TryParse(json.Substring(i, j - i).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ------------------------------------------------------------ HTTP (just enough of it)
        static void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        static void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var s = client.GetStream())
                {
                    LiveRequest req;
                    if (!Read(s, out req)) return;
                    var job = new Job { Req = req };
                    lock (jobs) jobs.Enqueue(job);
                    // Runs on the game's next frame; if the game is busy (loading a zone), say so.
                    var json = job.Done.WaitOne(8000) ? job.Result : "{\"error\":\"The game is busy (loading?). Try again in a moment.\"}";
                    var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + bytes.Length +
                                                       "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length);
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }

        static bool Read(Stream s, out LiveRequest req)
        {
            req = null;
            var buf = new MemoryStream();
            uint last4 = 0; int b;
            while ((b = s.ReadByte()) >= 0)
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
            int len = 0;
            foreach (var l in lines)
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(15).Trim(), out len);
            if (len < 0 || len > 4 * 1024 * 1024) return false;
            var data = new byte[len];
            int got = 0;
            while (got < len) { int r = s.Read(data, got, len - got); if (r <= 0) break; got += r; }
            var target = first[1];
            int q = target.IndexOf('?');
            req = new LiveRequest
            {
                Method = first[0].ToUpperInvariant(),
                Path = q < 0 ? target : target.Substring(0, q),
                Query = q < 0 ? "" : target.Substring(q + 1),
                Body = Encoding.UTF8.GetString(data, 0, got),
            };
            return true;
        }
    }
}
