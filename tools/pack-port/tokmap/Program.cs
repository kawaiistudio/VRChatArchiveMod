using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Read, out of Il2CppInterop-generated assemblies, the metadata TOKEN each proxy method is bound to.
//
// Every generated type initializer does
//     ldc.i4  <token>
//     call    IL2CPP.GetIl2CppMethodByToken(IntPtr, int)
//     stsfld  NativeMethodInfoPtr_<method>_<attrs>_<ret>_<params>_<n>
// so the cctor's IL pairs a token with a field, and the field name carries the il2cpp method name.
// Fields are bound the same way, by NAME:
//     ldstr   "<the real il2cpp field name>"
//     call    IL2CPP.GetIl2CppField(IntPtr, string)
//     stsfld  NativeFieldInfoPtr_<field>
//
// Those tokens and names were valid on VRChat build 1886. On 1903 the Unity assemblies changed, and one
// method removed early in an assembly shifts every later token by one: each proxy then silently resolves
// to the NEXT method in its class (measured: get_transform -> get_layer, GetActiveScene ->
// SetActiveScene). Worse, VRChat re-randomises its own member names on every update, so for its own
// classes neither the token nor the name means anything on the new build.
//
// The mod repairs both at runtime, and this tool produces what it needs:
//   * NAME ANCHORS, for the methods whose names are plain text on both builds;
//   * a POSITIONAL row per type carrying its shape (method and field counts, base type), which is how a
//     RENAMED class is identified -- a rename does not change how many members a class has;
//   * and the SHAPE OF EVERY MEMBER, in declaration order. A rename does not change a method's arity,
//     its staticness, or the kind of each of its parameters either. Lining that sequence up against the
//     live class recovers the whole member table of a class whose names are all gone, which is the only
//     way the members of a recovered class can ever be called again.
//
// usage: tokmap <interop.dll> [TypeName ...]              list (type, token, field) for a few types
//        tokmap --table <interop dir> <out.tsv>           anchor table for every assembly
//
// Table rows:
//   anchor  : namespace \t class \t declaring \t token(hex) \t il2cpp name \t param count \t static
//   position: namespace \t class \t declaring \t minToken(hex) \t *POS* \t methods \t fields \t
//             typeToken(hex) \t base \t ifaces \t typeAttrs \t method shapes \t field shapes
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--table") return Table(args[1], args[2]);
        if (args.Length < 1) { Console.WriteLine("usage: tokmap <dll> [Type...] | --table <dir> <out.tsv>"); return 2; }
        using var s = File.OpenRead(args[0]);
        using var pe = new PEReader(s);
        var md = pe.GetMetadataReader();
        var only = new HashSet<string>(args.Skip(1), StringComparer.Ordinal);
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            string tn = md.GetString(td.Name);
            if (only.Count > 0 && !only.Contains(tn)) continue;
            var readers = Readers(pe, md, td);
            foreach (var (token, fn, ftok) in TokenFields(pe, md, td))
                Console.WriteLine($"{tn}\t0x{token:X8}\t{Shape(md, readers, ftok)}\t{fn.Substring("NativeMethodInfoPtr_".Length)}");
            foreach (var (name, fn, ftok) in NameFields(pe, md, td))
                Console.WriteLine($"{tn}\tFIELD\t{FieldShape(md, readers, ftok)}\t{fn.Substring("NativeFieldInfoPtr_".Length)}\t{name}");
        }
        return 0;
    }

    // ---- IL walking ------------------------------------------------------------------------------
    //
    // Scanning for an opcode byte-by-byte finds it inside operands too. That was tolerable for the
    // three-opcode cctor pattern, but the member shapes below are read off single `ldsfld`s, where one
    // false positive would attribute a signature to the wrong member -- and a wrong signature is how a
    // member ends up bound to a real method with the wrong arity, which is an access violation on the
    // first call. So the IL is decoded properly, with an operand-size table.
    private static readonly sbyte[] Sz1 = BuildSz1();
    private static readonly sbyte[] Sz2 = BuildSz2();

    private static sbyte[] BuildSz1()
    {
        var t = new sbyte[256];
        void Set(int from, int to, sbyte n) { for (int i = from; i <= to; i++) t[i] = n; }
        Set(0x0E, 0x13, 1);                       // ldarg.s .. stloc.s
        t[0x1F] = 1;                              // ldc.i4.s
        t[0x20] = 4; t[0x21] = 8;                 // ldc.i4 / ldc.i8
        t[0x22] = 4; t[0x23] = 8;                 // ldc.r4 / ldc.r8
        Set(0x27, 0x29, 4);                       // jmp / call / calli
        Set(0x2B, 0x37, 1);                       // br.s .. bgt.un.s
        Set(0x38, 0x44, 4);                       // br .. bgt.un
        t[0x45] = -1;                             // switch
        Set(0x6F, 0x75, 4);                       // callvirt / cpobj / ldobj / ldstr / newobj / castclass / isinst
        t[0x79] = 4;                              // unbox
        Set(0x7B, 0x81, 4);                       // ldfld .. stobj
        t[0x8D] = 4; t[0x8F] = 4;                 // newarr / ldelema
        Set(0xA3, 0xA5, 4);                       // ldelem / stelem / unbox.any
        t[0xC2] = 4; t[0xC6] = 4;                 // refanyval / mkrefany
        t[0xD0] = 4;                              // ldtoken
        t[0xDD] = 4; t[0xDE] = 1;                 // leave / leave.s
        return t;
    }

    private static sbyte[] BuildSz2()
    {
        var t = new sbyte[256];
        t[0x06] = 4; t[0x07] = 4;                 // ldftn / ldvirtftn
        for (int i = 0x09; i <= 0x0E; i++) t[i] = 2;   // ldarg .. stloc
        t[0x12] = 1;                              // unaligned.
        t[0x15] = 4; t[0x16] = 4;                 // initobj / constrained.
        t[0x19] = 1;                              // no.
        t[0x1C] = 4;                              // sizeof
        return t;
    }

    // (opcode, two-byte?, 4-byte operand) for each instruction, in order.
    private static IEnumerable<(int op, bool ext, int arg)> Walk(byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            byte b = il[i++];
            bool ext = b == 0xFE;
            int op = b;
            sbyte n;
            if (ext)
            {
                if (i >= il.Length) yield break;
                op = il[i++];
                n = Sz2[op];
            }
            else n = Sz1[op];

            if (n == -1)                                   // switch
            {
                // A body that is not really IL (or one this walker mis-stepped into) can present a
                // switch with a nonsense count; trusting it walks the cursor off the array, and the
                // exception took a whole assembly's anchors with it.
                if (i + 4 > il.Length) yield break;
                int cnt = BitConverter.ToInt32(il, i);
                if (cnt < 0 || cnt > (il.Length - i) / 4) yield break;
                i += 4 + 4 * cnt;
                continue;
            }
            int arg = n == 4 && i + 4 <= il.Length ? BitConverter.ToInt32(il, i) : 0;
            yield return (op, ext, arg);
            if (n < 0 || i > il.Length - n) yield break;
            i += n;
        }
    }

    // Every `stsfld NativeMethodInfoPtr_*` of the type initializer, with the token it was given, in the
    // order the cctor emits them -- which is il2cpp's own method order for the class.
    private static IEnumerable<(int token, string field, int fieldTok)> TokenFields(PEReader pe, MetadataReader md, TypeDefinition td)
    {
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (md.GetString(m.Name) != ".cctor" || m.RelativeVirtualAddress == 0) continue;
            byte[] il;
            try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
            int pending = int.MinValue;
            foreach (var (op, ext, arg) in Walk(il))
            {
                if (ext) continue;
                if (op == 0x20) { pending = arg; continue; }               // ldc.i4 <token>
                if (op == 0x80 && pending != int.MinValue)                 // stsfld
                {
                    string fn = FieldName(md, arg);
                    if (fn != null && fn.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal))
                        yield return (pending, fn, arg);
                    pending = int.MinValue;
                }
                else if (op != 0x28 && op != 0x7E) pending = int.MinValue; // anything else breaks the pair
            }
        }
    }

    // Every `stsfld NativeFieldInfoPtr_*`, with the il2cpp field NAME the cctor looked it up by. That
    // name is the game's real one, obfuscation and all -- which is exactly why it stops resolving when
    // VRChat re-randomises it, and why the mod needs to know its POSITION instead.
    private static IEnumerable<(string name, string field, int fieldTok)> NameFields(PEReader pe, MetadataReader md, TypeDefinition td)
    {
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (md.GetString(m.Name) != ".cctor" || m.RelativeVirtualAddress == 0) continue;
            byte[] il;
            try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
            string pending = null;
            foreach (var (op, ext, arg) in Walk(il))
            {
                if (ext) continue;
                if (op == 0x72)                                            // ldstr
                {
                    try { pending = md.GetUserString(MetadataTokens.UserStringHandle(arg)); } catch { pending = null; }
                    continue;
                }
                if (op == 0x80 && pending != null)                         // stsfld
                {
                    string fn = FieldName(md, arg);
                    if (fn != null && fn.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
                        yield return (pending, fn, arg);
                    pending = null;
                }
                else if (op != 0x28 && op != 0x7E) pending = null;
            }
        }
    }

    // the stores go through a MemberRef when the field sits on a nested generic store type
    private static string FieldName(MetadataReader md, int token)
    {
        try
        {
            var fh = MetadataTokens.EntityHandle(token);
            if (fh.Kind == HandleKind.FieldDefinition) return md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)fh).Name);
            if (fh.Kind == HandleKind.MemberReference) return md.GetString(md.GetMemberReference((MemberReferenceHandle)fh).Name);
        }
        catch { }
        return null;
    }

    // ---- member shapes ---------------------------------------------------------------------------
    //
    // A generated proxy method loads its own NativeMethodInfoPtr field and nothing else's, so the method
    // that reads a given field IS the proxy for that il2cpp method -- and its managed signature mirrors
    // the il2cpp one. Same for a field: the property that reads NativeFieldInfoPtr_X is X's accessor.
    private static Dictionary<int, MethodDefinitionHandle> Readers(PEReader pe, MetadataReader md, TypeDefinition td)
    {
        var map = new Dictionary<int, MethodDefinitionHandle>();
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (m.RelativeVirtualAddress == 0) continue;
            if (md.GetString(m.Name) == ".cctor") continue;
            byte[] il;
            try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
            foreach (var (op, ext, arg) in Walk(il))
            {
                if (ext || op != 0x7E) continue;                           // ldsfld
                if (!map.ContainsKey(arg)) map[arg] = mh;
            }
        }
        return map;
    }

    // "<return><parameters>", one character each, with the return upper-cased for a static method.
    // '?' when the proxy could not be identified -- the mod treats that position as unknown rather
    // than pretending to know it.
    private static string Shape(MetadataReader md, Dictionary<int, MethodDefinitionHandle> readers, int fieldTok)
    {
        if (readers == null || !readers.TryGetValue(fieldTok, out var mh)) return "?";
        try
        {
            var m = md.GetMethodDefinition(mh);
            var sig = m.DecodeSignature(new CatProv(), null);
            var sb = new System.Text.StringBuilder(2 + sig.ParameterTypes.Length);
            if ((m.Attributes & System.Reflection.MethodAttributes.Static) != 0) sb.Append('@');
            sb.Append(sig.ReturnType);
            foreach (char c in sig.ParameterTypes) sb.Append(c);

            // AND THE TYPE NAMES, WHERE THEY STILL MEAN SOMETHING ON THE NEW BUILD.
            //
            // Kinds alone are not always enough to single a method out: VRCNetworkingClient has several
            // methods that take one reference and return nothing, so OnEvent(EventData) could not be
            // told from its neighbours -- and a hook that cannot be placed exactly is a hook that must be
            // refused, which cost the Photon guard, the network log and the voice mimic all at once.
            // A parameter typed with a name VRChat did NOT obfuscate (Photon's EventData, Unity's
            // Renderer) keeps that name on both builds, so recording it makes the shape a near-identity.
            // Obfuscated and invented names are left out: they name nothing on the live side.
            var names = m.DecodeSignature(new NameProv(), null);
            var tail = new System.Text.StringBuilder();
            bool any = false;
            AppendName(tail, names.ReturnType, ref any);
            foreach (string n in names.ParameterTypes) AppendName(tail, n, ref any);
            if (any) { sb.Append('|'); sb.Append(tail); }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static void AppendName(System.Text.StringBuilder sb, string n, ref bool any)
    {
        if (sb.Length > 0) sb.Append(';');
        if (n == null) return;
        sb.Append(n);
        any = true;
    }

    // The field's own kind, prefixed with '@' when it is static.
    private static string FieldShape(MetadataReader md, Dictionary<int, MethodDefinitionHandle> readers, int fieldTok)
    {
        if (readers == null || !readers.TryGetValue(fieldTok, out var mh)) return "?";
        try
        {
            var m = md.GetMethodDefinition(mh);
            var sig = m.DecodeSignature(new CatProv(), null);
            // the accessor is either `X get_X()` or `void set_X(X)`
            char c = sig.ReturnType == 'v' && sig.ParameterTypes.Length > 0
                ? sig.ParameterTypes[sig.ParameterTypes.Length - 1]
                : sig.ReturnType;
            return (m.Attributes & System.Reflection.MethodAttributes.Static) != 0 ? "@" + c : c.ToString();
        }
        catch { return "?"; }
    }

    // IL2CPPINTEROP TURNS A VALUE TYPE INTO A CLASS, AND THE SHAPES HAVE TO KNOW.
    //
    // A proxy for one of VRChat's own structs is generated as a managed CLASS deriving from
    // Il2CppSystem.ValueType -- not as a struct. Read straight off the signature, every such parameter
    // therefore looks like a reference while il2cpp calls it a value type, and the two sequences stop
    // agreeing precisely on the classes that matter: LoadBalancingClient matched 63 of its 139 methods,
    // UIMenu 29 of 74, and both were refused as "not the same class" when they were exactly the right
    // one. Collected once over the whole interop, by base type, so a parameter can be classified as what
    // il2cpp will actually report.
    private static readonly HashSet<string> ValueProxies = new HashSet<string>(StringComparer.Ordinal);

    private static void CollectValueProxies(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir, "*.dll"))
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
                    string bn = null;
                    try
                    {
                        var bt = td.BaseType;
                        if (bt.IsNil) continue;
                        if (bt.Kind == HandleKind.TypeReference) bn = md.GetString(md.GetTypeReference((TypeReferenceHandle)bt).Name);
                        else if (bt.Kind == HandleKind.TypeDefinition) bn = md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)bt).Name);
                    }
                    catch { continue; }
                    if (bn != "ValueType" && bn != "Enum") continue;
                    if ((td.Attributes & System.Reflection.TypeAttributes.ClassSemanticsMask) != 0) continue;
                    // QUALIFIED ONLY. Indexing the bare name as well let one obscure value type poison
                    // every homonym in the interop: something called Material made UnityEngine.Material
                    // read as a value type, so every method taking one recorded a kind il2cpp disagrees
                    // with and stopped matching. A nested type has no namespace of its own and is keyed
                    // by its declaring type instead, which is still unambiguous.
                    string ns = md.GetString(td.Namespace), cls = md.GetString(td.Name);
                    if (td.IsNested)
                    {
                        try
                        {
                            var dt = md.GetTypeDefinition(td.GetDeclaringType());
                            ns = md.GetString(dt.Namespace);
                            cls = md.GetString(dt.Name) + "/" + cls;
                        }
                        catch { }
                    }
                    ValueProxies.Add(ns.Length > 0 ? ns + "." + cls : cls);
                }
            }
            catch { }
        }
        Console.WriteLine($"proxies de types valeur : {ValueProxies.Count} noms");
    }

    private static int Table(string dir, string outPath)
    {
        CollectValueProxies(dir);
        int rows = 0, types = 0, shaped = 0, gaps = 0, holes = 0;
        using var w = new StreamWriter(outPath);
        foreach (var f in Directory.EnumerateFiles(dir, "*.dll").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                using var s = File.OpenRead(f);
                using var pe = new PEReader(s);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var th in md.TypeDefinitions)
                {
                  // PER TYPE, NOT PER ASSEMBLY. One type whose body the walker cannot follow used to
                  // abort the whole file: three assemblies dropped out that way and took six thousand
                  // anchors with them, silently, because the message named only the DLL.
                  try
                  {
                    var td = md.GetTypeDefinition(th);
                    var fields = TokenFields(pe, md, td).ToList();
                    if (fields.Count == 0) continue;
                    types++;
                    string ns = md.GetString(td.Namespace), cls = md.GetString(td.Name), decl = "";
                    if (td.IsNested)
                    {
                        var dt = md.GetTypeDefinition(td.GetDeclaringType());
                        decl = md.GetString(dt.Name);
                        if (ns.Length == 0) ns = md.GetString(dt.Namespace);
                    }

                    // managed proxies, grouped by name; a ctor taking a lone IntPtr is Il2CppInterop's
                    // own wrapper constructor, not an il2cpp method, and is left out of the count
                    var byName = new Dictionary<string, List<(int pc, bool st)>>(StringComparer.Ordinal);
                    foreach (var mh in td.GetMethods())
                    {
                        var m = md.GetMethodDefinition(mh);
                        string n = md.GetString(m.Name);
                        if (!PlainName(n)) continue;
                        var sig = m.DecodeSignature(new SigProv(), null);
                        if (n == ".ctor" && sig.ParameterTypes.Length == 1 && sig.ParameterTypes[0] == "IntPtr") continue;
                        if (!byName.TryGetValue(n, out var l)) byName[n] = l = new List<(int, bool)>();
                        l.Add((sig.ParameterTypes.Length, (m.Attributes & System.Reflection.MethodAttributes.Static) != 0));
                    }
                    foreach (var kv in byName)
                    {
                        if (kv.Value.Count != 1) continue;
                        string prefix = "NativeMethodInfoPtr_" + (kv.Key == ".ctor" ? "_ctor" : kv.Key) + "_";
                        var hits = fields.Where(x => x.field.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                        if (hits.Count != 1) continue;
                        w.WriteLine($"{ns}\t{cls}\t{decl}\t{hits[0].token:X8}\t{kv.Key}\t{kv.Value[0].pc}\t{(kv.Value[0].st ? 1 : 0)}");
                        rows++;
                    }

                    // POSITIONAL FALLBACK, for the types the rule above cannot anchor at all.
                    //
                    // A name only anchors when it is plain text AND unique in its type. That misses two
                    // whole families: classes where every method is an overload of one name
                    // (UnityWebRequestAssetBundle is nothing but GetAssetBundle overloads, which is why
                    // the mod's asset-bundle hook could not be installed), and VRChat's fully obfuscated
                    // classes, where no name is readable at all.
                    //
                    // Both are still recoverable. A type whose method SET did not change between builds
                    // has the same methods in the same order, so its tokens moved as a block: the lowest
                    // token of the live class minus the lowest recorded here IS the shift. Recording the
                    // count alongside makes that check honest -- a different count means the type really
                    // did change, and the shift is then refused rather than guessed.
                    // The 8th column is the type's own TypeDef token. VRChat renames its obfuscated
                    // CLASSES between builds too (VRC.Player, VRCNetworkingClient, PortalInternal all
                    // vanish under their 1886 names), and a name lookup then finds nothing at all. But
                    // class tokens move in blocks exactly like method tokens do, so recording this lets
                    // the mod recover a renamed class from the shift its plain-named neighbours reveal.
                    var nameFields = NameFields(pe, md, td).ToList();
                    int fieldCount = 0;
                    foreach (var fh2 in td.GetFields())
                        if (md.GetString(md.GetFieldDefinition(fh2).Name).StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)) fieldCount++;
                    // Column 9 is the base class's simple name. Shape alone is not enough to name a
                    // renamed class -- VRChat's types also GAIN and LOSE members across an update, so
                    // the counts only ever come close -- and the parent is the discriminator that makes
                    // the nearest match trustworthy.
                    string baseName = "";
                    try
                    {
                        var bt = td.BaseType;
                        if (!bt.IsNil)
                        {
                            if (bt.Kind == HandleKind.TypeReference) baseName = md.GetString(md.GetTypeReference((TypeReferenceHandle)bt).Name);
                            else if (bt.Kind == HandleKind.TypeDefinition) baseName = md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)bt).Name);
                        }
                    }
                    catch { }

                    // THE SHAPE OF EVERY MEMBER, IN ORDER (columns 12 and 13).
                    //
                    // Recovering a renamed class gives back its identity, not its members: the 1886 names
                    // and tokens of those still mean nothing on the new build, so every call into it
                    // stays dead and every field read logs "was not found". But il2cpp lists a class's
                    // members in the order the metadata declares them, and renaming changes neither that
                    // order nor a member's SHAPE -- its arity, its staticness, the kind of each of its
                    // parameters. Recording the sequence lets the mod line the two lists up member by
                    // member and rebuild the whole binding table for the class.
                    //
                    // The methods only line up if their tokens are the contiguous run the class's own
                    // metadata block is. When they are not, the column is left empty rather than risk
                    // pairing a token with the wrong member -- which would be an access violation, not a
                    // wrong answer.
                    // A HOLE IS NOT A REASON TO GIVE UP ON THE WHOLE TYPE.
                    //
                    // The shapes are read back by INDEX, as minToken + i, so the first version demanded a
                    // perfectly contiguous run and dropped every type that was missing one token -- 976 of
                    // them, PortalInternal among them, which is why portal glow had nothing to mark. Its
                    // 79 methods span 80 tokens with a single gap, and all that is needed is to leave that
                    // one position EMPTY so every later member still lands on its own token. A span wildly
                    // larger than the member count is a different animal and is still skipped.
                    int minTok = fields.Min(x => x.token);
                    int maxTok = fields.Max(x => x.token);
                    int span = maxTok - minTok + 1;
                    bool usable = span > 0 && span <= fields.Count * 2 + 16;
                    var readers = usable || nameFields.Count > 0 ? Readers(pe, md, td) : null;
                    string mShapes = "";
                    if (usable)
                    {
                        var slot = new string[span];
                        foreach (var f2 in fields) slot[f2.token - minTok] = Shape(md, readers, f2.fieldTok);
                        for (int i = 0; i < span; i++) if (slot[i] == null) slot[i] = "";
                        mShapes = string.Join(",", slot);
                        shaped++;
                        if (span != fields.Count) holes++;
                    }
                    else gaps++;
                    // Fields are bound by NAME, so the recorded sequence carries the name the interop
                    // will ask for next to the kind that identifies its position.
                    string fShapes = nameFields.Count == 0 ? ""
                        : string.Join(",", nameFields.Select(x => FieldShape(md, readers, x.fieldTok) + x.name));

                    string posRow = $"\t{decl}\t{minTok:X8}\t*POS*\t{fields.Count}\t{fieldCount}\t{MetadataTokens.GetToken(th):X8}\t{baseName}\t\t\t{mShapes}\t{fShapes}";
                    w.WriteLine(ns + "\t" + cls + posRow);
                    rows++;

                    // AND UNDER THE NAME IL2CPP ACTUALLY USES.
                    //
                    // For a type VRChat obfuscated, Il2CppInterop invents a readable managed name but
                    // keeps the real one in [ObfuscatedName], and that is the name it passes to
                    // GetIl2CppClass at runtime. Indexing only the invented name meant the lookup and
                    // the table never met: every obfuscated type -- LoadBalancingClient,
                    // VRCNetworkingClient and the whole Photon stack among them -- came back "no shape
                    // recorded" and could not be recovered at all.
                    string obf = ObfuscatedName(md, td);
                    if (obf != null && obf != cls)
                    {
                        // The attribute carries the original name, sometimes namespace-qualified.
                        int dot = obf.LastIndexOf('.');
                        string oNs = dot > 0 ? obf.Substring(0, dot) : ns;
                        string oCls = dot > 0 ? obf.Substring(dot + 1) : obf;
                        w.WriteLine(oNs + "\t" + oCls + posRow);
                        if (oNs != "") w.WriteLine("\t" + oCls + posRow);
                        rows++;
                    }
                  }
                  catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(f)} type#{MetadataTokens.GetToken(th):X8} : {e.Message}"); }
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(f)} : {e.Message}"); }
        }
        Console.WriteLine($"types avec tokens : {types}   ancres : {rows}   formes de membres : {shaped} (dont {holes} a trous) (+{gaps} types au bloc de tokens trop eclate)   -> {outPath}");
        return 0;
    }

    // The value of Il2CppInterop's [ObfuscatedName("...")] on a type, if it carries one: the name the
    // game's metadata really uses, which is what GetIl2CppClass is called with at runtime.
    private static string ObfuscatedName(MetadataReader md, TypeDefinition td)
    {
        foreach (var ah in td.GetCustomAttributes())
        {
            try
            {
                var a = md.GetCustomAttribute(ah);
                string attrName = null;
                if (a.Constructor.Kind == HandleKind.MemberReference)
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                        attrName = md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
                }
                if (attrName != "ObfuscatedNameAttribute") continue;

                // CustomAttribute blob: prolog 0x0001, then a SerString (compressed length + UTF-8).
                var blob = md.GetBlobReader(a.Value);
                if (blob.ReadUInt16() != 1) continue;
                return blob.ReadSerializedString();
            }
            catch { }
        }
        return null;
    }

    private static bool PlainName(string n)
    {
        if (n.Length == 0 || n.StartsWith("Method_") || n.StartsWith("get_prop_") || n.StartsWith("set_prop_")) return false;
        if (n[0] == '.' && n != ".ctor") return false;
        foreach (char c in n) if (c > 126 || c < 32) return false;
        return true;
    }

    // Enough of a signature decoder to count parameters and spot the IntPtr wrapper ctor.
    private sealed class SigProv : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string e, ArrayShape s) => e + "[]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetFunctionPointerType(MethodSignature<string> si) => "fnptr";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericMethodParameter(object g, int i) => "!!" + i;
        public string GetGenericTypeParameter(object g, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
        public string GetPointerType(string e) => e + "*";
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte k) => "spec";
    }

    // The simple name of a slot's type, but ONLY when that name survives the obfuscator: plain ASCII,
    // and not one of the readable names Il2CppInterop INVENTS for a type VRChat renamed (they all end
    // in "Unique" and exist nowhere in the live metadata). Everything else -- primitives, arrays,
    // generics, pointers -- returns null and contributes nothing.
    private sealed class NameProv : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string e, ArrayShape s) => null;
        // A by-reference slot is deliberately left unnamed: the live side would have to unwrap the
        // element type to say anything, and the two sides must agree on silence rather than guess.
        public string GetByReferenceType(string e) => null;
        public string GetFunctionPointerType(MethodSignature<string> si) => null;
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => null;
        public string GetGenericMethodParameter(object g, int i) => null;
        public string GetGenericTypeParameter(object g, int i) => null;
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
        public string GetPointerType(string e) => null;
        // il2cpp files System.Object and System.String under kinds of their own rather than CLASS, and
        // names them "Object" and "String". A proxy signature spells them Il2CppSystem.Object /
        // Il2CppSystem.String, which lands here as a TypeReference and is already named -- but a plain
        // `object` or `string` in the same signature arrives as a primitive, and leaving THAT unnamed
        // made the two sides disagree about the very same slot.
        public string GetPrimitiveType(PrimitiveTypeCode c)
            => c == PrimitiveTypeCode.Object ? "Object" : c == PrimitiveTypeCode.String ? "String" : null;
        public string GetSZArrayType(string e) => null;
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Keep(r.GetString(r.GetTypeDefinition(h).Name));
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Keep(r.GetString(r.GetTypeReference(h).Name));
        public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte k) => null;

        private static string Keep(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length > 48) return null;
            if (n.EndsWith("Unique", StringComparison.Ordinal)) return null;
            foreach (char c in n) if (c > 126 || c < 32 || c == '|' || c == ';' || c == ',') return null;
            return n;
        }
    }

    // EXACTLY THE KINDS IL2CPP ITSELF DISTINGUISHES, one character each, so a recorded shape and a
    // live one are directly comparable.
    //
    // The first version collapsed every integer into one symbol, and that turned out to be the flaw
    // that mattered: with bool, byte and int sharing a letter, "this shape occurs exactly once in the
    // class" stopped being true, and Photon's EventData.get_Sender was paired with an unrelated method
    // of the same coarse shape. It returned a number, the guard believed it, and the mod started
    // dropping real network traffic on invented actor ids. The widths are free to record and they are
    // what makes a pairing a fact instead of a guess.
    //
    //   v void   b bool   c char   1..8 the integer widths   n native int   f float   d double
    //   o reference   s value type   p byref/pointer   g generic parameter   ? unknown
    // A leading '@' marks a static member; it is a prefix rather than a case change so that every kind
    // can have its own letter.
    private sealed class CatProv : ISignatureTypeProvider<char, object>
    {
        public char GetArrayType(char e, ArrayShape s) => 'o';
        public char GetByReferenceType(char e) => 'p';
        public char GetFunctionPointerType(MethodSignature<char> si) => 'p';
        // A generic instantiation counts as a reference on BOTH sides. Asking il2cpp at runtime whether
        // one is a value type means calling il2cpp_class_is_valuetype, which on this build goes through
        // VRChat's patched struct wrapper and ends the process -- so the live reader cannot answer, and
        // the recorded shape must not pretend to either.
        public char GetGenericInstantiation(char g, ImmutableArray<char> a) => 'o';
        public char GetGenericMethodParameter(object g, int i) => 'g';
        public char GetGenericTypeParameter(object g, int i) => 'g';
        public char GetModifiedType(char m, char u, bool r) => u;
        public char GetPinnedType(char e) => e;
        public char GetPointerType(char e) => 'p';
        public char GetPrimitiveType(PrimitiveTypeCode c)
        {
            switch (c)
            {
                case PrimitiveTypeCode.Void: return 'v';
                case PrimitiveTypeCode.Boolean: return 'b';
                case PrimitiveTypeCode.Char: return 'c';
                case PrimitiveTypeCode.SByte: return '1';
                case PrimitiveTypeCode.Byte: return '2';
                case PrimitiveTypeCode.Int16: return '3';
                case PrimitiveTypeCode.UInt16: return '4';
                case PrimitiveTypeCode.Int32: return '5';
                case PrimitiveTypeCode.UInt32: return '6';
                case PrimitiveTypeCode.Int64: return '7';
                case PrimitiveTypeCode.UInt64: return '8';
                case PrimitiveTypeCode.Single: return 'f';
                case PrimitiveTypeCode.Double: return 'd';
                case PrimitiveTypeCode.IntPtr:
                case PrimitiveTypeCode.UIntPtr: return 'n';
                case PrimitiveTypeCode.String:
                case PrimitiveTypeCode.Object: return 'o';
                case PrimitiveTypeCode.TypedReference: return 's';
                default: return '?';
            }
        }
        public char GetSZArrayType(char e) => 'o';
        // 0x11 is ELEMENT_TYPE_VALUETYPE; anything else here is a class -- unless it is one of the
        // classes Il2CppInterop generated to stand in for an il2cpp value type, which il2cpp itself
        // still reports as one.
        public char GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
        {
            if (k == 0x11) return 's';
            var td = r.GetTypeDefinition(h);
            return Known(r.GetString(td.Namespace), r.GetString(td.Name));
        }
        public char GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
        {
            if (k == 0x11) return 's';
            var tr = r.GetTypeReference(h);
            return Known(r.GetString(tr.Namespace), r.GetString(tr.Name));
        }
        public char GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte k) => 'o';
        private static char Known(string ns, string name)
        {
            return ValueProxies.Contains(ns.Length > 0 ? ns + "." + name : name) ? 's' : 'o';
        }
    }
}
