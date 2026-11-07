using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MoonSharp.Interpreter;
using NineLivesModLoader;
using UnityEngine;

namespace ScriptLoader
{
    /// <summary>
    /// One pack's Lua (MoonSharp, sandboxed: no files, no programs). Scripts run once when the world
    /// loads and hook events with on(); timers with after()/every(). The API is the "game" table.
    /// </summary>
    class LuaHost
    {
        readonly string pack;
        readonly Script script;
        readonly Dictionary<string, List<DynValue>> handlers = new Dictionary<string, List<DynValue>>();
        readonly List<KeyValuePair<KeyCode, DynValue>> keys = new List<KeyValuePair<KeyCode, DynValue>>();
        class Timer { public int Id; public float At, Every; public DynValue Fn; }
        readonly List<Timer> timers = new List<Timer>();
        int nextTimer = 1;
        List<string> capture;   // the console's output, while it runs code

        LuaHost(string pack)
        {
            this.pack = pack;
            script = new Script(CoreModules.Preset_SoftSandbox);
            script.Options.DebugPrint = Out;
            Register();
        }

        /// <summary>Null (and a problem noted) if Lua can't start, e.g. MoonSharp.Interpreter.dll is missing from Mods\lib.</summary>
        public static LuaHost Create(string pack, List<string> problems)
        {
            try { return new LuaHost(pack); }
            catch (Exception e) { problems.Add("Lua isn't available (is Mods\\lib\\MoonSharp.Interpreter.dll there?): " + e.Message); return null; }
        }

        void Out(string s)
        {
            if (capture != null) capture.Add(s);
            ModLoader.Log("Lua " + pack + ": " + s);
        }

        void Fail(string where, Exception e)
        {
            var msg = e is InterpreterException ? ((InterpreterException)e).DecoratedMessage ?? e.Message : e.Message;
            Out("error in " + where + ": " + msg);
            if (ScriptLoader.Instance != null && capture == null) ScriptLoader.Instance.problems.Add(pack + ": Lua " + where + ": " + msg);
        }

        void Call(DynValue fn, string where, params object[] args)
        {
            try { script.Call(fn, args.Select(a => a is DynValue ? (DynValue)a : DynValue.FromObject(script, a)).ToArray()); }
            catch (Exception e) { Fail(where, e); }
        }

        // ------------------------------------------------------------ running
        public void RunFile(string id, string code)
        {
            try { script.DoString(code, null, pack + "/" + id); }
            catch (Exception e) { Fail(id, e); }
        }

        public void RunSnippet(string code, Dictionary<string, object> e)
        {
            script.Globals["event"] = ToTable(e);
            try { script.DoString(code, null, pack + " (rule)"); }
            catch (Exception ex) { Fail("rule", ex); }
        }

        /// <summary>The editor's console: run code, return what it printed (and its value).</summary>
        public Dictionary<string, object> Console(string code)
        {
            capture = new List<string>();
            bool ok = true;
            try
            {
                DynValue v;
                try { v = script.DoString("return " + code, null, "console"); }   // an expression: show its value
                catch (SyntaxErrorException) { v = script.DoString(code, null, "console"); }
                if (v != null && !v.IsNil()) capture.Add("= " + v.ToPrintString());
            }
            catch (Exception e) { ok = false; Fail("console", e); }
            var o = new Dictionary<string, object> { { "ok", ok }, { "output", capture } };
            capture = null;
            return o;
        }

        public void Fire(string type, Dictionary<string, object> e)
        {
            List<DynValue> list;
            if (!handlers.TryGetValue(type, out list)) return;
            var t = ToTable(e);
            foreach (var fn in list.ToList()) Call(fn, "on(\"" + type + "\")", t);
        }

        public void Tick(float now)
        {
            foreach (var t in timers.Where(t => now >= t.At).ToList())
            {
                if (t.Every > 0) t.At = now + t.Every; else timers.Remove(t);
                Call(t.Fn, "timer " + t.Id);
            }
        }

        public void KeyCheck()
        {
            foreach (var k in keys.ToList()) if (Input.GetKeyDown(k.Key)) Call(k.Value, "onKey(\"" + k.Key + "\")");
        }

        public void Stop() { handlers.Clear(); keys.Clear(); timers.Clear(); }

        Table ToTable(Dictionary<string, object> e)
        {
            var t = new Table(script);
            if (e != null) foreach (var kv in e) t[kv.Key] = DynValue.FromObject(script, kv.Value);
            return t;
        }

        // ------------------------------------------------------------ the API
        static double Num(CallbackArguments a, int i, double def) { var v = a.Count > i ? a[i].CastToNumber() : null; return v ?? def; }
        static string Str(CallbackArguments a, int i, string def) { return a.Count > i && !a[i].IsNil() ? a[i].ToPrintString() : def; }
        static bool Bool(CallbackArguments a, int i, bool def) { return a.Count > i && !a[i].IsNil() ? a[i].CastToBool() : def; }

        void Fn(Table t, string name, Func<CallbackArguments, DynValue> f)
        {
            t[name] = DynValue.NewCallback((ctx, args) => f(args) ?? DynValue.Nil, name);
        }

        void Register()
        {
            var g = script.Globals;
            Fn(g.OwnerScript.Globals, "log", a => { Out(string.Join(" ", Enumerable.Range(0, a.Count).Select(i => a[i].ToPrintString()).ToArray())); return null; });
            Fn(g.OwnerScript.Globals, "on", a =>
            {
                var type = Str(a, 0, ""); var fn = a.Count > 1 ? a[1] : DynValue.Nil;
                if (fn.Type != DataType.Function) throw new ScriptRuntimeException("on(event, function): the second thing must be a function");
                List<DynValue> list; if (!handlers.TryGetValue(type, out list)) handlers[type] = list = new List<DynValue>();
                list.Add(fn); return null;
            });
            Fn(g.OwnerScript.Globals, "onKey", a =>
            {
                KeyCode k;
                try { k = (KeyCode)Enum.Parse(typeof(KeyCode), Str(a, 0, ""), true); } catch { throw new ScriptRuntimeException("onKey: unknown key \"" + Str(a, 0, "") + "\" (try \"F5\", \"G\", \"Alpha1\")"); }
                keys.Add(new KeyValuePair<KeyCode, DynValue>(k, a[1])); return null;
            });
            Func<CallbackArguments, bool, DynValue> timer = (a, repeat) =>
            {
                var t = new Timer { Id = nextTimer++, Every = repeat ? (float)Math.Max(0.1, Num(a, 0, 1)) : 0, At = Time.realtimeSinceStartup + (float)Math.Max(0, Num(a, 0, 1)), Fn = a[1] };
                timers.Add(t); return DynValue.NewNumber(t.Id);
            };
            Fn(g.OwnerScript.Globals, "after", a => timer(a, false));
            Fn(g.OwnerScript.Globals, "every", a => timer(a, true));
            Fn(g.OwnerScript.Globals, "stop", a => { int id = (int)Num(a, 0, 0); timers.RemoveAll(t => t.Id == id); return null; });

            var game = new Table(script);
            g["game"] = game;
            Fn(game, "message", a => { Messages.Show(Str(a, 0, ""), (float)Num(a, 1, 4)); return null; });
            Fn(game, "give", a => DynValue.NewBoolean(Game.GiveItem((int)Num(a, 0, 0), (int)Num(a, 1, 1))));
            Fn(game, "take", a => { ScriptLoader.TakeItem((int)Num(a, 0, 0), (int)Num(a, 1, 1)); return null; });
            Fn(game, "count", a => DynValue.NewNumber(ScriptLoader.ItemCount((int)Num(a, 0, 0))));
            Fn(game, "gold", a => { Game.GiveGold((long)Num(a, 0, 0)); return null; });
            Fn(game, "exp", a => { Game.GiveExp((long)Num(a, 0, 0)); return null; });
            Fn(game, "flag", a => DynValue.NewBoolean(Game.GetFlag((int)Num(a, 0, 0))));
            Fn(game, "setFlag", a => { Game.SetFlag((int)Num(a, 0, 0), Bool(a, 1, true)); return null; });
            Fn(game, "teleport", a => { Game.Teleport(new Vector3((float)Num(a, 0, 0), (float)Num(a, 1, 0), (float)Num(a, 2, 0))); return null; });
            Fn(game, "npc", a => { ScriptLoader.SetShown("ModNPC_" + (int)Num(a, 0, 0) + "_", true, Bool(a, 1, true)); return null; });
            Fn(game, "thing", a => { ScriptLoader.SetShown("WorldObj_" + Str(a, 0, ""), false, Bool(a, 1, true)); return null; });
            Fn(game, "startQuest", a => { var q = QuestController.GetQuestCtrl(); return DynValue.NewBoolean(q != null && q.questReceive((int)Num(a, 0, 0))); });
            Fn(game, "dialog", a => { var why = ScriptLoader.Instance.OpenDialog(Str(a, 0, ""), pack); if (why != null) throw new ScriptRuntimeException(why); return null; });
            Fn(game, "zone", a => DynValue.NewString(Game.Zone));
            Fn(game, "time", a => DynValue.NewNumber(Time.realtimeSinceStartup));
            Fn(game, "player", a =>
            {
                var t = new Table(script);
                if (!Game.InWorld) return DynValue.NewTable(t);
                var p = Game.Player.transform; var s = Game.Stats;
                t["x"] = Math.Round(p.position.x, 2); t["y"] = Math.Round(p.position.y, 2); t["z"] = Math.Round(p.position.z, 2);
                t["facing"] = Math.Round(p.eulerAngles.y, 1); t["zone"] = Game.Zone;
                if (s != null) { t["name"] = s.dispName; t["gold"] = (double)s.gold.read(); }
                return DynValue.NewTable(t);
            });
        }
    }
}
