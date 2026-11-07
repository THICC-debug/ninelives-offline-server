using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NineLivesEditor
{
    /// <summary>Small JSON helpers on top of .NET's own serializer (no packages needed).</summary>
    static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        /// <summary>Objects become Dictionary&lt;string, object&gt;, arrays object[], numbers int/long/decimal.</summary>
        public static object Parse(string text) { return Ser.DeserializeObject(text); }

        public static string Write(object o) { return Ser.Serialize(o); }

        /// <summary>A file's text without a BOM, or null if it isn't there.</summary>
        public static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿') : null; }
            catch (IOException) { return null; }
        }

        /// <summary>Parsed file, or null if missing or broken.</summary>
        public static object Load(string path)
        {
            var t = ReadText(path);
            if (t == null) return null;
            try { return Parse(t); } catch { return null; }
        }

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static IList List(object o) { return o as IList; }

        public static object Get(object o, string key)
        {
            var d = Obj(o); object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(object o, string key) { var v = Get(o, key); return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }

        public static long Num(object o, string key)
        {
            var v = Get(o, key);
            try { return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        /// <summary>Pretty JSON (2-space indent, non-ASCII kept as is), like the packs the mods write.</summary>
        public static string Pretty(object o)
        {
            var sb = new StringBuilder();
            WritePretty(sb, o, 0);
            return sb.ToString();
        }

        static void WritePretty(StringBuilder sb, object o, int depth)
        {
            var pad = new string(' ', (depth + 1) * 2);
            var end = new string(' ', depth * 2);
            var d = o as IDictionary;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (DictionaryEntry kv in d)
                {
                    sb.Append(pad); WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture)); sb.Append(": ");
                    WritePretty(sb, kv.Value, depth + 1);
                    sb.Append(++i < d.Count ? ",\n" : "\n");
                }
                sb.Append(end).Append('}');
                return;
            }
            if (o is string) { WriteString(sb, (string)o); return; }
            var l = o as IEnumerable;
            if (l != null)
            {
                var items = new List<object>();
                foreach (var x in l) items.Add(x);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(pad); WritePretty(sb, items[i], depth + 1);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(end).Append(']');
                return;
            }
            if (o == null) sb.Append("null");
            else if (o is bool) sb.Append((bool)o ? "true" : "false");
            else if (o is double || o is float) sb.Append(Convert.ToDouble(o).ToString("R", CultureInfo.InvariantCulture));
            else if (o is decimal) sb.Append(((decimal)o).ToString(CultureInfo.InvariantCulture));
            else sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
