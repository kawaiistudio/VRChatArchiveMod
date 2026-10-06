using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Read the DEPLOYED P/Invoke table out of a .NET assembly and diff it against final_mapping.csv.
//
// MAPPING_NOTES.md says 17 of the mappings already shipped in the pack are wrong, and names the
// damage: il2cpp_string_new is bound to a two-argument String::NewLen, so every managed-to-il2cpp
// string is allocated with whatever happened to be in the length register. That corrupts the heap
// on the first string and kills the process some calls later, at a point that moves with whatever
// runs in between -- exactly the behaviour observed.
//
// Nothing is written here. This only reports what is bound today versus what the evidence says it
// should be bound to.
//
// usage: implmap <assembly.dll> [final_mapping.csv]
internal static class Program
{
    private static int Main(string[] args)
    {
        using var s = File.OpenRead(args[0]);
        using var pe = new PEReader(s);
        var md = pe.GetMetadataReader();

        var deployed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            var imp = m.GetImport();
            if (imp.Name.IsNil) continue;
            deployed[md.GetString(m.Name)] = md.GetString(imp.Name);
        }
        Console.WriteLine($"{Path.GetFileName(args[0])} : {deployed.Count} P/Invoke");
        int obf = deployed.Count(kv => kv.Value.Length == 11 && !kv.Value.StartsWith("il2cpp_"));
        Console.WriteLine($"  repointes vers un nom obfusque : {obf}");
        Console.WriteLine($"  nommant encore le symbole standard : {deployed.Count - obf}");

        if (args.Length < 2) return 0;

        var want = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(args[1]))
        {
            var c = line.Split(',');
            if (c.Length < 2) continue;
            var fn = c[0].Trim();
            var to = c[1].Trim();
            if (fn.Length == 0 || to.Length == 0) continue;
            want[fn] = to;
        }
        Console.WriteLine($"\nfinal_mapping.csv : {want.Count} lignes\n");

        var wrong = new List<string>();
        var missing = new List<string>();
        int ok = 0;
        foreach (var kv in want.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!deployed.TryGetValue(kv.Key, out var cur)) { missing.Add(kv.Key); continue; }
            if (cur == kv.Value) { ok++; continue; }
            wrong.Add($"  {kv.Key,-46} deploye='{cur}'  ->  devrait etre '{kv.Value}'");
        }
        Console.WriteLine($"deja correct     : {ok}");
        Console.WriteLine($"A CORRIGER       : {wrong.Count}");
        foreach (var w in wrong) Console.WriteLine(w);
        Console.WriteLine($"\nabsent du P/Invoke de cet assembly ({missing.Count}) : {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? " ..." : "")}");
        return 0;
    }
}
