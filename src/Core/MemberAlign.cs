using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace VRChatArchiveMod.Core
{
	// GIVE A RENAMED CLASS ITS MEMBERS BACK.
	//
	// Recovering a class by its shape (ObfuscatedClassFinder) restores its IDENTITY and nothing else.
	// The interop still holds 1886 tokens for its methods and 1886 names for its fields, and on this
	// build both are meaningless: the token belongs to some unrelated method now, and the name does not
	// exist at all. So every member of a recovered class had to be neutralised -- casts worked, calls did
	// not. That is why HighlightsFX could be found and still not glow, why VRC.Player's fields read as
	// nothing, and where the several hundred "Field ... was not found" lines came from.
	//
	// But obfuscation only renames. It does not reorder a class's members, and it cannot change what they
	// ARE: a method keeps its arity, its staticness and the kind of each parameter; a field keeps its
	// kind. tokmap records that sequence for every type of the 1886 interop, one short string per member.
	// Read the same sequence off the live class and the two line up member by member -- an ordinary
	// longest-common-subsequence, which also absorbs the members an update added or removed -- and every
	// recorded token and name gets a live target again.
	//
	// A pairing is only ever made between members of the SAME shape. That is what keeps it safe: even a
	// mis-paired member is called with the right number of arguments of the right kinds, so the worst
	// case is a wrong answer, never the corrupted stack that a blind token guess produced.
	internal static unsafe class MemberAlign
	{
		private const ushort METHOD_STATIC = 0x0010;
		private const int FIELD_STATIC = 0x0010, FIELD_LITERAL = 0x0040;

		internal sealed class Aligned
		{
			public Dictionary<int, IntPtr> Methods;     // 1886 token -> live method
			public Dictionary<string, IntPtr> Fields;   // 1886 il2cpp name -> live field
			public int MethodHits, MethodTotal, FieldHits, FieldTotal;

			// PLACING A MEMBER AND TRUSTING A CLASS ARE NOT THE SAME QUESTION.
			//
			// Every pairing below is exact, so any single one of them is safe to resolve. But a module
			// that reflects over a class calls whatever member it likes, and a member NOT paired still
			// falls through to the neutral stub -- which ended the process the moment FewTags read
			// VRC.Player.APIUser. So the class only counts as callable once nearly all of it is placed.
			public bool Trustworthy;

			// The recorded shapes themselves, kept so that EVERY token translation can be checked
			// against the member it claims to be -- see ShapeOk.
			public string[] Want;
			public int MinTok;
		}

		private static readonly Dictionary<IntPtr, Aligned> _cache = new Dictionary<IntPtr, Aligned>();
		// Printing both shape sequences is how every mismatch so far was diagnosed, and it is far too
		// long to leave on. Set VA_DUMP_SHAPES to a class's recorded METHOD COUNT and the two lines for
		// that one class are written out; the count is the only handle an obfuscated class offers.
		private static readonly int _dumpShapes = int.TryParse(Environment.GetEnvironmentVariable("VA_DUMP_SHAPES"), out int _ds) ? _ds : 0;
		private static bool _usable = true, _armed;
		private static int _placedM, _placedF, _permuted, _refusedClasses, _missField, _quiet, _detailed, _mismatch, _named, _byName, _dumpedPermuted;

		internal static bool Armed => _armed;
		internal static string Summary =>
			$"{_placedM} methodes et {_placedF} champs replaces par leur forme ({_permuted} classes aux membres permutes, {_named} membres identifies par le nom de leurs types, {_byName} par leur propre nom), {_refusedClasses} classes refusees, {_mismatch} traductions refusees par la forme, {_stripped} methodes sans code ecartees, {_missField} champs introuvables";

		// ---- reading the live side --------------------------------------------------------------

		// EXACTLY THE KINDS IL2CPP ITSELF DISTINGUISHES, one character each, matching what tokmap wrote.
		//
		// The first version collapsed every integer width into one symbol, and that is the flaw that
		// mattered: with bool, byte and int sharing a letter, "this shape occurs exactly once in the
		// class" stopped being true, and Photon's EventData.get_Sender was paired with an unrelated
		// method of the same coarse shape. It returned a number, PhotonGuard believed it, and the mod
		// began dropping real network traffic on invented actor ids until the game's own Photon state
		// gave way. The widths cost nothing to record and they are what makes a pairing a fact.
		//
		//   v void   b bool   c char   1..8 the integer widths   n native int   f float   d double
		//   o reference   s value type   p byref/pointer   g generic parameter   ? unknown
		private static char Cat(IntPtr type)
		{
			if (type == IntPtr.Zero) return '?';
			try
			{
				var tw = UnityVersionHandler.Wrap((Il2CppTypeStruct*)type);
				if (tw.ByRef) return 'p';
				switch ((int)tw.Type)
				{
					case 0x01: return 'v';
					case 0x02: return 'b';
					case 0x03: return 'c';
					case 0x04: return '1';
					case 0x05: return '2';
					case 0x06: return '3';
					case 0x07: return '4';
					case 0x08: return '5';
					case 0x09: return '6';
					case 0x0a: return '7';
					case 0x0b: return '8';
					case 0x0c: return 'f';
					case 0x0d: return 'd';
					case 0x18: case 0x19: return 'n';
					case 0x0e: case 0x12: case 0x14: case 0x1c: case 0x1d: return 'o';
					case 0x0f: case 0x10: case 0x1b: return 'p';
					case 0x11: case 0x16: return 's';
					case 0x13: case 0x1e: return 'g';
					// A generic instantiation counts as a reference on both sides: asking il2cpp whether
					// one is a value type means il2cpp_class_is_valuetype, which goes through VRChat's
					// patched struct wrapper here and ends the process.
					case 0x15: return 'o';
					default: return '?';
				}
			}
			catch { return '?'; }
		}

		// The simple name of a slot's type, but only for the two kinds tokmap also names: a plain class
		// or a plain value type. Strings, objects, arrays and generics stay unnamed on both sides, so the
		// two agree on silence instead of each guessing something different.
		private static string SlotName(IntPtr type)
		{
			if (type == IntPtr.Zero) return null;
			try
			{
				int kind = (int)UnityVersionHandler.Wrap((Il2CppTypeStruct*)type).Type;
				// il2cpp gives System.Object and System.String kinds of their own rather than filing them
				// under CLASS, but the interop's proxy signature names them like any other type -- so a
				// parameter typed Il2CppSystem.Object was recorded as "Object" and named nothing here.
				// That one gap is what left LoadBalancingClient.RaiseEvent unidentifiable, and with it the
				// outbound network log and the voice mimic.
				if (kind == 0x1c) return "Object";
				if (kind == 0x0e) return "String";
				if (kind != 0x11 && kind != 0x12) return null;
				IntPtr k = IL2CPP.il2cpp_class_from_type(type);
				if (k == IntPtr.Zero || !NativeGuard.IsReadable(k, 16)) return null;
				string n = Str(UnityVersionHandler.Wrap((Il2CppClass*)k).Name);
				if (string.IsNullOrEmpty(n) || n.Length > 48) return null;
				foreach (char c in n) if (c > 126 || c < 32 || c == '|' || c == ';' || c == ',') return null;
				return n;
			}
			catch { return null; }
		}

		// "<kinds>" alone, or "<kinds>|<name>;<name>;..." when at least one slot has a name that means the
		// same thing on both builds. Kinds alone could not tell OnEvent(EventData) from its neighbours in
		// VRCNetworkingClient, and a member that cannot be singled out exactly is one no hook may be placed
		// on -- which is what silenced the Photon guard, the network log and the voice mimic at once.
		private static string MethodShape(IntPtr m)
		{
			try
			{
				var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m);
				int n = mw.ParametersCount;
				if (n < 0 || n > 64) return "?";
				var kinds = new System.Text.StringBuilder(n + 2);
				// '@' rather than a case change, so that every kind can have its own letter.
				if (((ushort)mw.Flags & METHOD_STATIC) != 0) kinds.Append('@');
				IntPtr rt = IL2CPP.il2cpp_method_get_return_type(m);
				kinds.Append(Cat(rt));

				var names = new System.Text.StringBuilder();
				bool any = false;
				Append(names, SlotName(rt), ref any);
				for (int i = 0; i < n; i++)
				{
					IntPtr pt = IL2CPP.il2cpp_method_get_param(m, (uint)i);
					kinds.Append(Cat(pt));
					Append(names, SlotName(pt), ref any);
				}
				return any ? kinds.ToString() + "|" + names : kinds.ToString();
			}
			catch { return "?"; }
		}

		private static void Append(System.Text.StringBuilder sb, string n, ref bool any)
		{
			if (sb.Length > 0) sb.Append(';');
			if (n == null) return;
			sb.Append(n);
			any = true;
		}

		// The kinds without the names: what a translation is verified against, and what the class
		// fingerprint compares, because a live name may be obfuscated where the recorded one is not.
		private static string Base(string shape)
		{
			int i = shape.IndexOf('|');
			return i < 0 ? shape : shape.Substring(0, i);
		}

		private static string[] Bases(IList<string> v)
		{
			var r = new string[v.Count];
			for (int i = 0; i < v.Count; i++) r[i] = Base(v[i]);
			return r;
		}

		internal static List<string> LiveMethodShapes(IntPtr clazz, List<IntPtr> methods)
		{
			var shapes = new List<string>();
			IntPtr iter = IntPtr.Zero, m;
			int guard = 0;
			while ((m = IL2CPP.il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero && guard++ < 4096)
			{
				methods?.Add(m);
				shapes.Add(MethodShape(m));
			}
			return shapes;
		}

		// THE FIELD TABLE, CALIBRATED RATHER THAN ASSUMED.
		//
		// Three readings of a field's type were tried and all three came back as kind '?' for every field
		// of every class: il2cpp_field_get_type and il2cpp_field_get_flags are mis-bound on this build's
		// scrambled exports, and walking the class struct's own field array off a guessed stride simply
		// dereferenced garbage -- that one did not fail quietly, it took the process down inside
		// Marshal.PtrToStringAnsi. VRChat also ships patched il2cpp structs, so no offset here can be taken
		// on faith.
		//
		// So the layout is MEASURED once, against a class whose fields are known, and every read that could
		// touch an unmapped page goes through NativeGuard first. A FieldInfo is identified by the only two
		// things that can be recognised without knowing the layout: a slot holding readable text (the name)
		// and a slot holding an Il2CppType whose kind byte is one of the twenty-odd il2cpp defines.
		private static int _nameOff = -1, _typeOff = -1;
		private static int _calibCount, _calibNamed, _structCount;

		private static string SafeStr(IntPtr p, int max = 192)
		{
			if (p == IntPtr.Zero || !NativeGuard.IsReadable(p, max, false)) return null;
			try
			{
				byte* b = (byte*)p;
				int n = 0;
				while (n < max && b[n] != 0) n++;
				if (n == 0 || n >= max) return null;
				for (int i = 0; i < n; i++) if (b[i] < 32 || b[i] > 126) return null;
				return System.Text.Encoding.ASCII.GetString(b, n);
			}
			catch { return null; }
		}

		private static IntPtr Slot(IntPtr basePtr, int off)
		{
			if (!NativeGuard.IsReadable(basePtr + off, IntPtr.Size, false)) return IntPtr.Zero;
			try { return *(IntPtr*)((byte*)basePtr + off); } catch { return IntPtr.Zero; }
		}

		// Does this pointer really address an Il2CppType? Its kind byte is a small enum, and a pointer or
		// a piece of text would almost never land inside that range with a sane attribute word.
		private static bool LooksLikeType(IntPtr t)
		{
			if (t == IntPtr.Zero || !NativeGuard.IsReadable(t, 16, false)) return false;
			try
			{
				int k = (int)UnityVersionHandler.Wrap((Il2CppTypeStruct*)t).Type;
				return k >= 0x01 && k <= 0x1e;
			}
			catch { return false; }
		}

		private static bool _dumped;
		private static int _stride = -1;

		// WHAT il2cpp_class_get_fields ACTUALLY RETURNS HERE IS THE PROPERTY LIST.
		//
		// It handed back 74 entries for a class whose own struct says it has 7 fields, and the first one
		// dumped as ... +24:0x17000052 ... -- metadata table 0x17, which is Property. So that export is
		// bound to the property iterator on this build, and every "field" read through it was a
		// PropertyInfo. The class struct's own field array is the real one; its layout is measured here
		// rather than assumed, because VRChat ships patched il2cpp structs.
		private static bool Layout(IntPtr table, int count)
		{
			if (_nameOff >= 0) return true;
			// The dump that settled it: +0 token 0x040001B4, +8 name, +16 zero, +24 parent class,
			// +32 the Il2CppType (an address inside GameAssembly, where the type table lives), and the
			// next entry's token at +40. So the stride is 40 and the type sits past where the first
			// probe stopped looking -- which is exactly why it found nothing and dumped instead.
			foreach (int stride in new[] { 40, 32, 24, 48, 16 })
				for (int no = 0; no <= 40; no += 8)
					for (int to = 0; to <= 40; to += 8)
					{
						if (to == no) continue;
						bool ok = true;
						for (int i = 0; i < count && i < 4; i++)
						{
							IntPtr f = (IntPtr)((byte*)table + i * stride);
							if (SafeStr(Slot(f, no)) == null || !LooksLikeType(Slot(f, to))) { ok = false; break; }
						}
						if (!ok) continue;
						_stride = stride; _nameOff = no; _typeOff = to;
						VRChatArchiveModPlugin.Logger.LogInfo($"[MemberAlign] disposition d'un champ mesuree : pas {stride} o, nom +{no}, type +{to}.");
						return true;
					}
			if (!_dumped)
			{
				_dumped = true;
				try
				{
					if (!NativeGuard.IsReadable(table, 64, false)) { VRChatArchiveModPlugin.Logger.LogWarning($"[MemberAlign] tableau de champs 0x{table.ToString("X")} illisible."); }
					else
					{
						var sb = new System.Text.StringBuilder();
						byte* b = (byte*)table;
						for (int i = 0; i < 64; i += 8) sb.Append(" +" + i + ":0x" + (*(ulong*)(b + i)).ToString("X"));
						VRChatArchiveModPlugin.Logger.LogWarning($"[MemberAlign] tableau de champs 0x{table.ToString("X")} ({count} entrees) :{sb}");
					}
				}
				catch { }
			}
			return false;
		}

		// THE CORRECTED FIELD WALK, FOR ANYONE ELSE WHO NEEDS IT.
		//
		// il2cpp_class_get_fields is bound to the PROPERTY iterator on this build and
		// il2cpp_field_get_type gives back something that is not an Il2CppType at all -- so any module
		// walking a class's fields through the exports is reading nonsense, and feeding that to
		// il2cpp_class_from_type is an access violation. FewTags' nameplate search did exactly that.
		// CACHED PER CLASS. A class's field table never changes, but reading it validates every entry
		// with a VirtualQuery -- three syscalls per field. Walking a nameplate's components on every
		// pass therefore cost twenty thousand syscalls a second and put VaTags at 850 ms/s with the game
		// at 13 fps. Read once, kept for the process.
		private static readonly Dictionary<IntPtr, IntPtr[]> _fieldsByClass = new Dictionary<IntPtr, IntPtr[]>();
		private static readonly IntPtr[] _noFields = new IntPtr[0];

		internal static IReadOnlyList<IntPtr> LiveFields(IntPtr clazz)
		{
			if (clazz == IntPtr.Zero) return _noFields;
			lock (_fieldsByClass) { if (_fieldsByClass.TryGetValue(clazz, out var hit)) return hit; }
			var fields = new List<IntPtr>();
			try { LiveFieldShapes(clazz, fields); } catch { }
			var arr = fields.Count == 0 ? _noFields : fields.ToArray();
			lock (_fieldsByClass) { _fieldsByClass[clazz] = arr; }
			return arr;
		}

		// WHERE A CLASS KEEPS ITS OBJECT REFERENCES, precomputed once.
		//
		// A walker that wants the objects an instance points at does not need the field table again --
		// it needs a list of offsets. Recomputing that per instance meant a VirtualQuery per field per
		// pass; this is the same answer, read once per class and then free.
		private static readonly Dictionary<IntPtr, int[]> _refOffsets = new Dictionary<IntPtr, int[]>();
		private static readonly int[] _noOffsets = new int[0];

		internal static int[] ReferenceOffsets(IntPtr clazz)
		{
			if (clazz == IntPtr.Zero) return _noOffsets;
			lock (_refOffsets) { if (_refOffsets.TryGetValue(clazz, out var hit)) return hit; }
			var offs = new List<int>();
			try
			{
				foreach (IntPtr f in LiveFields(clazz))
				{
					IntPtr ft = FieldTypePtr(f);
					if (ft == IntPtr.Zero || !IsInstanceReference(ft)) continue;
					uint off;
					try { off = IL2CPP.il2cpp_field_get_offset(f); } catch { continue; }
					if (off < 8 || off > 0x4000) continue;
					offs.Add((int)off);
				}
			}
			catch { }
			var arr = offs.Count == 0 ? _noOffsets : offs.ToArray();
			lock (_refOffsets) { _refOffsets[clazz] = arr; }
			return arr;
		}

		// The field's Il2CppType, read at the offset measured on this build rather than through the
		// mis-bound export. Zero when the layout could not be measured.
		// The base class, read from the class struct rather than through il2cpp_class_get_parent --
		// which is one more of this build's mis-bound exports and takes the process down when handed a
		// class it does not like. ObfuscatedClassFinder has been reading it this way all along.
		// Does this field hold an OBJECT REFERENCE on the instance? Reading a struct field as a pointer
		// is meaningless and reading a static one off an instance is worse, so a walker that wants the
		// objects a component points at asks this first. The static bit lives in the low 16 of the type.
		internal static bool IsInstanceReference(IntPtr fieldType)
		{
			if (fieldType == IntPtr.Zero || !NativeGuard.IsReadable(fieldType, 16)) return false;
			try
			{
				var tw = UnityVersionHandler.Wrap((Il2CppTypeStruct*)fieldType);
				if ((tw.Attrs & FIELD_STATIC) != 0) return false;
				if (tw.ByRef) return false;
				int k = (int)tw.Type;
				// class, string, array, szarray, object, generic instance
				return k == 0x12 || k == 0x0e || k == 0x14 || k == 0x1d || k == 0x1c || k == 0x15;
			}
			catch { return false; }
		}

		internal static IntPtr ParentOf(IntPtr clazz)
		{
			if (clazz == IntPtr.Zero || !NativeGuard.IsReadable(clazz, 0x100)) return IntPtr.Zero;
			try
			{
				var p = UnityVersionHandler.Wrap((Il2CppClass*)clazz).Parent;
				IntPtr k = p == null ? IntPtr.Zero : (IntPtr)p;
				return k != IntPtr.Zero && NativeGuard.IsReadable(k, 0x100) ? k : IntPtr.Zero;
			}
			catch { return IntPtr.Zero; }
		}

		internal static IntPtr FieldTypePtr(IntPtr field)
		{
			if (field == IntPtr.Zero || _typeOff < 0) return IntPtr.Zero;
			return Slot(field, _typeOff);
		}

		internal static string LiveFieldName(IntPtr field)
		{
			return _nameOff >= 0 ? SafeStr(Slot(field, _nameOff)) : null;
		}

		private static List<string> LiveFieldShapes(IntPtr clazz, List<IntPtr> fields)
		{
			var shapes = new List<string>();
			IntPtr table; int count;
			// THE CLASS POINTER ITSELF HAS TO BE REAL. Callers hand this in from il2cpp_class_get_parent
			// and from component walks, and neither is guaranteed to end on zero -- wrapping a stray
			// value reads at a patched struct offset and takes the process with it.
			if (clazz == IntPtr.Zero || !NativeGuard.IsReadable(clazz, 0x100)) return shapes;
			try
			{
				var kw = UnityVersionHandler.Wrap((Il2CppClass*)clazz);
				table = (IntPtr)kw.Fields; count = kw.FieldCount;
			}
			catch { return shapes; }
			_calibCount = count; _structCount = count;
			if (table == IntPtr.Zero || count <= 0 || count > 4096) return shapes;
			if (!NativeGuard.IsReadable(table, 32, false)) return shapes;
			if (!Layout(table, count)) return shapes;

			_calibNamed = 0;
			for (int i = 0; i < count; i++)
			{
				IntPtr f = (IntPtr)((byte*)table + i * _stride);
				// The whole entry, not the first 32 bytes: the type slot sits at +32 on this build, so a
				// shorter check let the last read run past the end of the region it validated.
				if (!NativeGuard.IsReadable(f, _stride, false)) break;
				IntPtr ft = Slot(f, _typeOff);
				if (!LooksLikeType(ft)) continue;
				_calibNamed++;
				// A field's ATTRIBUTES live in the low 16 bits of its Il2CppType, not behind an export:
				// il2cpp_field_get_flags came back as nonsense here, and every field looked like a constant.
				int flags;
				try { flags = UnityVersionHandler.Wrap((Il2CppTypeStruct*)ft).Attrs; } catch { continue; }
				// CONSTANTS COUNT. Skipping them was tried, on the theory that Il2CppInterop emits a constant
				// as a managed literal and never binds it -- but Camera's recorded fields are kMinAperture,
				// kMaxAperture, kMinBladeCount and kMaxBladeCount, all four of them consts with a
				// NativeFieldInfoPtr of their own. Dropping them shifted every later field by a position.
				char c = Cat(ft);
				fields?.Add(f);
				shapes.Add((flags & FIELD_STATIC) != 0 ? "@" + c : c.ToString());
			}
			return shapes;
		}

		// ---- can this method actually be called? -------------------------------------------------
		//
		// "Resolved" and "callable" are different facts here. VRChat's il2cpp strips methods nothing
		// references and leaves their MethodInfo in place with a NULL entry point; il2cpp_runtime_invoke
		// jumps to it and the process is gone, with no managed exception to catch. That is what killed
		// Thread.MemoryBarrier when it was chosen as the neutral method, and Photon's EventData.get_Sender
		// on every single run -- the token translation was right, the method simply has no code.
		//
		// Reading MethodInfo.methodPointer means knowing where it sits, and VRChat ships patched il2cpp
		// structs (checking it through the interop's wrapper rejected even methods the mod calls every
		// frame). So the offset is MEASURED, against methods that are certainly compiled because this mod
		// invokes them continuously, and a slot only wins if it holds executable memory for all of them.
		private static int _mpOff = -2;

		private static readonly string[][] KnownGood =
		{
			new[] { "UnityEngine.Time", "NativeMethodInfoPtr_get_realtimeSinceStartup" },
			new[] { "UnityEngine.Object", "NativeMethodInfoPtr_get_name" },
			new[] { "UnityEngine.Transform", "NativeMethodInfoPtr_get_position" },
			new[] { "UnityEngine.GameObject", "NativeMethodInfoPtr_get_transform" },
		};

		private static IntPtr ProxyMethod(string typeName, string prefix)
		{
			Type t = typeName == "UnityEngine.Time" ? typeof(UnityEngine.Time)
				: typeName == "UnityEngine.Object" ? typeof(UnityEngine.Object)
				: typeName == "UnityEngine.Transform" ? typeof(UnityEngine.Transform)
				: typeName == "UnityEngine.GameObject" ? typeof(UnityEngine.GameObject) : null;
			if (t == null) return IntPtr.Zero;
			foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (f.FieldType != typeof(IntPtr) || !f.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
				try { return (IntPtr)f.GetValue(null); } catch { return IntPtr.Zero; }
			}
			return IntPtr.Zero;
		}

		private static void CalibrateMethodPointer()
		{
			if (_mpOff != -2) return;
			_mpOff = -1;

			// THE WITNESSES MUST NOT COME THROUGH THE THING BEING REPAIRED.
			//
			// This used to take a handful of methods resolved through the interop's own token lookup and
			// demand that ALL of them have executable code at the candidate offset. On a build where
			// that lookup is wrong -- which is the only reason any of this code exists -- the witnesses
			// point at arbitrary memory, no offset satisfies every one of them, and the measurement
			// fails. Callable then answers "yes" to everything, by design, and the entire
			// stripped-method defence silently switches off: that is how UnityEngine.Vector3's stripped
			// constructor was accepted, written into the proxy and invoked straight into an access
			// violation.
			// Live methods enumerated from a class il2cpp itself hands back owe nothing to the token
			// lookup. A real class has mostly-compiled methods, so the correct offset stands out by a
			// wide margin, and a clear majority replaces unanimity -- a few genuinely stripped members
			// no longer sink the whole measurement.
			var sample = new List<IntPtr>();
			foreach (Type t in new[] { typeof(Il2CppSystem.Object), typeof(Il2CppSystem.String), typeof(UnityEngine.Object) })
			{
				IntPtr k = IntPtr.Zero;
				try { k = Il2CppClassPointerStore.GetNativeClassPointer(t); } catch { }
				if (k == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(k)) continue;
				try
				{
					IntPtr iter = IntPtr.Zero, m;
					int guard = 0;
					while ((m = IL2CPP.il2cpp_class_get_methods(k, ref iter)) != IntPtr.Zero && guard++ < 128)
						if (NativeGuard.IsReadable(m, 32, false)) sample.Add(m);
				}
				catch { }
				if (sample.Count >= 120) break;
			}
			if (sample.Count < 8)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign] impossible de mesurer l'entree de code d'une methode — les methodes strippees ne seront pas ecartees.");
				return;
			}

			int bestOff = -1, bestHits = 0;
			for (int off = 0; off <= 24; off += 8)
			{
				int hits = 0;
				foreach (IntPtr m in sample) if (NativeGuard.IsExecutable(Slot(m, off))) hits++;
				if (hits > bestHits) { bestHits = hits; bestOff = off; }
			}
			if (bestOff >= 0 && bestHits * 10 >= sample.Count * 7)
			{
				_mpOff = bestOff;
				VRChatArchiveModPlugin.Logger.LogInfo($"[MemberAlign] entree de code d'une methode mesuree a +{bestOff} ({bestHits}/{sample.Count} methodes vivantes).");
				return;
			}
			VRChatArchiveModPlugin.Logger.LogWarning($"[MemberAlign] aucune position d'entree de code ne convient ({bestHits}/{sample.Count} au mieux, a +{bestOff}) — les methodes strippees ne seront pas ecartees.");
		}

		// False only when we are SURE the method has no code: an unmeasurable layout leaves everything
		// allowed, because refusing on no evidence would silence members that work.
		internal static bool Callable(IntPtr method)
		{
			if (method == IntPtr.Zero) return false;
			CalibrateMethodPointer();
			if (_mpOff < 0) return true;
			if (!NativeGuard.IsReadable(method, _mpOff + 8, false)) return false;
			if (NativeGuard.IsExecutable(Slot(method, _mpOff))) return true;
			_stripped++;
			return false;
		}

		internal static int Stripped => _stripped;
		private static int _stripped;

		// Methods placed only by the permuted route: safe to call, never safe to detour.
		private static readonly HashSet<IntPtr> _weak = new HashSet<IntPtr>();

		internal static bool TooWeakToHook(IntPtr method)
		{
			if (method == IntPtr.Zero) return true;
			// The neutral method above all: it is a real, healthy il2cpp method standing in for members
			// this build does not have, so Harmony will detour it without complaint -- onto something the
			// game calls constantly, with a prefix written for an entirely different signature.
			if (method == TokenShiftFix.NoOpPointer) return true;
			lock (_weak) return _weak.Contains(method);
		}

		// ---- lining the two sequences up ---------------------------------------------------------

		// Longest common subsequence: map[i] is the live index paired with recorded member i, or -1.
		// Members an update added or removed simply stay unpaired instead of shifting everything after
		// them, which a plain index-for-index walk would do.
		private static int[] Align(IList<string> want, IList<string> have)
		{
			int n = want.Count, m = have.Count;
			var map = new int[n];
			for (int i = 0; i < n; i++) map[i] = -1;
			if (n == 0 || m == 0) return map;

			if ((long)n * m > 400000)
			{
				// Too big to align properly; only an exact same-length sequence is trusted.
				if (n != m) return map;
				for (int i = 0; i < n; i++) if (want[i] == have[i]) map[i] = i;
				return map;
			}

			var dp = new int[n + 1, m + 1];
			for (int i = n - 1; i >= 0; i--)
				for (int j = m - 1; j >= 0; j--)
					dp[i, j] = want[i] == have[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
			int x = 0, y = 0;
			while (x < n && y < m)
			{
				if (want[x] == have[y]) { map[x] = y; x++; y++; }
				else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
				else y++;
			}
			return map;
		}

		// Three quarters of a class's members have to find a partner before any of them is trusted. A
		// class that only half matches is probably not the same class, and binding half of it would be
		// worse than leaving it dark: the failures would be silent instead of obvious.
		private const int AcceptNum = 3, AcceptDen = 4;

		// THE OBFUSCATOR PERMUTES MEMBERS, IT DOES NOT ONLY RENAME THEM.
		//
		// Measured on a 76-method VRChat class: both sequences carry the same 76 shapes and the longest
		// common subsequence is only 29, because the members come back in a different ORDER. (Type tokens
		// turned out to be permuted the same way, so this is the obfuscator being consistent.) Order-based
		// alignment is then not merely weak, it is wrong -- it pairs members that merely happen to line up.
		//
		// What survives a permutation is a shape that occurs EXACTLY ONCE on each side. There is then only
		// one member it can be, whatever order they arrive in, and the pairing is a fact rather than an
		// inference. Rarer than a full alignment, and certain, which is the right trade for something that
		// ends in a call.
		private static string NamesOf(string shape)
		{
			int i = shape.IndexOf('|');
			return i < 0 ? "" : shape.Substring(i + 1);
		}

		// COULD THESE TWO BE THE SAME MEMBER?
		//
		// Identical kinds, and no type name that CONTRADICTS. A name missing on one side proves nothing:
		// VRChat renamed RaiseEventOptions between builds, so the live signature of RaiseEvent reads
		// "Object;;SendOptions" where the table says "Object;RaiseEventOptions;SendOptions". Demanding
		// the two strings be equal threw that member away over a slot neither side disagrees about, and
		// with it the outbound network log and the voice mimic.
		private static bool Compatible(string want, string have)
		{
			if (Base(want) != Base(have)) return false;
			string wn = NamesOf(want), hn = NamesOf(have);
			if (wn.Length == 0 || hn.Length == 0) return true;
			var a = wn.Split(';');
			var b = hn.Split(';');
			if (a.Length != b.Length) return true;
			for (int i = 0; i < a.Length; i++)
				if (a[i].Length > 0 && b[i].Length > 0 && a[i] != b[i]) return false;
			return true;
		}

		// Do they AGREE about a name, rather than merely not disagreeing? That is the difference between
		// an identification and an elimination, and only the first is strong enough to hook.
		private static bool Corroborates(string want, string have)
		{
			string wn = NamesOf(want), hn = NamesOf(have);
			if (wn.Length == 0 || hn.Length == 0) return false;
			var a = wn.Split(';');
			var b = hn.Split(';');
			if (a.Length != b.Length) return false;
			for (int i = 0; i < a.Length; i++)
				if (a[i].Length > 0 && a[i] == b[i]) return true;
			return false;
		}

		// A pairing is kept only when it is the ONLY possibility in both directions: this recorded member
		// fits exactly one live member, and that live member fits exactly one recorded member. Anything
		// else is a choice among equals, and there is nothing to choose with.
		// TWO ROUNDS, STRICT THEN TOLERANT.
		//
		// The strict round pairs members whose shapes are IDENTICAL and occur once on each side; that is
		// what identifies OnEvent, and loosening it cost that pairing because the looser relation let a
		// second recorded member fit the same live one. The tolerant round then runs over what is left,
		// where a name missing on one side no longer disqualifies anything -- which is what identifies
		// RaiseEvent, whose RaiseEventOptions parameter VRChat renamed. Both keep the same rule: a
		// pairing survives only if it is the sole possibility in BOTH directions.
		private static Dictionary<int, int> ByUniqueMatch(IList<string> want, IList<string> have, out HashSet<int> corroborated)
		{
			corroborated = new HashSet<int>();
			var map = new Dictionary<int, int>();
			int n = want.Count, m = have.Count;
			if (n == 0 || m == 0 || (long)n * m > 1000000) return map;

			var takenHave = new bool[m];
			foreach (var kv in ByUniqueShape(want, have))
			{
				map[kv.Key] = kv.Value;
				takenHave[kv.Value] = true;
				if (Corroborates(want[kv.Key], have[kv.Value])) corroborated.Add(kv.Key);
			}

			var forward = new int[n];
			var backCount = new int[m];
			for (int i = 0; i < n; i++)
			{
				forward[i] = -1;
				if (map.ContainsKey(i) || want[i] == "?" || want[i].Length == 0) continue;
				int hit = -1, seen = 0;
				for (int j = 0; j < m; j++)
				{
					if (takenHave[j] || !Compatible(want[i], have[j])) continue;
					if (++seen > 1) break;
					hit = j;
				}
				if (seen == 1) { forward[i] = hit; backCount[hit]++; }
			}
			for (int i = 0; i < n; i++)
			{
				int j = forward[i];
				if (j < 0 || backCount[j] != 1) continue;
				map[i] = j;
				takenHave[j] = true;
				if (Corroborates(want[i], have[j])) corroborated.Add(i);
			}

			// A THIRD ROUND, ON THE KINDS ALONE.
			//
			// Both rounds above weigh type names, and a member whose slots are all primitives or all
			// obfuscated offers none -- so the tolerant round, which needs a candidate to be the sole
			// possibility, often cannot separate them. Falling back to the bare kinds recovers those,
			// and dropping this round cost HighlightsFX four of its nineteen members, which put the whole
			// class under the bar ProxyGuard uses and left the glow calling a getter it was then refused.
			// Never corroborated, so never hookable.
			var wb = Bases(want);
			var hb = Bases(have);
			foreach (var kv in ByUniqueShape(wb, hb))
			{
				if (map.ContainsKey(kv.Key) || takenHave[kv.Value]) continue;
				map[kv.Key] = kv.Value;
				takenHave[kv.Value] = true;
			}
			return map;
		}

		private static Dictionary<int, int> ByUniqueShape(IList<string> want, IList<string> have)
		{
			var wc = new Dictionary<string, int>(StringComparer.Ordinal);
			var hc = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (string s in want) { wc.TryGetValue(s, out int n); wc[s] = n + 1; }
			foreach (string s in have) { hc.TryGetValue(s, out int n); hc[s] = n + 1; }
			var firstHave = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int j = 0; j < have.Count; j++) if (!firstHave.ContainsKey(have[j])) firstHave[have[j]] = j;

			var map = new Dictionary<int, int>();
			for (int i = 0; i < want.Count; i++)
			{
				string s = want[i];
				if (s == "?" || s.Length == 0) continue;
				if (!wc.TryGetValue(s, out int nw) || nw != 1) continue;
				if (!hc.TryGetValue(s, out int nh) || nh != 1) continue;
				map[i] = firstHave[s];
			}
			return map;
		}

		// How many members the two sequences have in common regardless of order. This is the honest
		// measure of "is this the same class": counting agreement, not layout.
		private static int MultisetScore(IList<string> want, IList<string> have)
		{
			var hc = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (string s in have) { hc.TryGetValue(s, out int n); hc[s] = n + 1; }
			int score = 0;
			foreach (string s in want)
				if (hc.TryGetValue(s, out int n) && n > 0) { hc[s] = n - 1; score++; }
			return score;
		}

		private static Aligned Build(IntPtr clazz, string key)
		{
			var a = new Aligned();
			if (!TokenShiftFix.RecordedMembers(key, out int minTok, out string mSpec, out string fSpec)) return a;
			a.MinTok = minTok;

			if (!string.IsNullOrEmpty(mSpec))
			{
				var want = mSpec.Split(',');
				a.Want = want;
				var live = new List<IntPtr>();
				var have = LiveMethodShapes(clazz, live);
				var d = new Dictionary<int, IntPtr>();

				// NAMES FIRST, WHERE THERE ARE ANY.
				//
				// VRChat renames its CLASSES far more thoroughly than its methods: inside a class whose
				// own name is gone, get_IsVRCPlus is still called get_IsVRCPlus. TokenShiftFix already
				// records every plain, unique method name per type, and a recovered class simply never
				// got to use them -- its anchors are filed under the 1886 key, not under what il2cpp
				// calls it now. A name that matches with the right arity and staticness identifies the
				// member outright, which is stronger than any shape and strong enough to hook.
				int byName = 0;
				TokenShiftFix.ForEachAnchor(key, (tok, name, pars, stat) =>
				{
					int idx = tok - minTok;
					if (idx < 0 || idx >= want.Length || d.ContainsKey(tok)) return;
					IntPtr hit = IntPtr.Zero;
					int seen = 0;
					for (int j = 0; j < live.Count; j++)
					{
						try
						{
							var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)live[j]);
							if (mw.ParametersCount != pars) continue;
							if ((((ushort)mw.Flags & METHOD_STATIC) != 0) != stat) continue;
							if (Str(mw.Name) != name) continue;
							if (++seen > 1) break;
							hit = live[j];
						}
						catch { }
					}
					if (seen == 1 && Callable(hit)) { d[tok] = hit; byName++; }
				});
				_byName += byName;

				var wantBase = Bases(want);
				var haveBase = Bases(have);

				// Order first, because when the order DID survive it places every member, including the
				// ones whose shape it shares with a neighbour.
				var map = Align(wantBase, haveBase);
				int lcs = 0;
				foreach (int v in map) if (v >= 0) lcs++;
				if (lcs * AcceptDen >= want.Length * AcceptNum)
				{
					for (int i = 0; i < want.Length; i++)
						if (map[i] >= 0 && want[i] != "?" && !d.ContainsKey(minTok + i)) d[minTok + i] = live[map[i]];
				}
				else
				{
					// Permuted: the member order no longer agrees, so each one has to identify itself.
					var pairs = ByUniqueMatch(want, have, out var corroborated);
					foreach (var kv in pairs)
					{
						if (d.ContainsKey(minTok + kv.Key)) continue;   // already identified by name
						d[minTok + kv.Key] = live[kv.Value];
						// A pairing where the two sides AGREE on at least one type name is an identity:
						// RaiseEvent is the only method of LoadBalancingClient returning a bool and taking
						// a byte, an Object, something, and a SendOptions. Those are strong enough to hook.
						// A pairing resting on kinds alone is a choice among equals -- fine to call, never
						// safe to detour, because a detour hands VRChat's real arguments to a prefix
						// written for different ones and Il2CppInterop converts them before any of the
						// mod's own code can run.
						if (corroborated.Contains(kv.Key)) _named++;
						else lock (_weak) _weak.Add(live[kv.Value]);
					}
					if (d.Count > 0) _permuted++;
				}
				if (_dumpShapes > 0 && want.Length == _dumpShapes && _dumpedPermuted++ < 4)
				{
					VRChatArchiveModPlugin.Logger.LogInfo("[MemberAlign]   " + key + " table : " + Head(want, 200));
					VRChatArchiveModPlugin.Logger.LogInfo("[MemberAlign]   " + key + " vivant: " + Head(have.ToArray(), 200));
				}
				a.MethodTotal = want.Length;
				a.MethodHits = d.Count;
				if (d.Count > 0) { a.Methods = d; _placedM += d.Count; }
				if (d.Count > 0 && d.Count < want.Length && _detailed++ < 20)
					VRChatArchiveModPlugin.Logger.LogInfo($"[MemberAlign] {key} : {d.Count}/{want.Length} membres replaces par leur forme.");
				if (d.Count * AcceptDen >= want.Length * AcceptNum) a.Trustworthy = true;
			}

			if (!string.IsNullOrEmpty(fSpec))
			{
				var raw = fSpec.Split(',');
				var wantKind = new string[raw.Length];
				var wantName = new string[raw.Length];
				for (int i = 0; i < raw.Length; i++)
				{
					int k = raw[i].Length > 0 && raw[i][0] == '@' ? 2 : 1;
					wantKind[i] = raw[i].Length >= k ? raw[i].Substring(0, k) : "?";
					wantName[i] = raw[i].Length > k ? raw[i].Substring(k) : null;
				}
				var live = new List<IntPtr>();
				var have = LiveFieldShapes(clazz, live);
				var d = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

				// A field's kind is one character, so the same kind repeats all over a class and a
				// unique-shape pairing would claim almost nothing. Order is the only usable key here,
				// and it is required to fit nearly perfectly before anything is bound: a mis-paired
				// field reads the wrong offset, which is the one failure that is not merely wrong.
				var map = Align(wantKind, have);
				int hit = 0;
				foreach (int v in map) if (v >= 0) hit++;
				if (hit * AcceptDen >= raw.Length * AcceptNum)
					for (int i = 0; i < raw.Length; i++)
						if (map[i] >= 0 && wantName[i] != null && wantKind[i] != "?") d[wantName[i]] = live[map[i]];
				a.FieldTotal = raw.Length;
				a.FieldHits = d.Count;
				if (d.Count > 0) { a.Fields = d; _placedF += d.Count; }
			}

			if (a.Methods == null && a.Fields == null && (a.MethodTotal > 0 || a.FieldTotal > 0))
			{
				_refusedClasses++;
				if (_refusedClasses <= 15)
					VRChatArchiveModPlugin.Logger.LogWarning(
						$"[MemberAlign] {key} : formes trop differentes ({a.MethodHits}/{a.MethodTotal} methodes, {a.FieldHits}/{a.FieldTotal} champs) — non appariee.");
				// THE TWO SEQUENCES, SIDE BY SIDE, ONCE. A percentage says the shapes disagree; only the
				// sequences say HOW, and every disagreement so far turned out to be one systematic rule
				// (a value type generated as a class, a constant dropped) visible at a glance here.
				if (_refusedClasses == 1 && !string.IsNullOrEmpty(mSpec))
					try
					{
						var l = LiveMethodShapes(clazz, null);
						VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign]   table : " + Head(mSpec.Split(','), 30));
						VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign]   vivant: " + Head(l.ToArray(), 30));
					}
					catch { }
			}
			return a;
		}

		private static string Head(string[] v, int n)
		{
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < v.Length && i < n; i++) { if (i > 0) sb.Append(','); sb.Append(v[i]); }
			if (v.Length > n) sb.Append(",... (" + v.Length + ")");
			return sb.ToString();
		}

		// How many of a recorded member sequence a candidate class can actually account for. This is the
		// tie-breaker ObfuscatedClassFinder falls back on when two classes have identical member COUNTS:
		// counts are two numbers, the sequence is dozens of symbols, and only one candidate fits it.
		internal static int ShapeScore(IntPtr clazz, string[] want)
		{
			if (clazz == IntPtr.Zero || want == null || want.Length == 0) return 0;
			return MultisetScore(Bases(want), Bases(LiveMethodShapes(clazz, null)));
		}

		internal static Aligned For(IntPtr clazz, string key)
		{
			if (!_usable || clazz == IntPtr.Zero || string.IsNullOrEmpty(key)) return null;
			lock (_cache) { if (_cache.TryGetValue(clazz, out var hit)) return hit; }
			Aligned a;
			try { a = Build(clazz, key); } catch { a = new Aligned(); }
			lock (_cache) { _cache[clazz] = a; }
			return a;
		}

		// IS THIS REALLY THE MEMBER THE TOKEN NAMES?
		//
		// A class's own name anchors place its methods by measuring a shift, and a shift is an
		// inference: when a member moved on its own, the translation lands on a REAL method of the right
		// class with the wrong signature -- and calling that is an access violation, not a wrong answer.
		// Photon.Client.EventData.get_Sender ended the process that way on every run, with a no-op and
		// without one, because it was never going through the missing-member path at all.
		//
		// The recorded shape settles it in one comparison: the member at this index has a known arity,
		// staticness and parameter kinds, and whatever the shift produced either matches them or is not
		// that member. A type the table has no shapes for keeps the old behaviour rather than being
		// refused on no evidence.
		internal static bool ShapeOk(IntPtr clazz, string key, int token, IntPtr method)
		{
			if (!_usable) return true;
			Aligned a;
			try { a = For(clazz, key); } catch { return true; }
			if (a?.Want == null) return true;
			int i = token - a.MinTok;
			if (i < 0 || i >= a.Want.Length) return true;
			string w = Base(a.Want[i]);
			if (w == "?" || w.Length == 0) return true;
			bool ok = w == Base(MethodShape(method));
			if (!ok && _mismatch++ < 20)
				VRChatArchiveModPlugin.Logger.LogWarning($"[MemberAlign] {key} : le token 0x{token:X8} tombe sur une methode de forme '{Base(MethodShape(method))}' au lieu de '{w}' — appel refuse.");
			return ok;
		}

		// THE PROXY'S OWN SIGNATURE IS A STRONGER FINGERPRINT THAN ANY SHAPE.
		//
		// Shapes describe a member in kinds -- "returns a reference, takes nothing" -- and three of
		// VRCPlusStatus's methods answer to that: one hands back a Nullable<DateTime>, one a
		// ReactiveProperty<bool>, one a ReactiveProperty<List<string>>. No amount of kind-matching can
		// separate them, so the VRC+ getter stayed unidentified and its hook was refused.
		//
		// But the generated proxy names those types exactly, and Il2CppInterop already knows which live
		// class each of them is -- including closed generics. Comparing CLASS POINTERS slot by slot
		// identifies the method outright rather than narrowing it down. Slots whose managed type has no
		// il2cpp class (primitives, the mod's own types) simply carry no opinion, which still leaves
		// arity and staticness doing their part.
		internal static IntPtr ResolveByProxySignature(MethodBase proxy)
		{
			if (proxy == null) return IntPtr.Zero;
			Type t = proxy.DeclaringType;
			if (t == null) return IntPtr.Zero;
			IntPtr clazz;
			try { clazz = Il2CppClassPointerStore.GetNativeClassPointer(t); }
			catch { return IntPtr.Zero; }
			if (clazz == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(clazz)) return IntPtr.Zero;

			var ps = proxy.GetParameters();
			bool wantStatic = proxy.IsStatic;
			var wantSlots = new Want[ps.Length + 1];
			wantSlots[0] = WantOf((proxy as MethodInfo)?.ReturnType);
			for (int i = 0; i < ps.Length; i++) wantSlots[i + 1] = WantOf(ps[i].ParameterType);

			// Nothing to go on beyond arity: too weak to claim an identification.
			bool anyKnown = false;
			foreach (Want w in wantSlots) if (w.Any) { anyKnown = true; break; }
			if (!anyKnown) return IntPtr.Zero;

			IntPtr hit = IntPtr.Zero;
			int seen = 0;
			IntPtr iter = IntPtr.Zero, m;
			int guard = 0;
			while ((m = IL2CPP.il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero && guard++ < 4096)
			{
				try
				{
					var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m);
					if (mw.ParametersCount != ps.Length) continue;
					if ((((ushort)mw.Flags & METHOD_STATIC) != 0) != wantStatic) continue;
					if (!Agrees(wantSlots[0], IL2CPP.il2cpp_method_get_return_type(m))) continue;
					bool ok = true;
					for (int i = 0; i < ps.Length; i++)
						if (!Agrees(wantSlots[i + 1], IL2CPP.il2cpp_method_get_param(m, (uint)i))) { ok = false; break; }
					if (!ok) continue;
					if (++seen > 1) return IntPtr.Zero;   // not an identification any more
					hit = m;
				}
				catch { }
			}
			return seen == 1 && Callable(hit) ? hit : IntPtr.Zero;
		}

		// IDENTIFYING THE RIGHT METHOD AND VETTING THE ONE ALREADY BOUND ARE DIFFERENT QUESTIONS.
		//
		// ResolveByProxySignature can only answer when the proxy's types resolve well enough to leave
		// exactly one candidate. A static getter taking nothing has a single slot to go on -- its return
		// type -- and when that type is itself absent from this build there is nothing to search with,
		// so it gives up. RoomManager's ApiWorldInstance getter is one of those, and falling back to
		// "Callable says there is code there" let a wrong pointer through to an access violation.
		// This asks the cheaper question instead: does the method CURRENTLY bound agree with what the
		// proxy declares? Arity and staticness always answer, and every slot whose type does resolve
		// adds to it. A slot whose type is unknown carries no opinion, exactly as in the search above,
		// so this never rejects a member for being unresolvable -- only for actively disagreeing.
		internal static bool SlotMatchesProxy(IntPtr method, MethodBase proxy)
		{
			if (method == IntPtr.Zero || proxy == null) return false;
			try
			{
				var ps = proxy.GetParameters();
				var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)method);
				if (mw.ParametersCount != ps.Length) return false;
				if ((((ushort)mw.Flags & METHOD_STATIC) != 0) != proxy.IsStatic) return false;
				if (!Agrees(WantOf((proxy as MethodInfo)?.ReturnType), IL2CPP.il2cpp_method_get_return_type(method)))
					return false;
				for (int i = 0; i < ps.Length; i++)
					if (!Agrees(WantOf(ps[i].ParameterType), IL2CPP.il2cpp_method_get_param(method, (uint)i)))
						return false;
				return true;
			}
			catch { return false; }
		}

		// VALUE TYPES CARRY AN OPINION TOO.
		//
		// This used to answer only for Il2CppObjectBase-derived types, so every engine STRUCT --
		// Rect, Vector3, Color, Quaternion -- counted as "no opinion" and overloads that differ only
		// in a struct parameter were indistinguishable. Il2CppInterop keeps a class pointer for those
		// as well, so asking the store settles them; a type it does not know still answers Zero and
		// is treated as unknown exactly as before.
		private static IntPtr ClassOf(Type managed)
		{
			if (managed == null) return IntPtr.Zero;
			if (managed.IsByRef) managed = managed.GetElementType();
			if (managed == null || managed.IsPrimitive || managed == typeof(string)) return IntPtr.Zero;
			try
			{
				IntPtr k = Il2CppClassPointerStore.GetNativeClassPointer(managed);
				return k != IntPtr.Zero && !MissingTypeGuard.IsPlaceholder(k) ? k : IntPtr.Zero;
			}
			catch { return IntPtr.Zero; }
		}

		// A PRIMITIVE SLOT IS NOT AN UNKNOWN SLOT.
		//
		// Class-pointer matching can only speak about types Il2CppInterop wraps, so every primitive
		// counted as "no opinion" -- and a signature made only of primitives therefore carried no
		// opinion at all. That is one blind spot, not several: UnityEngine.Vector3's two constructors
		// (floats), Mathf.Max(float,float) against Max(int,int) -- same name, same arity, same
		// staticness, and nothing left to separate them, so both the name index and the signature
		// search declined and the shape route guessed wrong. Each one crashed the first caller.
		// A primitive has a perfectly good il2cpp class with a stable name, so it is matched by that.
		private static string PrimName(Type t)
		{
			if (t == null) return null;
			if (t.IsByRef) t = t.GetElementType();
			if (t == typeof(void)) return "Void";
			if (t == typeof(float)) return "Single";
			if (t == typeof(double)) return "Double";
			if (t == typeof(int)) return "Int32";
			if (t == typeof(uint)) return "UInt32";
			if (t == typeof(long)) return "Int64";
			if (t == typeof(ulong)) return "UInt64";
			if (t == typeof(short)) return "Int16";
			if (t == typeof(ushort)) return "UInt16";
			if (t == typeof(byte)) return "Byte";
			if (t == typeof(sbyte)) return "SByte";
			if (t == typeof(bool)) return "Boolean";
			if (t == typeof(char)) return "Char";
			if (t == typeof(IntPtr)) return "IntPtr";
			// A managed string is il2cpp's System.String. Without this, UnityEngine.GUI.Label(Rect,
			// string) could not be told from Label(Rect, Texture) or Label(Rect, GUIContent) -- same
			// name, same arity, same staticness -- and the mod's whole IMGUI menu drew through
			// whichever one the shape route guessed.
			if (t == typeof(string)) return "String";
			return null;
		}

		// What a proxy slot expects: a live class pointer, a primitive's class name, or nothing.
		private readonly struct Want
		{
			public readonly IntPtr Klass;
			public readonly string Prim;
			public Want(IntPtr k, string p) { Klass = k; Prim = p; }
			public bool Any => Klass != IntPtr.Zero || Prim != null;
		}

		private static Want WantOf(Type managed)
		{
			string p = PrimName(managed);
			if (p != null) return new Want(IntPtr.Zero, p);
			return new Want(ClassOf(managed), null);
		}

		private static bool Agrees(Want want, IntPtr liveType)
		{
			if (!want.Any) return true;                  // no opinion about this slot
			if (liveType == IntPtr.Zero) return false;
			try
			{
				IntPtr k = IL2CPP.il2cpp_class_from_type(liveType);
				if (k == IntPtr.Zero) return false;
				if (want.Klass != IntPtr.Zero) return k == want.Klass;
				var kw = UnityVersionHandler.Wrap((Il2CppClass*)k);
				IntPtr np = kw.Name;
				return np != IntPtr.Zero && NativeGuard.IsReadable(np, 1, false) && Str(np) == want.Prim;
			}
			catch { return false; }
		}

		// FOR A TYPE NOBODY RENAMED, THE NAME IS THE ANSWER -- NO MEASUREMENT REQUIRED.
		//
		// Shapes and token shifts exist because VRChat obfuscates ITS OWN assemblies. The engine's are
		// untouched: UnityEngine.Application.get_unityVersion is called exactly that in the live
		// metadata, on every build. Yet the core rebind placed those members by measuring a token shift
		// and checking a SHAPE -- and a shape cannot separate two static methods that both return a
		// reference, so the engine's string getters landed on each other. Invoked through the proxy that
		// is an access violation, and get_unityVersion took the process down with one.
		// Il2CppInterop encodes the original name in the generated field
		// (NativeMethodInfoPtr_get_unityVersion_Public_Static_String_0), so for an unobfuscated type the
		// member can simply be looked up by it. An overload the name does not separate on its own is
		// refused rather than guessed, which leaves the shape route to settle those.
		private static readonly string[] _accessMarks =
		{
			"_Public_", "_Private_", "_Internal_", "_Protected_", "_ProtectedInternal_", "_PrivateProtected_",
		};

		// Diagnostic: the live member names of a class, as il2cpp reports them on this build.
		internal static string DumpNames(IntPtr clazz)
		{
			var m = NameIndex(clazz);
			if (m == null) return "(aucun index)";
			var keys = new List<string>(m.ByNameArity.Keys);
			keys.Sort(StringComparer.Ordinal);
			return string.Join(", ", keys);
		}

		internal static IntPtr ResolveByFieldName(IntPtr clazz, string fieldName, MethodBase proxyMethod = null)
		{
			const string P = "NativeMethodInfoPtr_";
			if (clazz == IntPtr.Zero || fieldName == null || !fieldName.StartsWith(P, StringComparison.Ordinal))
				return IntPtr.Zero;
			int cut = -1;
			foreach (string a in _accessMarks)
			{
				int i = fieldName.IndexOf(a, P.Length, StringComparison.Ordinal);
				if (i >= 0 && (cut < 0 || i < cut)) cut = i;
			}
			if (cut < 0) return IntPtr.Zero;
			string name = fieldName.Substring(P.Length, cut - P.Length);
			if (name.Length == 0) return IntPtr.Zero;
			// Il2CppInterop cannot put a '.' in an identifier, so .ctor and .cctor arrive as _ctor and
			// _cctor. Left unmapped they match nothing, and every constructor in the core list fell
			// through to the shape route -- which is how UnityEngine.Vector3's three-float constructor
			// ended up on another method and crashed the module that built a vector.
			if (name == "_ctor") name = ".ctor";
			else if (name == "_cctor") name = ".cctor";
			bool wantStatic = fieldName.IndexOf("_Static_", cut, StringComparison.Ordinal) >= 0;
			if (MissingTypeGuard.IsPlaceholder(clazz)) return IntPtr.Zero;

			var maps = NameIndex(clazz);
			if (maps == null) return IntPtr.Zero;
			IntPtr hit = IntPtr.Zero;
			// ARITY SEPARATES WHAT THE NAME CANNOT.
			//
			// A name shared by several members is ambiguous only until the number of arguments is
			// added, and the proxy method states that exactly. UnityEngine.Vector3 is the case that
			// forced this: it has two constructors, both called .ctor and neither static, so the
			// name-only index refused them -- and ResolveByProxySignature could not step in either,
			// because every parameter is a float and a primitive gives it no class pointer to match on.
			// Two routes blind to the same member, which then reached the proxy unrepaired and crashed
			// the first module to build a vector. Arity tells them apart at a glance.
			if (proxyMethod != null)
			{
				int n = proxyMethod.GetParameters().Length;
				maps.ByNameArity.TryGetValue(name + "/" + n + (wantStatic ? "@" : ""), out hit);
			}
			if (hit == IntPtr.Zero) maps.ByName.TryGetValue(name + (wantStatic ? "@" : ""), out hit);
			if (hit == IntPtr.Zero) return IntPtr.Zero;
			if (!Callable(hit))
			{
				// Named, found, and STRIPPED: il2cpp kept the MethodInfo but compiled no body. Saying so
				// matters -- it is the one case where the right answer exists and still cannot be called,
				// and silently returning zero sends it down the shape route to be mistaken for something
				// else.
				if (_stripNamed++ < 20)
					VRChatArchiveModPlugin.Logger.LogWarning($"[MemberAlign] {name} trouvee par son nom mais sans code compile (strippee par il2cpp) — non appelable.");
				return IntPtr.Zero;
			}
			return hit;
		}

		private static int _stripNamed;
		internal static int StrippedNamed => _stripNamed;

		// name (+ '@' when static) -> the single method that answers to it. A name carried by more than
		// one method of the class is dropped from the index rather than resolved arbitrarily: an
		// overload the name cannot separate is left to the shape route.
		//
		// Names come from the WRAPPER, not from il2cpp_method_get_name. The pack rebinds the il2cpp
		// exports to this build's obfuscated symbols, so an export is only as good as that remap, while
		// the wrapper reads an offset verified against the binary. Every name pointer is checked before
		// it is dereferenced -- unaligned, because a C string is not 8-aligned and requiring it would
		// reject every name.
		private sealed class NameMaps
		{
			public readonly Dictionary<string, IntPtr> ByName = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
			public readonly Dictionary<string, IntPtr> ByNameArity = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
		}

		private static readonly Dictionary<IntPtr, NameMaps> _nameIndex = new Dictionary<IntPtr, NameMaps>();

		private static NameMaps NameIndex(IntPtr clazz)
		{
			if (_nameIndex.TryGetValue(clazz, out var cached)) return cached;
			var maps = new NameMaps();
			var dupName = new HashSet<string>(StringComparer.Ordinal);
			var dupArity = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				IntPtr iter = IntPtr.Zero, m;
				int guard = 0;
				while ((m = IL2CPP.il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero && guard++ < 4096)
				{
					try
					{
						var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m);
						IntPtr np = mw.Name;
						if (np == IntPtr.Zero || !NativeGuard.IsReadable(np, 1, false)) continue;
						string n = Str(np);
						if (n.Length == 0) continue;
						string suffix = ((ushort)mw.Flags & METHOD_STATIC) != 0 ? "@" : "";
						Put(maps.ByName, dupName, n + suffix, m);
						Put(maps.ByNameArity, dupArity, n + "/" + mw.ParametersCount + suffix, m);
					}
					catch { }
				}
			}
			catch { }
			_nameIndex[clazz] = maps;
			return maps;
		}

		// First writer wins the slot; a second claimant removes it and blacklists the key, so an
		// ambiguous lookup returns nothing rather than an arbitrary one of the candidates.
		private static void Put(Dictionary<string, IntPtr> map, HashSet<string> dup, string key, IntPtr m)
		{
			if (dup.Contains(key)) return;
			if (map.ContainsKey(key)) { map.Remove(key); dup.Add(key); return; }
			map[key] = m;
		}

		internal static bool TryMethod(IntPtr clazz, string key, int token, out IntPtr method)
		{
			method = IntPtr.Zero;
			var a = For(clazz, key);
			return a?.Methods != null && a.Methods.TryGetValue(token, out method) && method != IntPtr.Zero;
		}

		// ---- the class's own table key -----------------------------------------------------------

		// A class recovered under a new name is keyed by the name the TABLE knows it under, not the one
		// il2cpp reports; anything else is keyed by what it calls itself.
		internal static string KeyOf(IntPtr clazz)
		{
			string k = ObfuscatedClassFinder.RecoveredKey(clazz);
			if (k != null) return k;
			try
			{
				var kw = UnityVersionHandler.Wrap((Il2CppClass*)clazz);
				string ns = Str(kw.Namespace), name = Str(kw.Name), decl = "";
				var dt = kw.DeclaringType;
				if (dt != null) { var dw = UnityVersionHandler.Wrap(dt); decl = Str(dw.Name); if (ns.Length == 0) ns = Str(dw.Namespace); }
				return ns + "|" + name + "|" + decl;
			}
			catch { return null; }
		}

		private static string Str(IntPtr p)
		{
			if (p == IntPtr.Zero) return "";
			try { return Marshal.PtrToStringAnsi(p) ?? ""; } catch { return ""; }
		}

		// ---- the field lookup --------------------------------------------------------------------

		// Il2CppInterop looks a field up by the name the 1886 metadata used and logs an error when it is
		// gone. For VRChat's own classes it is ALWAYS gone -- the names are re-randomised every update --
		// so the log filled with several hundred identical failures and every one of those fields read
		// as nothing. Answering the lookup here does both jobs at once: the name is resolved through the
		// alignment when il2cpp does not know it, and the error line never happens.
		private static bool FieldPrefix(IntPtr clazz, string fieldName, ref IntPtr __result)
		{
			if (clazz == IntPtr.Zero || fieldName == null) { __result = IntPtr.Zero; return false; }
			IntPtr f = IntPtr.Zero;
			try { f = IL2CPP.il2cpp_class_get_field_from_name(clazz, fieldName); } catch { }
			if (f == IntPtr.Zero && !MissingTypeGuard.IsPlaceholder(clazz))
			{
				var a = For(clazz, KeyOf(clazz));
				if (a?.Fields != null) a.Fields.TryGetValue(fieldName, out f);
			}
			if (f == IntPtr.Zero)
			{
				_missField++;
				if (_quiet++ < 20)
					VRChatArchiveModPlugin.Logger.LogWarning($"[MemberAlign] champ '{Short(fieldName)}' absent de {Short(NameOf(clazz))} — lecture neutralisee.");
			}
			__result = f;
			return false;
		}

		private static string NameOf(IntPtr clazz)
		{
			try { return Str(UnityVersionHandler.Wrap((Il2CppClass*)clazz).Name); } catch { return "?"; }
		}

		// Obfuscated names are 23 identical-looking glyphs; printing them whole makes the log unreadable.
		private static string Short(string s)
		{
			if (string.IsNullOrEmpty(s)) return "?";
			bool plain = true;
			foreach (char c in s) if (c > 126 || c < 32) { plain = false; break; }
			return plain ? s : "#" + (s.GetHashCode() & 0xFFFF).ToString("X4");
		}

		// ---- install + self-check ----------------------------------------------------------------

		internal static void Install()
		{
			try
			{
				TokenShiftFix.EnsureTable();
				if (!SelfTest()) { _usable = false; return; }

				var target = typeof(IL2CPP).GetMethod("GetIl2CppField", BindingFlags.Public | BindingFlags.Static);
				if (target != null)
					VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
						typeof(MemberAlign).GetMethod(nameof(FieldPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
				else
					VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign] IL2CPP.GetIl2CppField introuvable — les champs renommes resteront muets.");
				_armed = true;
			}
			catch (Exception e)
			{
				_usable = false;
				VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign] non installe : " + e.GetType().Name + " " + e.Message);
			}
		}

		// DOES THE LIVE READER AGREE WITH THE TABLE AT ALL?
		//
		// Reading a method's parameter kinds goes through il2cpp entry points this build exports under
		// scrambled names, and a single mis-bound one would make every shape come back as nonsense --
		// which would not fail loudly, it would just stop matching, or worse, match the wrong member. So
		// the reader is measured first, against classes whose names still resolve and whose recorded
		// shapes are therefore known to describe them.
		private static bool SelfTest()
		{
			string[][] probes =
			{
				new[] { "UnityEngine.CoreModule.dll", "UnityEngine", "Transform" },
				new[] { "UnityEngine.CoreModule.dll", "UnityEngine", "GameObject" },
				new[] { "UnityEngine.CoreModule.dll", "UnityEngine", "Camera" },
			};
			int best = 0, bestTotal = 0; string bestName = "?";
			int fBest = 0, fTotal = 0, fLive = 0, mset = 0; string fWant = "", fHave = "";
			foreach (var p in probes)
			{
				try
				{
					IntPtr k = IL2CPP.GetIl2CppClass(p[0], p[1], p[2]);
					if (k == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(k)) continue;
					if (!TokenShiftFix.RecordedMembers(p[1] + "|" + p[2] + "|", out _, out string mSpec, out string fSpec)) continue;
					if (string.IsNullOrEmpty(mSpec)) continue;
					var want = mSpec.Split(',');
					var have = LiveMethodShapes(k, null);
					var map = Align(want, have);
					int hits = 0;
					foreach (int v in map) if (v >= 0) hits++;
					if (hits <= best) continue;
					mset = MultisetScore(want, have);
					best = hits; bestTotal = want.Length; bestName = p[2];

					// THE FIELD READER GETS THE SAME TREATMENT, and it is the one that was silently
					// broken: it read a field's attributes through an il2cpp entry point this build
					// exports under a scrambled name, every field came back looking like a constant, and
					// the live list was therefore always empty -- 0 out of 6 on every class, with nothing
					// in the log to say so. Measured here, that cannot happen twice.
					fBest = 0; fTotal = 0; fLive = 0;
					if (!string.IsNullOrEmpty(fSpec))
					{
						var rawF = fSpec.Split(',');
						var wantF = new string[rawF.Length];
						// Same split as Build uses: a static field's kind is TWO characters ('@' + kind),
						// and taking one made the probe report every recorded field as "@" and score zero
						// against a live side that was in fact perfect.
						for (int i = 0; i < rawF.Length; i++)
						{
							int kk = rawF[i].Length > 0 && rawF[i][0] == '@' ? 2 : 1;
							wantF[i] = rawF[i].Length >= kk ? rawF[i].Substring(0, kk) : "?";
						}
						var haveF = LiveFieldShapes(k, null);
						fLive = haveF.Count;
						var mapF = Align(wantF, haveF);
						foreach (int v in mapF) if (v >= 0) fBest++;
						fTotal = rawF.Length;
						fWant = Head(wantF, 40); fHave = Head(haveF.ToArray(), 40);
					}
				}
				catch { }
			}
			if (bestTotal == 0)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign] auto-test impossible (aucune classe temoin) — appariement desactive.");
				return false;
			}
			bool ok = best * 4 >= bestTotal * 3;
			VRChatArchiveModPlugin.Logger.Log(ok ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning,
				$"[MemberAlign] auto-test sur {bestName} : {best}/{bestTotal} methodes dans l'ordre ({mset} hors ordre) et {fBest}/{fTotal} champs reconnus par leur forme ({fLive} lus, {_calibCount} enumeres, {_structCount} annonces par la classe, {_calibNamed} identifies)"
				+ (ok ? " — appariement arme." : " — trop peu, appariement desactive."));
			if (ok && fTotal > 0 && fBest * 2 < fTotal)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign] la lecture des champs vivants est douteuse — les champs renommes resteront muets.");
				VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign]   champs table : " + fWant);
				VRChatArchiveModPlugin.Logger.LogWarning("[MemberAlign]   champs vivants: " + fHave);
			}
			return ok;
		}
	}
}
