using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Find MonoBehaviour types that DECLARE OnGUI().
//
// Unity only runs its IMGUI loop when some enabled MonoBehaviour in the scene declares OnGUI. On this
// VRChat build nothing does at the login screen, so GUIUtility.BeginGUI never fires and the mod's
// menu, radar and HUD have no context to draw in. The mod used to supply that behaviour itself, but
// class injection is dead on 1903.
//
// So borrow one: attaching an existing component that declares OnGUI restarts the whole IMGUI
// pipeline, input included. The right candidate declares OnGUI and as little else as possible -- no
// Awake/Start/Update that would do real work, and the smallest OnGUI body, so adding it costs
// nothing but the loop. Body size is the IL byte count, which is a fair proxy for "does nothing".
//
// usage: ongui <interop dir>
internal static class Program
{
    private static int Main(string[] args)
    {
        var rows = new List<(string asm, string type, int gui, int others, string list)>();
        foreach (var f in Directory.EnumerateFiles(args[0], "*.dll"))
        {
            try
            {
                using var s = File.OpenRead(f);
                using var pe = new PEReader(s);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var th in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(th);
                    var names = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var mh in td.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        string n = md.GetString(m.Name);
                        int size = 0;
                        if (m.RelativeVirtualAddress != 0)
                            try { size = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes().Length; } catch { }
                        names[n] = size;
                    }
                    if (!names.TryGetValue("OnGUI", out int guiSize)) continue;

                    // the other Unity callbacks this type would also start running
                    var callbacks = new[] { "Awake", "OnEnable", "Start", "Update", "LateUpdate", "FixedUpdate", "OnDestroy", "OnDisable", "OnPreRender", "OnPostRender", "OnRenderObject" };
                    var present = callbacks.Where(c => names.ContainsKey(c)).ToList();

                    string ns = md.GetString(td.Namespace), cls = md.GetString(td.Name);
                    rows.Add((Path.GetFileNameWithoutExtension(f), (ns.Length > 0 ? ns + "." : "") + cls, guiSize, present.Count, string.Join(",", present)));
                }
            }
            catch { }
        }

        Console.WriteLine($"types declarant OnGUI : {rows.Count}\n");
        foreach (var r in rows.OrderBy(x => x.others).ThenBy(x => x.gui).Take(30))
            Console.WriteLine($"  OnGUI={r.gui,6} o  autres={r.others}  {r.type,-60} [{r.asm}]  {r.list}");
        return 0;
    }
}
