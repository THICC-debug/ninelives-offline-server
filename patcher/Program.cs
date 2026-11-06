using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Nine Lives offline patcher.
// Usage:  dotnet run --project patcher -- "<path to Ninelives folder>"          (patch)
//         dotnet run --project patcher -- "<path to Ninelives folder>" --restore (undo)
//
// Backs up the two original DLLs to Ninelives_Data\OfflineBackup the first time,
// always patches from that backup (so running it twice is safe), and checks the
// originals are the 0.24.3 files this was written for.

const string UnityScript = "Assembly-UnityScript.dll";
const string FirstPass = "Assembly-CSharp-firstpass.dll";
var known = new System.Collections.Generic.Dictionary<string, string> {
    [UnityScript] = "fdbeb458a0ab8eccedf19e36314efb9e",
    [FirstPass] = "cc1575888cd457cf7ae230d479ac5dcd",
};

if (args.Length < 1)
{
    Console.WriteLine("Usage: patcher <Ninelives folder> [--restore]");
    return 1;
}
var game = args[0];
var managed = Path.Combine(game, "Ninelives_Data", "Managed");
var backup = Path.Combine(game, "Ninelives_Data", "OfflineBackup");
if (!Directory.Exists(managed))
{
    Console.WriteLine("Can't find " + managed + " - point this at the folder that holds Ninelives.exe.");
    return 1;
}

static string Md5(string path)
{
    using var md5 = MD5.Create();
    using var s = File.OpenRead(path);
    return Convert.ToHexString(md5.ComputeHash(s)).ToLowerInvariant();
}

Directory.CreateDirectory(backup);
foreach (var name in known.Keys)
{
    var b = Path.Combine(backup, name);
    if (!File.Exists(b))
    {
        var live = Path.Combine(managed, name);
        if (Md5(live) != known[name])
        {
            Console.WriteLine(name + " is not the original 0.24.3 file and there is no backup. Stopping.");
            return 1;
        }
        File.Copy(live, b);
        Console.WriteLine("Backed up " + name);
    }
    else if (Md5(b) != known[name])
    {
        Console.WriteLine("Backup of " + name + " doesn't match the original 0.24.3 file. Stopping.");
        return 1;
    }
}

const string LoaderDll = "NineLivesModLoader.dll";
if (args.Contains("--restore"))
{
    foreach (var name in known.Keys)
        File.Copy(Path.Combine(backup, name), Path.Combine(managed, name), true);
    var installedLoader = Path.Combine(managed, LoaderDll);
    if (File.Exists(installedLoader)) File.Delete(installedLoader);
    Console.WriteLine("Restored the original game files.");
    return 0;
}

// Optional: --loader <path to NineLivesModLoader.dll> installs the mod loader too.
string? loaderPath = null;
int li = Array.IndexOf(args, "--loader");
if (li >= 0 && li + 1 < args.Length) loaderPath = Path.GetFullPath(args[li + 1]);
if (loaderPath != null && !File.Exists(loaderPath))
{
    Console.WriteLine("Mod loader not found: " + loaderPath);
    return 1;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var rp = new ReaderParameters { AssemblyResolver = resolver };

// 1) Server address -> 127.0.0.1:8080, and roll loot locally.
{
    using var asm = AssemblyDefinition.ReadAssembly(Path.Combine(backup, UnityScript), rp);
    var mod = asm.MainModule;

    var cctor = mod.GetType("ServerDefine").Methods.First(m => m.Name == ".cctor");
    int n = 0;
    foreach (var ins in cctor.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
    {
        var s = (string)ins.Operand;
        if (s == "path") { ins.Operand = "127"; n++; }
        else if (s == "smokymonkeys.com") { ins.Operand = "0.0.1"; n++; }
    }
    if (n != 2) throw new Exception("ServerDefine: expected 2 strings, patched " + n);

    var vd = mod.GetType("VersionDefine");
    var vcctor = vd.Methods.First(m => m.Name == ".cctor");
    var il = vcctor.Body.GetILProcessor();
    var ret = vcctor.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
    il.InsertBefore(ret, il.Create(OpCodes.Ldc_I4_1));
    il.InsertBefore(ret, il.Create(OpCodes.Stsfld, vd.Fields.First(f => f.Name == "offlineItemDrop")));

    Console.WriteLine("Server address -> http://127.0.0.1:8080/kyrill/, offline loot on");

    if (loaderPath != null)
    {
        File.Copy(loaderPath, Path.Combine(managed, LoaderDll), true);
        using var loader = AssemblyDefinition.ReadAssembly(loaderPath);
        var init = mod.ImportReference(loader.MainModule.GetType("NineLivesModLoader.ModLoader")
                                             .Methods.First(m => m.Name == "Init"));
        // Call ModLoader.Init() from two static constructors that run at startup:
        // the module initializer and Universe (the game's global state class).
        // Init() only runs once, whichever fires first.
        foreach (var type in new[] { mod.GetType("<Module>"), mod.GetType("Universe") })
        {
            var init_cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (init_cctor == null)
            {
                init_cctor = new MethodDefinition(".cctor",
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, mod.TypeSystem.Void);
                init_cctor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(init_cctor);
            }
            init_cctor.Body.GetILProcessor().InsertBefore(init_cctor.Body.Instructions[0], Instruction.Create(OpCodes.Call, init));
        }
        Directory.CreateDirectory(Path.Combine(game, "Mods", "lib"));
        // The loader uses Harmony itself (events, mod list); it ships next to the loader DLL.
        var harmonyDll = Path.Combine(Path.GetDirectoryName(loaderPath)!, "0Harmony.dll");
        if (File.Exists(harmonyDll)) File.Copy(harmonyDll, Path.Combine(game, "Mods", "lib", "0Harmony.dll"), true);
        else Console.WriteLine("Warning: 0Harmony.dll not found next to the loader; events and the F11 mod list won't work.");
        Console.WriteLine("Mod loader installed (mods go in " + Path.Combine(game, "Mods") + ")");
    }

    asm.Write(Path.Combine(managed, UnityScript));
}

// 2) Send Google Analytics hits to the local server instead of Google.
{
    using var asm = AssemblyDefinition.ReadAssembly(Path.Combine(backup, FirstPass), rp);
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
    Console.WriteLine("Google Analytics redirected to the local server (" + n + ")");
}

Console.WriteLine("Done. Start the server, then the game.");
return 0;
