using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NineLivesPatch
{
    /// <summary>
    /// Patches Ninelives 0.24.3 for the offline server, installs the mod loader, and undoes it.
    /// Shared by the developer patcher (patcher/) and Setup.exe (installer/). Builds on .NET 8 and .NET Framework 4.8.
    ///
    /// The two original DLLs are backed up to Ninelives_Data\OfflineBackup the first time, patching
    /// always starts from that backup (so running it twice is safe), and the originals are checked
    /// to be the 0.24.3 files this was written for.
    /// </summary>
    public static class GamePatcher
    {
        public const string UnityScript = "Assembly-UnityScript.dll";
        public const string FirstPass = "Assembly-CSharp-firstpass.dll";
        public const string LoaderDll = "NineLivesModLoader.dll";

        /// <summary>MD5 of the original 0.24.3 files.</summary>
        public static readonly Dictionary<string, string> Originals = new Dictionary<string, string>
        {
            { UnityScript, "fdbeb458a0ab8eccedf19e36314efb9e" },
            { FirstPass, "cc1575888cd457cf7ae230d479ac5dcd" },
        };

        public static string Managed(string game) { return Path.Combine(Path.Combine(game, "Ninelives_Data"), "Managed"); }
        public static string Backup(string game) { return Path.Combine(Path.Combine(game, "Ninelives_Data"), "OfflineBackup"); }

        public static bool LooksLikeGame(string game)
        {
            return !string.IsNullOrEmpty(game) && File.Exists(Path.Combine(game, "Ninelives.exe")) && Directory.Exists(Managed(game));
        }

        public static string Md5(string path)
        {
            using (var md5 = MD5.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Make sure there's a verified backup of the originals. Throws with a readable message if not possible.</summary>
        public static void EnsureBackup(string game, Action<string> log)
        {
            var managed = Managed(game);
            var backup = Backup(game);
            Directory.CreateDirectory(backup);
            foreach (var kv in Originals)
            {
                var b = Path.Combine(backup, kv.Key);
                if (!File.Exists(b))
                {
                    var live = Path.Combine(managed, kv.Key);
                    if (Md5(live) != kv.Value)
                        throw new InvalidOperationException(kv.Key + " is not the original Ninelives 0.24.3 file, and there's no backup of it. " +
                                                            "This only works with an unmodified 0.24.3 install.");
                    File.Copy(live, b);
                    log("Backed up " + kv.Key);
                }
                else if (Md5(b) != kv.Value)
                    throw new InvalidOperationException("The backup of " + kv.Key + " isn't the original 0.24.3 file.");
            }
        }

        /// <summary>Is the game currently patched (its DLLs differ from the originals)?</summary>
        public static bool IsPatched(string game)
        {
            return File.Exists(Path.Combine(Managed(game), LoaderDll))
                || Originals.Any(kv => File.Exists(Path.Combine(Managed(game), kv.Key)) && Md5(Path.Combine(Managed(game), kv.Key)) != kv.Value);
        }

        /// <summary>Put the original game files back and remove the mod loader DLL.</summary>
        public static void Restore(string game, Action<string> log)
        {
            EnsureBackup(game, log);
            foreach (var name in Originals.Keys)
                File.Copy(Path.Combine(Backup(game), name), Path.Combine(Managed(game), name), true);
            var installedLoader = Path.Combine(Managed(game), LoaderDll);
            if (File.Exists(installedLoader)) File.Delete(installedLoader);
            log("Restored the original game files.");
        }

        /// <summary>
        /// Patch the game. loaderPath (optional) = NineLivesModLoader.dll to install; 0Harmony.dll is
        /// taken from the same folder and put in Mods\lib.
        /// </summary>
        public static void Patch(string game, string loaderPath, Action<string> log)
        {
            var managed = Managed(game);
            var backup = Backup(game);
            EnsureBackup(game, log);
            if (loaderPath != null && !File.Exists(loaderPath)) throw new FileNotFoundException("Mod loader not found: " + loaderPath);

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);
            var rp = new ReaderParameters { AssemblyResolver = resolver };

            // 1) Server address -> 127.0.0.1:8080, and roll loot locally.
            using (var asm = AssemblyDefinition.ReadAssembly(Path.Combine(backup, UnityScript), rp))
            {
                var mod = asm.MainModule;
                var cctor = mod.GetType("ServerDefine").Methods.First(m => m.Name == ".cctor");
                int n = 0;
                foreach (var ins in cctor.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
                {
                    var s = (string)ins.Operand;
                    if (s == "path") { ins.Operand = "127"; n++; }
                    else if (s == "smokymonkeys.com") { ins.Operand = "0.0.1"; n++; }
                }
                if (n != 2) throw new InvalidOperationException("ServerDefine: expected 2 strings, patched " + n);

                var vd = mod.GetType("VersionDefine");
                var vcctor = vd.Methods.First(m => m.Name == ".cctor");
                var il = vcctor.Body.GetILProcessor();
                var ret = vcctor.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(ret, il.Create(OpCodes.Stsfld, vd.Fields.First(f => f.Name == "offlineItemDrop")));
                log("Server address -> http://127.0.0.1:8080/kyrill/, offline loot on");

                if (loaderPath != null)
                {
                    File.Copy(loaderPath, Path.Combine(managed, LoaderDll), true);
                    using (var loader = AssemblyDefinition.ReadAssembly(loaderPath))
                    {
                        var init = mod.ImportReference(loader.MainModule.GetType("NineLivesModLoader.ModLoader").Methods.First(m => m.Name == "Init"));
                        // Call ModLoader.Init() from two static constructors that run at startup: the module
                        // initializer and Universe (the game's global state class). Init() only runs once.
                        foreach (var type in new[] { mod.GetType("<Module>"), mod.GetType("Universe") })
                        {
                            var c = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
                            if (c == null)
                            {
                                c = new MethodDefinition(".cctor",
                                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
                                    MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, mod.TypeSystem.Void);
                                c.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                                type.Methods.Add(c);
                            }
                            c.Body.GetILProcessor().InsertBefore(c.Body.Instructions[0], Instruction.Create(OpCodes.Call, init));
                        }
                    }
                    var lib = Path.Combine(Path.Combine(game, "Mods"), "lib");
                    Directory.CreateDirectory(lib);
                    // The loader uses Harmony itself (events, mod list); it ships next to the loader DLL.
                    var harmonyDll = Path.Combine(Path.GetDirectoryName(loaderPath), "0Harmony.dll");
                    if (File.Exists(harmonyDll)) File.Copy(harmonyDll, Path.Combine(lib, "0Harmony.dll"), true);
                    else log("Warning: 0Harmony.dll not found next to the loader; events and the F11 mod list won't work.");
                    log("Mod loader installed (mods go in " + Path.Combine(game, "Mods") + ")");
                }
                asm.Write(Path.Combine(managed, UnityScript));
            }

            // 2) Send Google Analytics hits to the local server instead of Google.
            using (var asm = AssemblyDefinition.ReadAssembly(Path.Combine(backup, FirstPass), rp))
            {
                int n = 0;
                foreach (var t in asm.MainModule.GetTypes())
                    foreach (var m in t.Methods.Where(m => m.HasBody))
                        foreach (var ins in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
                            if (((string)ins.Operand).StartsWith("https://www.google-analytics.com/collect"))
                            {
                                ins.Operand = ((string)ins.Operand).Replace("https://www.google-analytics.com", "http://127.0.0.1:8080");
                                n++;
                            }
                asm.Write(Path.Combine(managed, FirstPass));
                log("Google Analytics redirected to the local server (" + n + ")");
            }
        }
    }
}
