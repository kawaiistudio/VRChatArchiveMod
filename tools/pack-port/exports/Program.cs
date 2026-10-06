using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Dump a PE's export table (name -> RVA) and validate a mapping against it.
//
// Two names in final_mapping.csv resolved to the same obfuscated export, and binding both broke
// startup with EntryPointNotFound. Identical code folding means one function body can carry several
// export names, so an alias used twice is a table bug, not a discovery. Rather than fix that one row
// blind, check every target name in the mapping against the real export table first: names that do
// not exist at all, and names claimed by more than one function, both show up here.
//
// usage: exports <pe.dll> [mapping.csv]
internal static class Program
{
    private static int Main(string[] args)
    {
        var d = File.ReadAllBytes(args[0]);
        int pe = BitConverter.ToInt32(d, 0x3C);
        int opt = pe + 24;
        bool p32 = BitConverter.ToUInt16(d, opt) == 0x10B;
        int dirs = opt + (p32 ? 96 : 112);
        uint expRva = BitConverter.ToUInt32(d, dirs);
        if (expRva == 0) { Console.WriteLine("aucun export"); return 1; }

        int nSec = BitConverter.ToUInt16(d, pe + 6);
        int secs = opt + BitConverter.ToUInt16(d, pe + 20);
        long ToOff(uint rva)
        {
            for (int i = 0; i < nSec; i++)
            {
                int s = secs + i * 40;
                uint va = BitConverter.ToUInt32(d, s + 12), sz = BitConverter.ToUInt32(d, s + 16), raw = BitConverter.ToUInt32(d, s + 20);
                if (rva >= va && rva < va + sz) return raw + (rva - va);
            }
            return -1;
        }
        string Asciiz(long o)
        {
            int e = (int)o; while (d[e] != 0) e++;
            return Encoding.ASCII.GetString(d, (int)o, e - (int)o);
        }

        long ed = ToOff(expRva);
        uint nNames = BitConverter.ToUInt32(d, (int)ed + 24);
        uint aFunc = BitConverter.ToUInt32(d, (int)ed + 28);
        uint aName = BitConverter.ToUInt32(d, (int)ed + 32);
        uint aOrd = BitConverter.ToUInt32(d, (int)ed + 36);

        var byName = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < nNames; i++)
        {
            uint nr = BitConverter.ToUInt32(d, (int)ToOff(aName) + i * 4);
            ushort ord = BitConverter.ToUInt16(d, (int)ToOff(aOrd) + i * 2);
            uint fr = BitConverter.ToUInt32(d, (int)ToOff(aFunc) + ord * 4);
            byName[Asciiz(ToOff(nr))] = fr;
        }
        Console.WriteLine($"{Path.GetFileName(args[0])} : {byName.Count} exports nommes, {byName.Values.Distinct().Count()} RVA distinctes");
        if (args.Length < 2) return 0;

        var want = new List<(string fn, string to)>();
        foreach (var line in File.ReadAllLines(args[1]))
        {
            var c = line.Split(',');
            if (c.Length >= 2 && c[0].Trim().Length > 0 && c[1].Trim().Length > 0) want.Add((c[0].Trim(), c[1].Trim()));
        }

        var absent = want.Where(w => !byName.ContainsKey(w.to)).ToList();
        Console.WriteLine($"\ncibles absentes de la table d'exports : {absent.Count}");
        foreach (var a in absent) Console.WriteLine($"  {a.fn} -> '{a.to}'");

        var dup = want.Where(w => byName.ContainsKey(w.to)).GroupBy(w => w.to).Where(g => g.Count() > 1).ToList();
        Console.WriteLine($"\nnoms d'export utilises par plusieurs fonctions : {dup.Count}");
        foreach (var g in dup)
        {
            uint rva = byName[g.Key];
            var aliases = byName.Where(kv => kv.Value == rva).Select(kv => kv.Key).ToList();
            Console.WriteLine($"  '{g.Key}' (RVA 0x{rva:X}) reclame par : {string.Join(", ", g.Select(x => x.fn))}");
            Console.WriteLine($"     alias du meme corps ({aliases.Count}) : {string.Join(", ", aliases)}");
        }
        return 0;
    }
}
