using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Read a Windows minidump without a debugger installed.
//
// An access violation inside il2cpp cannot be caught in .NET, so the usual way to find out where it
// happened is a debugger -- and there is none on this machine. But Windows Error Reporting already
// writes a full dump to %LOCALAPPDATA%\CrashDumps, and the two things worth knowing are in plain
// structures: the exception record (code and faulting address) and the module list (which module
// owns that address). The thread context then gives RIP/RSP, and scanning the stack for values that
// land inside a loaded module reconstructs a usable call chain without any unwind information.
//
// Usage: dmpinfo <file.dmp>
internal static class Program
{
    private static byte[] _d;

    private static uint U32(long o) => BitConverter.ToUInt32(_d, (int)o);
    private static ulong U64(long o) => BitConverter.ToUInt64(_d, (int)o);

    private static string Str(long rva)
    {
        int len = (int)U32(rva);
        return Encoding.Unicode.GetString(_d, (int)rva + 4, len);
    }

    private sealed class Mod
    {
        public ulong Base; public uint Size; public string Name;
        public bool Holds(ulong a) => a >= Base && a < Base + Size;
        public override string ToString() => Path.GetFileName(Name);
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: dmpinfo <file.dmp>"); return 2; }
        _d = File.ReadAllBytes(args[0]);
        if (U32(0) != 0x504D444D) { Console.WriteLine("pas un minidump"); return 2; }

        uint nStreams = U32(8);
        long dir = U32(12);
        var streams = new Dictionary<uint, (uint size, uint rva)>();
        for (int i = 0; i < nStreams; i++)
        {
            long e = dir + i * 12L;
            streams[U32(e)] = (U32(e + 4), U32(e + 8));
        }

        // --- modules (stream 4) ---
        var mods = new List<Mod>();
        if (streams.TryGetValue(4, out var ml))
        {
            uint n = U32(ml.rva);
            for (int i = 0; i < n; i++)
            {
                long m = ml.rva + 4 + i * 108L;
                mods.Add(new Mod { Base = U64(m), Size = U32(m + 8), Name = Str(U32(m + 20)) });
            }
        }
        string Where(ulong a)
        {
            var m = mods.FirstOrDefault(x => x.Holds(a));
            return m == null ? "(hors module)" : $"{m} + 0x{a - m.Base:X}";
        }

        // --- exception (stream 6) ---
        ulong rip = 0, rsp = 0;
        if (streams.TryGetValue(6, out var ex))
        {
            long r = ex.rva;
            uint tid = U32(r);
            uint code = U32(r + 8);
            ulong addr = U64(r + 24);
            uint nParam = U32(r + 32);
            Console.WriteLine($"thread        : {tid}");
            Console.WriteLine($"code          : 0x{code:X8}{(code == 0xC0000005 ? "  (ACCESS_VIOLATION)" : "")}");
            Console.WriteLine($"adresse       : 0x{addr:X}   {Where(addr)}");
            for (int i = 0; i < Math.Min(nParam, 2); i++)
            {
                ulong p = U64(r + 40 + i * 8);
                string what = i == 0 ? (p == 0 ? "lecture" : p == 1 ? "ECRITURE" : p == 8 ? "execution DEP" : p.ToString())
                                     : $"0x{p:X}   {Where(p)}";
                Console.WriteLine($"  parametre {i} : {what}");
            }
            long ctxRva = U32(r + 164);
            if (ctxRva > 0) { rsp = U64(ctxRva + 0x98); rip = U64(ctxRva + 0xF8); }
            Console.WriteLine($"RIP           : 0x{rip:X}   {Where(rip)}");
            Console.WriteLine($"RSP           : 0x{rsp:X}");
        }

        // --- memory, so the stack can be read (stream 9 preferred, else 3) ---
        var ranges = new List<(ulong start, ulong size, long rva)>();
        if (streams.TryGetValue(9, out var m64))
        {
            ulong n = U64(m64.rva);
            long baseRva = (long)U64(m64.rva + 8);
            long cur = baseRva;
            for (ulong i = 0; i < n; i++)
            {
                long e = m64.rva + 16 + (long)i * 16;
                ulong st = U64(e), sz = U64(e + 8);
                ranges.Add((st, sz, cur));
                cur += (long)sz;
            }
        }
        else if (streams.TryGetValue(3, out var m32))
        {
            uint n = U32(m32.rva);
            for (int i = 0; i < n; i++)
            {
                long e = m32.rva + 4 + i * 16L;
                ranges.Add((U64(e), U32(e + 8), U32(e + 12)));
            }
        }

        bool Read(ulong addr, out ulong val)
        {
            val = 0;
            foreach (var (st, sz, rva) in ranges)
                if (addr >= st && addr + 8 <= st + sz)
                {
                    long at = rva + (long)(addr - st);
                    // A dump can declare a range whose bytes were truncated on write; trust the file
                    // length rather than the descriptor.
                    if (at < 0 || at + 8 > _d.Length) return false;
                    val = U64(at);
                    return true;
                }
            return false;
        }

        // Poor man's stack walk: every stack slot that points into a loaded module is a plausible
        // return address. Noisy by nature, so it is printed as candidates, not as a call stack.
        if (rsp != 0 && ranges.Count > 0)
        {
            Console.WriteLine("\nretours plausibles sur la pile (du plus recent au plus ancien) :");
            int shown = 0;
            for (ulong off = 0; off < 0x4000 && shown < 30; off += 8)
            {
                if (!Read(rsp + off, out ulong v) || v == 0) continue;
                var m = mods.FirstOrDefault(x => x.Holds(v));
                if (m == null) continue;
                Console.WriteLine($"  rsp+0x{off:X4}  0x{v:X}   {m} + 0x{v - m.Base:X}");
                shown++;
            }
        }
        return 0;
    }
}
