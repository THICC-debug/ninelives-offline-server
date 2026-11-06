using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using NineLivesModLoader;

namespace FreezeFinder
{
    // Developer-only diagnostic (not released). Hooks every method of the game's own code and keeps
    // a call stack for the main thread. If the game stops updating for a few seconds, a background
    // thread writes that stack and the most-called methods to Mods\FreezeFinder.log, which shows
    // where an endless loop is. Makes startup slow (patching takes a while) and the game a bit slower.
    [ModInfo("freezefinder", "FreezeFinder", "1.0.0", Author = "Nine Lives offline project",
             Description = "Diagnostic: logs where the game is stuck if it freezes.")]
    public class FreezeFinder : Mod
    {
        internal static int MainThread;
        internal static readonly MethodBase[] Stack = new MethodBase[1024];
        internal static volatile int Depth;
        internal static readonly Dictionary<MethodBase, int> Calls = new Dictionary<MethodBase, int>();
        internal static readonly object CallsLock = new object();
        static long lastTick;
        static string logPath;

        public override void Load()
        {
            MainThread = Thread.CurrentThread.ManagedThreadId;
            logPath = Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(FreezeFinder).Assembly.Location), "."), "FreezeFinder.log");
            File.WriteAllText(logPath, "FreezeFinder started " + DateTime.Now + "\r\n");
            var h = new Harmony("ninelives.freezefinder");
            var pre = new HarmonyMethod(typeof(Hooks).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
            var post = new HarmonyMethod(typeof(Hooks).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic));
            var game = typeof(QuestController).Assembly;
            var t0 = DateTime.Now;
            int ok = 0, failed = 0;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Type[] types;
            try { types = game.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition || t.IsInterface) continue;
                foreach (var m in t.GetMethods(all))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters) continue;
                    if ((m.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0) continue;
                    try { if (m.GetMethodBody() == null) continue; } catch { continue; }
                    try { h.Patch(m, pre, post); ok++; }
                    catch { failed++; }
                }
            }
            Write("hooked " + ok + " methods (" + failed + " couldn't be hooked) in " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + " s");
            Log("hooked " + ok + " game methods; a freeze will be written to Mods\\FreezeFinder.log");
            lastTick = DateTime.UtcNow.Ticks;
            new Thread(Watch) { IsBackground = true, Name = "FreezeFinder" }.Start();
        }

        public override void OnUpdate()
        {
            // Load() can run before Unity's main loop is up; Update always runs on the main thread.
            MainThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Exchange(ref lastTick, DateTime.UtcNow.Ticks);
            lock (CallsLock) Calls.Clear();   // counts are per frame; during a freeze they pile up
        }

        static void Write(string s)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss") + " " + s + "\r\n"); } catch { }
        }

        static string Name(MethodBase m) { return m == null ? "?" : (m.DeclaringType != null ? m.DeclaringType.FullName : "") + "." + m.Name; }

        static void Watch()
        {
            int dumps = 0;
            while (true)
            {
                Thread.Sleep(500);
                double stuck = (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastTick)) / 1e7;
                if (stuck < 4) { if (dumps > 0) Write("game is updating again"); dumps = 0; continue; }
                if (dumps >= 3 || stuck < 4 + dumps * 6) continue;   // dumps at ~4 s, 10 s, 16 s
                dumps++;
                var sb = new StringBuilder();
                sb.AppendLine("=== The game has not updated for " + stuck.ToString("0.0") + " s. Main thread stack (innermost last):");
                int d = Math.Min(Depth, Stack.Length);
                for (int i = 0; i < d; i++) sb.AppendLine("   " + i + "  " + Name(Stack[i]));
                sb.AppendLine("Most-called methods since the last frame:");
                KeyValuePair<MethodBase, int>[] top;
                lock (CallsLock) top = Calls.OrderByDescending(kv => kv.Value).Take(30).ToArray();
                foreach (var kv in top) sb.AppendLine("   " + kv.Value.ToString().PadLeft(10) + "  " + Name(kv.Key));
                Write(sb.ToString());
            }
        }
    }

    static class Hooks
    {
        static void Prefix(MethodBase __originalMethod, out int __state)
        {
            __state = -1;
            if (Thread.CurrentThread.ManagedThreadId != FreezeFinder.MainThread) return;
            int d = FreezeFinder.Depth;
            __state = d;
            if (d < FreezeFinder.Stack.Length) FreezeFinder.Stack[d] = __originalMethod;
            FreezeFinder.Depth = d + 1;
            lock (FreezeFinder.CallsLock)
            {
                int n;
                FreezeFinder.Calls.TryGetValue(__originalMethod, out n);
                FreezeFinder.Calls[__originalMethod] = n + 1;
            }
        }

        static void Postfix(int __state)
        {
            if (__state >= 0) FreezeFinder.Depth = __state;
        }
    }
}
