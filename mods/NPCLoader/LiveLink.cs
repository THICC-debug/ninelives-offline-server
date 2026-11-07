using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NPCLoader
{
    /// <summary>
    /// A tiny local server inside the game (127.0.0.1:8099 only) so the Nine Lives Editor can show
    /// NPC changes live: requests are queued and run on the game's main thread in Pump().
    /// </summary>
    class LiveLink
    {
        public const int Port = 8099;

        class Job
        {
            public string Method, Path, Query, Body, Result;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        readonly Func<string, string, string, string, string> handle;   // (method, path, query, body) -> JSON, main thread
        readonly Queue<Job> jobs = new Queue<Job>();
        TcpListener listener;

        public LiveLink(Func<string, string, string, string, string> handle) { this.handle = handle; }

        public bool Start(out string error)
        {
            error = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                try { listener.ExclusiveAddressUse = true; } catch { }
                listener.Start();
            }
            catch (Exception e) { error = e.Message; return false; }
            var t = new Thread(Accept) { IsBackground = true, Name = "NPCLoader live link" };
            t.Start();
            return true;
        }

        /// <summary>Run waiting requests (call from OnUpdate, i.e. the main thread).</summary>
        public void Pump()
        {
            while (true)
            {
                Job j;
                lock (jobs) { if (jobs.Count == 0) return; j = jobs.Dequeue(); }
                try { j.Result = handle(j.Method, j.Path, j.Query, j.Body); }
                catch (Exception e) { j.Result = "{\"error\":" + Quote(e.Message) + "}"; }
                j.Done.Set();
            }
        }

        void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var s = client.GetStream())
                {
                    string method, target, body;
                    if (!Read(s, out method, out target, out body)) return;
                    int q = target.IndexOf('?');
                    var job = new Job { Method = method, Path = q < 0 ? target : target.Substring(0, q), Query = q < 0 ? "" : target.Substring(q + 1), Body = body };
                    lock (jobs) jobs.Enqueue(job);
                    // The game runs it on its next frame; if it's busy loading, say so.
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

        static bool Read(Stream s, out string method, out string target, out string body)
        {
            method = target = body = null;
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
            method = first[0].ToUpperInvariant(); target = first[1];
            int len = 0;
            foreach (var l in lines)
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(15).Trim(), out len);
            if (len < 0 || len > 1024 * 1024) return false;
            var data = new byte[len];
            int got = 0;
            while (got < len) { int r = s.Read(data, got, len - got); if (r <= 0) break; got += r; }
            body = Encoding.UTF8.GetString(data, 0, got);
            return true;
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

        public static string QueryValue(string query, string key)
        {
            foreach (var part in (query ?? "").Split('&'))
            {
                int e = part.IndexOf('=');
                if (e > 0 && Uri.UnescapeDataString(part.Substring(0, e)) == key) return Uri.UnescapeDataString(part.Substring(e + 1).Replace('+', ' '));
            }
            return null;
        }
    }
}
