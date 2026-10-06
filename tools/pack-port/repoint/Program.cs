using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

// Bind Il2CppInterop's P/Invokes to the obfuscated export names proven for THIS VRChat build.
//
// MAPPING_NOTES.md derived the correct name for 100 il2cpp API functions by disassembling the
// shipped GameAssembly.dll. Comparing that table against what the pack actually deploys shows 98
// are wrong -- among them il2cpp_string_new, which is bound to an export that takes (string,
// length). Il2CppInterop calls it for every managed string it hands to il2cpp, so every such string
// is allocated with whatever happened to be in the length register. That corrupts the heap on the
// first string and ends the process a few calls later, at a point that moves depending on what runs
// in between -- which is exactly the behaviour observed while hunting this.
//
// The names CANNOT be patched in place. A P/Invoke's entry-point string and the managed method's
// own name are identical here, so the compiler stored them once and overwrote bytes would rename the
// method as well -- and code elsewhere looks those methods up by name. Cecil is used instead, so a
// fresh heap entry is created for the entry point and the method name is untouched.
//
// usage: repoint <assembly.dll> <final_mapping.csv> [--apply]
internal static class Program
{
    private static int Main(string[] args)
    {
        string path = args[0], csv = args[1];
        bool apply = args.Contains("--apply");

        var want = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(csv))
        {
            var c = line.Split(',');
            if (c.Length < 2) continue;
            string fn = c[0].Trim(), to = c[1].Trim();
            if (fn.Length == 0 || to.Length == 0) continue;
            want[fn] = to;
        }

        var asm = AssemblyDefinition.ReadAssembly(path);
        int changed = 0, already = 0, absent = 0;
        var log = new List<string>();
        foreach (var t in asm.MainModule.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasPInvokeInfo) continue;
                if (!want.TryGetValue(m.Name, out var target)) continue;
                var pi = m.PInvokeInfo;
                if (pi.EntryPoint == target) { already++; continue; }
                log.Add($"  {m.Name,-44} '{pi.EntryPoint}' -> '{target}'");
                if (apply) m.PInvokeInfo = new PInvokeInfo(pi.Attributes, target, pi.Module);
                changed++;
            }
        foreach (var kv in want) if (!asm.MainModule.GetTypes().SelectMany(x => x.Methods).Any(x => x.HasPInvokeInfo && x.Name == kv.Key)) absent++;

        Console.WriteLine($"assembly   : {Path.GetFileName(path)}");
        Console.WriteLine($"a corriger : {changed}   deja bon : {already}   absent : {absent}");
        Console.WriteLine($"mode       : {(apply ? "APPLY (ecriture)" : "SIMULATION")}");
        foreach (var l in log.Take(8)) Console.WriteLine(l);
        if (log.Count > 8) Console.WriteLine($"  ... et {log.Count - 8} autres");

        if (apply && changed > 0)
        {
            string bak = path + ".bak-prefix1903";
            if (!File.Exists(bak)) File.Copy(path, bak);
            string tmp = path + ".new";
            asm.Write(tmp);
            asm.Dispose();
            File.Copy(tmp, path, true);
            File.Delete(tmp);
            Console.WriteLine($"ECRIT (sauvegarde : {Path.GetFileName(bak)})");
        }
        return 0;
    }
}
