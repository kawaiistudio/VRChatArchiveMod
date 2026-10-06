using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

// v11.0 -- WHY does every token resolve to the next method?
//
// v10 showed each cached MethodInfo is the one AFTER the intended method, uniformly, on every class
// probed. Two things could do that: the 1886 tokens simply naming a different method on 1903's
// metadata (one method removed early in the assembly shifts every later token by one), or the
// token lookup itself reading the wrong slot. This prints the live method table of a few classes
// with index, il2cpp's own name, the token il2cpp reports for it, and the token the struct handler
// reads -- against the constants extracted from the interop's IL (tokmap), which one it is becomes a
// matter of reading two columns.
[BepInPlugin("va.probe", "VA Probe", "11.0.0")]
public class Probe : BasePlugin
{
    private static string _f;

    private static void Say(string s)
    {
        try
        {
            using var fs = new FileStream(_f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var b = Encoding.UTF8.GetBytes(s + Environment.NewLine);
            fs.Write(b, 0, b.Length);
            fs.Flush(true);
        }
        catch { }
    }

    private static unsafe void Table(string asm, string ns, string cls, int max)
    {
        Say("");
        Say("### " + ns + "." + cls);
        IntPtr klass = IL2CPP.GetIl2CppClass(asm, ns, cls);
        Say("  classe 0x" + klass.ToString("X"));
        IntPtr iter = IntPtr.Zero, mi;
        int i = 0;
        while (i < max)
        {
            try { mi = IL2CPP.il2cpp_class_get_methods(klass, ref iter); }
            catch (Exception e) { Say("  il2cpp_class_get_methods : " + e.GetType().Name + " " + e.Message); return; }
            if (mi == IntPtr.Zero) break;
            string nm = "?", tokNative = "?", tokHandler = "?";
            try
            {
                var w = UnityVersionHandler.Wrap((Il2CppMethodInfo*)mi);
                nm = w.Name == IntPtr.Zero ? "<null>" : Marshal.PtrToStringAnsi(w.Name);
                tokHandler = "0x" + w.Token.ToString("X8");
            }
            catch (Exception e) { nm = "EXC " + e.GetType().Name; }
            try { tokNative = "0x" + IL2CPP.il2cpp_method_get_token(mi).ToString("X8"); }
            catch (Exception e) { tokNative = "EXC " + e.GetType().Name; }
            Say($"  [{i,3}] 0x{mi.ToString("X")}  natif={tokNative}  handler={tokHandler}  {nm}");
            i++;
        }
    }

    public override void Load()
    {
        _f = Path.Combine(Paths.BepInExRootPath, "probe.log");
        try { File.WriteAllText(_f, "=== VA Probe 11.0 : table de methodes vivante ===" + Environment.NewLine); } catch { }
        Log.LogMessage("[Probe] v11.0 -> " + _f);

        const string core = "UnityEngine.CoreModule.dll";
        Table(core, "UnityEngine", "Time", 40);
        Table(core, "UnityEngine.SceneManagement", "SceneManager", 40);
        Table(core, "UnityEngine", "GameObject", 24);
        Table("mscorlib.dll", "System", "Object", 16);

        Say("");
        Say("=== FIN, PROCESS VIVANT ===");
        Log.LogMessage("[Probe] termine");
    }
}
