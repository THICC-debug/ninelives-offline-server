using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NineLivesEditor
{
    /// <summary>Files built into the exe: the web pages, and WebView2's own DLLs.</summary>
    static class Resources
    {
        static byte[] Read(string name)
        {
            using (var s = typeof(Resources).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var b = new byte[s.Length];
                int got = 0;
                while (got < b.Length) got += s.Read(b, got, b.Length - got);
                return b;
            }
        }

        /// <summary>A page or stylesheet from editor/web.</summary>
        public static byte[] Web(string file) { return Read("web/" + file); }

        /// <summary>Load WebView2's managed DLLs from inside the exe when .NET asks for them.</summary>
        public static void HookAssemblies()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var bytes = Read("lib/" + name + ".dll");
                return bytes == null ? null : Assembly.Load(bytes);
            };
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);

        /// <summary>
        /// WebView2Loader.dll is native, so it can't load from memory: write it to a folder in
        /// LocalAppData (once per version) and tell Windows to look there.
        /// </summary>
        public static void PrepareNativeLoader()
        {
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            var bytes = Read("native/" + arch + "/WebView2Loader.dll");
            if (bytes == null) return;
            var dir = Path.Combine(AppData, "runtime-" + arch + "-" + bytes.Length);
            var file = Path.Combine(dir, "WebView2Loader.dll");
            Directory.CreateDirectory(dir);
            if (!File.Exists(file) || new FileInfo(file).Length != bytes.Length) File.WriteAllBytes(file, bytes);
            SetDllDirectory(dir);
        }

        public static string AppData
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NineLivesOffline", "Editor"); }
        }
    }
}
