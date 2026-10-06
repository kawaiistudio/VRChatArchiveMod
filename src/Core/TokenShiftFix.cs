using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace VRChatArchiveMod.Core
{
	// EVERY INTEROP CALL WAS REACHING THE WRONG METHOD.
	//
	// The interop assemblies were generated against VRChat build 1886, and a generated proxy binds
	// each method by metadata TOKEN. Build 1903 ships different Unity assemblies: one method removed
	// early in UnityEngine.CoreModule shifts every later token down by one, so on 1903 the 1886 token
	// of get_transform is the token of get_layer, GetActiveScene's is SetActiveScene's, and so on
	// for the whole assembly. Measured, method by method, with il2cpp's own tokens:
	//     get_frameCount   1886 token 0x06001175   1903 token 0x06001174
	//     GetActiveScene   1886 token 0x06001412   1903 token 0x06001411
	//     System.Object.Equals   0x06001231 on both  (mscorlib did not change)
	// That is why every value returned "worked" while being something else, and why any method whose
	// neighbour takes arguments ended the process the moment it was called.
	//
	// The remedy is to regenerate the interop for 1903. Until the encrypted metadata allows that, the
	// shift is undone here. An anchor table built from the interop's own IL (vrchat-pack-tools/tokmap)
	// lists, per class, the methods whose names are plain text: for each such anchor the live method
	// of that name is found by NAME, and the difference between its live token and its 1886 token is
	// the shift for that class. Shifts are kept piecewise by token, so a method inserted in the middle
	// of a class only misleads the range after it. Then GetIl2CppMethodByToken is answered with the
	// method whose live token is 1886 token + shift.
	//
	// Classes without a usable anchor keep the old behaviour and are counted, so the log says how much
	// of the interop is covered rather than leaving it to be guessed. Anything resolved before this
	// installs is re-bound by rewriting the cached NativeMethodInfoPtr_* fields of the core proxy
	// types, which is the only place a wrong pointer can otherwise survive.
	internal static class TokenShiftFix
	{
		private struct Anchor { public int Token; public string Name; public int Params; public bool Static; public int Fields; public string Parent; public int Ifaces; public int Attrs; }

		private sealed class ClassInfo
		{
			public Dictionary<int, IntPtr> Live = new Dictionary<int, IntPtr>();
			public List<KeyValuePair<int, int>> Pieces = new List<KeyValuePair<int, int>>();   // (1886 token, delta), sorted
			public bool Ok;
			public string Key, Image = "";

			public int DeltaFor(int token)
			{
				int d = Pieces[0].Value;
				for (int i = 0; i < Pieces.Count; i++)
				{
					if (Pieces[i].Key > token) break;
					d = Pieces[i].Value;
				}
				return d;
			}
		}

		private const ushort METHOD_ATTRIBUTE_STATIC = 0x0010;

		private static Dictionary<string, List<Anchor>> _anchors;
		// (namespace|class|declaring) -> the type's own 1886 TypeDef token, for ObfuscatedClassFinder.
		private static readonly Dictionary<string, uint> _typeTokens = new Dictionary<string, uint>(StringComparer.Ordinal);

		// THE SHAPE OF EVERY MEMBER, for the classes whose names no longer mean anything.
		//
		// One entry per type: the token its method block starts at, the ordered shapes of those methods,
		// and the ordered (kind + 1886 name) of its fields. MemberAlign lines these up against the live
		// class to give a renamed type its members back. The three rows a renamed type gets in the table
		// (invented name, obfuscated name with and without namespace) describe the same type and are
		// written consecutively, so they share one object instead of three copies of a long string.
		private sealed class MemberSpec { public int MinToken; public string M; public string F; }
		private static readonly Dictionary<string, MemberSpec> _members = new Dictionary<string, MemberSpec>(StringComparer.Ordinal);

		// THE PLAIN-NAMED METHODS OF ONE TYPE, FOR A CLASS NO LONGER FINDABLE BY NAME.
		//
		// Info() anchors a class on its own plain-named methods, but it keys the table by what il2cpp
		// calls that class NOW -- and a recovered class answers to a new obfuscated name, so the lookup
		// finds nothing and the anchors are never consulted. Yet VRChat renames its CLASSES far more
		// than its methods: get_IsVRCPlus is still called get_IsVRCPlus inside a class whose own name is
		// gone. MemberAlign asks for these under the 1886 key the recovery started from, where a name
		// that matches with the right arity is an identification rather than an inference.
		internal static void ForEachAnchor(string key, Action<int, string, int, bool> visit)
		{
			if (_anchors == null || key == null || visit == null) return;
			if (!_anchors.TryGetValue(key, out var list)) return;
			foreach (var a in list)
			{
				if (a.Name == "*POS*" || string.IsNullOrEmpty(a.Name)) continue;
				visit(a.Token, a.Name, a.Params, a.Static);
			}
		}

		internal static bool RecordedMembers(string key, out int minToken, out string mShapes, out string fShapes)
		{
			minToken = 0; mShapes = null; fShapes = null;
			if (key == null || !_members.TryGetValue(key, out var s)) return false;
			minToken = s.MinToken; mShapes = s.M; fShapes = s.F;
			return true;
		}

		// Class name -> its single table key, when the name is unambiguous. A base type is recorded by
		// NAME only (no namespace), so recovering a renamed parent means finding its row this way.
		private static Dictionary<string, string> _byClassName;

		internal static bool KeyForClassName(string name, out string key)
		{
			key = null;
			if (_anchors == null || string.IsNullOrEmpty(name)) return false;
			if (_byClassName == null)
			{
				_byClassName = new Dictionary<string, string>(StringComparer.Ordinal);
				var dup = new HashSet<string>(StringComparer.Ordinal);
				foreach (var k in _anchors.Keys)
				{
					int a = k.IndexOf('|'), b = k.IndexOf('|', a + 1);
					if (a < 0 || b < 0) continue;
					string cls = k.Substring(a + 1, b - a - 1);
					if (_byClassName.ContainsKey(cls)) { dup.Add(cls); continue; }
					_byClassName[cls] = k;
				}
				foreach (var d in dup) _byClassName.Remove(d);   // ambiguous names are no help
			}
			return _byClassName.TryGetValue(name, out key);
		}

		internal static bool RecordedShapeByKey(string key, out int methods, out int fields, out string parent, out int ifaces, out int attrs)
		{
			methods = fields = 0; parent = ""; ifaces = -1; attrs = -1;
			if (_anchors == null || key == null || !_anchors.TryGetValue(key, out var l)) return false;
			foreach (var a in l)
				if (a.Name == "*POS*") { methods = a.Params; fields = a.Fields; parent = a.Parent ?? ""; ifaces = a.Ifaces; attrs = a.Attrs; return methods > 0; }
			return false;
		}

		// Looked up by namespace+name only, the way il2cpp reports a live class.
		internal static bool RecordedTypeToken(string ns, string name, out uint token)
		{
			token = 0;
			if (_typeTokens == null) return false;
			return _typeTokens.TryGetValue(ns + "|" + name + "|", out token);
		}

		// How many methods and fields the interop says this type has. Class tokens turned out to be
		// PERMUTED between builds rather than shifted, so a renamed class is identified by its shape
		// instead: those two counts together are near-unique inside one assembly.
		internal static bool RecordedShape(string ns, string name, out int methods, out int fields, out string parent, out int ifaces, out int attrs)
		{
			methods = fields = 0; parent = ""; ifaces = -1; attrs = -1;
			if (_anchors == null || !_anchors.TryGetValue(ns + "|" + name + "|", out var l)) return false;
			foreach (var a in l)
				if (a.Name == "*POS*") { methods = a.Params; fields = a.Fields; parent = a.Parent ?? ""; ifaces = a.Ifaces; attrs = a.Attrs; return methods > 0; }
			return false;
		}
		private static readonly Dictionary<IntPtr, ClassInfo> _classes = new Dictionary<IntPtr, ClassInfo>();

		// CAN THIS CLASS'S MEMBERS BE TRUSTED AT ALL?
		//
		// A class whose own anchors (or whose own method block) place its tokens is safe to call. One
		// with neither is not: its members resolve to whatever the 1886 token happens to name here, and
		// on a permuted assembly that is a real method with the wrong signature — an access violation on
		// the first call. ProxyGuard asks this before letting any reflected invoke through, which is the
		// generic version of the crash HighlightsFX's static getter kept producing.
		internal static bool MembersTrustworthy(IntPtr clazz)
		{
			if (clazz == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(clazz)) return false;
			try
			{
				// A class whose members were placed BY THEIR SHAPE is as safe to call as an anchored
				// one: every pairing matched arity, staticness and parameter kinds, so a call cannot
				// land on a signature it does not fit. This is what reopens a recovered class -- they
				// were all refused here before, which is why finding HighlightsFX still left it dark.
				var a = MemberAlign.For(clazz, MemberAlign.KeyOf(clazz));
				if (a != null && a.Trustworthy) return true;
			}
			catch { }
			if (ObfuscatedClassFinder.IsRecovered(clazz)) return false;
			try { var ci = Info(clazz); return ci != null && ci.Ok; }
			catch { return false; }
		}
		private static int _fixed, _aligned, _neutralised, _byName, _badRoute, _stubShown, _unresolved, _noAnchor, _uniform, _piecewise, _rebound, _seenFields, _stubFields, _noInfo, _shown, _writeFail, _thrown, _positional;
		private static readonly Dictionary<string, Dictionary<int, int>> _imageVotes = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);

		// The shift most uniformly-shifted classes of an assembly agree on, when at least three do
		// and they agree by a clear majority.
		private static bool ImageDelta(string image, out int delta)
		{
			delta = 0;
			if (string.IsNullOrEmpty(image)) return false;
			lock (_imageVotes)
			{
				if (!_imageVotes.TryGetValue(image, out var votes)) return false;
				int total = 0, best = 0;
				foreach (var kv in votes) { total += kv.Value; if (kv.Value > best) { best = kv.Value; delta = kv.Key; } }
				return total >= 3 && best * 4 >= total * 3;
			}
		}
		private static bool _armed;

		internal static bool Armed => _armed;
		internal static string Summary =>
			$"{_fixed} tokens retraduits, {_aligned} replaces par leur forme, {_unresolved} sans cible, {_thrown} refuses, classes: {_uniform} uniformes / {_piecewise} par morceaux / {_positional} par position / {_noAnchor} sans ancre, {_rebound} champs reboundes";

		// The table is needed BEFORE the guard installs: MissingTypeGuard asks ObfuscatedClassFinder for
		// a renamed class the moment the first lookup misses, and that needs the recorded type tokens.
		// A MEMBER THAT CANNOT BE PLACED MUST STILL BE SAFE TO CALL.
		//
		// Il2CppInterop's own "missing member" stub finishes the type initializer, which is what saved
		// the mod from poisoned cctors — but INVOKING one ends the process (measured: the static getter
		// of HighlightsFX, straight through reflection). And the modules invoke: sixty-seven of them,
		// through their own reflection, with no single choke point to guard.
		//
		// So an unresolvable member resolves to a real, harmless method instead: Thread.MemoryBarrier,
		// static, void, no arguments. il2cpp invokes it happily and it does nothing. A caller expecting
		// an object gets null, a caller expecting a struct gets a managed NullReferenceException when
		// the null is unboxed — both are things every module already handles, and neither is fatal.
		private static IntPtr _noOp = new IntPtr(-1);

		// A REAL METHOD STANDS IN FOR A MISSING ONE, AND THAT MAKES IT HOOKABLE BY ACCIDENT.
		//
		// Il2CppInterop's own missing-member stub could never be detoured -- a hook on one simply failed
		// to install, which is why "hook install failed: 0" was harmless. The neutral method is a genuine
		// il2cpp method, so Harmony took it happily: VRChat's Photon OnEvent hook landed on
		// UnityEngine.Time.get_realtimeSinceStartup, and from then on EVERY read of the clock ran the
		// network prefix with whatever happened to be in the argument slots. That is where the EventData
		// access violation came from, and why it arrived a few seconds in and never in the same place.
		internal static IntPtr NoOpPointer => _noOp == new IntPtr(-1) ? IntPtr.Zero : _noOp;

		// THE NEUTRAL METHOD HAS TO BE ONE THIS BUILD ACTUALLY COMPILED.
		//
		// Thread.MemoryBarrier was the first choice and it is STRIPPED here: il2cpp still carries its
		// MethodInfo, so it resolved and looked perfectly healthy, but its methodPointer is null -- and
		// il2cpp_runtime_invoke jumps straight to it. The "safe" answer for a member that could not be
		// placed was therefore itself the access violation, which is what ended the process on
		// VRC.Player.APIUser and again on Method_Public_get_Boolean_9. Every candidate is now checked for
		// a real entry point, a real invoker and no parameters before it is handed out, and the list ends
		// on methods the mod itself calls every frame, so one of them is certainly compiled in.
		private static readonly string[][] NoOpCandidates =
		{
			new[] { "mscorlib.dll", "System.Threading", "Thread", "MemoryBarrier", "System.Void" },
			new[] { "UnityEngine.CoreModule.dll", "UnityEngine", "Debug", "ClearDeveloperConsole", "System.Void" },
			new[] { "mscorlib.dll", "System", "GC", "Collect", "System.Void" },
			new[] { "UnityEngine.CoreModule.dll", "UnityEngine", "Time", "get_frameCount", "System.Int32" },
			new[] { "UnityEngine.CoreModule.dll", "UnityEngine", "Time", "get_realtimeSinceStartup", "System.Single" },
		};

		// PROVE IT BY USE, NOT BY INSPECTION.
		//
		// Checking the candidate's entry point and invoker in its MethodInfo rejected all five, including
		// one the mod calls every single frame -- VRChat ships patched il2cpp structs, so those two
		// offsets are no more trustworthy than the exports were. The only reading that cannot be wrong is
		// a pointer a WORKING proxy is already using: VaClock.Now is read by
		// the profiler on every tick, so whatever il2cpp method its generated field holds is, by
		// observation, compiled in and callable. It is static and takes no arguments, which is all a
		// neutral answer needs to be; it returns a float that nobody will look at.
		private static readonly string[][] NoOpProxies =
		{
			new[] { "UnityEngine.Time", "NativeMethodInfoPtr_get_realtimeSinceStartup" },
			new[] { "UnityEngine.Time", "NativeMethodInfoPtr_get_frameCount" },
			new[] { "UnityEngine.Time", "NativeMethodInfoPtr_get_unscaledTime" },
		};

		private static IntPtr FromProxy(string typeName, string prefix)
		{
			Type t = typeName == "UnityEngine.Time" ? typeof(UnityEngine.Time) : null;
			if (t == null) return IntPtr.Zero;
			foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (f.FieldType != typeof(IntPtr) || !f.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
				try { return (IntPtr)f.GetValue(null); } catch { return IntPtr.Zero; }
			}
			return IntPtr.Zero;
		}

		private static IntPtr NoOpMethod()
		{
			if (_noOp != new IntPtr(-1)) return _noOp;
			_noOp = IntPtr.Zero;

			// Run the proxy's type initializer so those fields are filled -- WITHOUT calling into il2cpp.
			//
			// This used to read the property (`_ = VaClock.Now`), which does
			// far more than initialise: it performs an il2cpp_runtime_invoke. And NoOpMethod runs inside
			// the Harmony prefix on GetIl2CppMethodByToken, so that invoke re-entered the very path it
			// was called from -- UnityEngine.Time's own cctor asks for its method tokens, which lands
			// back in Prefix, which lands back here. The reentrancy guard above returns IntPtr.Zero to
			// the inner call, so the cctor was still part-way through when the outer invoke fired on a
			// NativeMethodInfoPtr field that had not been assigned yet. Invoking a null MethodInfo is an
			// access violation, and it killed the process during Load() on the Unity 6 build, under
			// VRC.Core.ApiAvatar's cctor.
			// RunClassConstructor is exactly the stated intent and nothing more: it forces the fields to
			// be populated and never invokes a single il2cpp method.
			try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(UnityEngine.Time).TypeHandle); } catch { }
			foreach (var p in NoOpProxies)
			{
				IntPtr m = FromProxy(p[0], p[1]);
				if (!MemberAlign.Callable(m)) continue;
				_noOp = m;
				VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix] methode neutre pour les membres introuvables : {p[1].Substring("NativeMethodInfoPtr_".Length)} @ 0x{m.ToString("X")}");
				return _noOp;
			}

			foreach (var c in NoOpCandidates)
			{
				try
				{
					IntPtr t = IL2CPP.GetIl2CppClass(c[0], c[1], c[2]);
					if (t == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(t)) continue;
					IntPtr m = IL2CPP.GetIl2CppMethod(t, false, c[4], c[3], new string[0]);
					if (!MemberAlign.Callable(m)) continue;
					_noOp = m;
					VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix] methode neutre pour les membres introuvables : {c[2]}.{c[3]} @ 0x{m.ToString("X")}");
					return _noOp;
				}
				catch { }
			}
			VRChatArchiveModPlugin.Logger.LogWarning("[TokenShiftFix] aucune methode neutre appelable sur ce build — les membres introuvables retombent sur le stub de l'interop.");
			return _noOp;
		}

		// The answer for any member this build does not have.
		//
		// A REAL METHOD CANNOT STAND IN FOR AN ARBITRARY MISSING ONE -- THE CALLER DECIDES HOW TO READ
		// THE RETURN VALUE.
		//
		// This used to hand back the neutral method, and that is unsound for a reason no choice of
		// neutral method can fix. All this prefix knows is a class and a token; the SIGNATURE belongs to
		// the generated proxy doing the invoking, and it is never in scope here. So the caller reads
		// whatever comes back as its OWN declared return type. The neutral method is a static float
		// getter, so UnityEngine.Application.get_unityVersion -- which Il2CppInterop compiles as
		// `Il2CppStringToManaged(invoke(...))` -- took the boxed Single it returned, read it as an
		// Il2CppString, and dereferenced a length and a character buffer that were not there. Access
		// violation, during Load(), on the Unity 6 build. The comment on NoOpProxies claimed it "returns
		// a float that nobody will look at"; every reference-returning member looks at it.
		// Choosing a reference-returning neutral method just inverts the casualty: a member that unboxes
		// a primitive would then unbox null. No single method satisfies every signature, so none is
		// neutral.
		// Il2CppInterop's own missing-member stub is the one honest answer: invoking it raises a
		// CATCHABLE managed exception instead of corrupting the caller, the proxy's cctor still
		// completes, and every module already degrades on it -- which is exactly the behaviour the
		// comments on Prefix describe wanting. It is tried first, and the neutral method is kept only as
		// a last resort for a build where even the stub cannot be produced.
		private static bool Unresolvable(int token, ref IntPtr __result)
		{
			_thrown++;
			// clazz == IntPtr.Zero returns through this same prefix's first line, so this does not recurse.
			try
			{
				IntPtr stub = IL2CPP.GetIl2CppMethodByToken(IntPtr.Zero, token);
				if (stub != IntPtr.Zero) { __result = stub; return false; }
			}
			catch { }
			IntPtr n = NoOpMethod();
			if (n != IntPtr.Zero) { __result = n; return false; }
			return true;
		}

		internal static void EnsureTable()
		{
			if (_anchors != null) return;
			try { _anchors = LoadTable(); } catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[TokenShiftFix] table illisible : " + e.Message); }
		}

		internal static void Install()
		{
			try
			{
				EnsureTable();
				if (_anchors == null || _anchors.Count == 0)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[TokenShiftFix] table d'ancres absente — decalage de tokens NON corrige.");
					return;
				}

				// Re-bind the proxies BepInEx and the interop itself touched before this plugin ran,
				// while the lookup is still the stock one: their fields hold the +1 neighbours.
				int types = 0;
				foreach (var t in CoreTypes()) { Rebind(t); types++; }
				VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix] rebind du noyau : {types} types, {_seenFields} champs lus, {_byName} places par leur nom, {_rebound} corriges, {_neutralised} neutralises (forme incompatible), {_stubFields} stubs, {_noInfo} sans info de classe.");

				var target = typeof(IL2CPP).GetMethod("GetIl2CppMethodByToken", BindingFlags.Public | BindingFlags.Static);
				if (target == null) throw new MissingMethodException("IL2CPP.GetIl2CppMethodByToken");
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
					typeof(TokenShiftFix).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
				_armed = true;
				VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix] arme — {_anchors.Count} classes ancrees ; {_rebound} champs du noyau reboundes.");

				// The gameplay classes the modules actually call. If their delta is a single piece the
				// whole class is a clean shift and every method (obfuscated names included) is repaired;
				// if it is many pieces the class was method-REORDERED between builds and only anchored
				// methods can be trusted. This tells which world we are in without another probe.
				ProbePieces("Player", "VRC");
				ProbePieces("VRCPlayer", "");
				ProbePieces("PlayerManager", "VRC.SDKBase");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[TokenShiftFix] non installe : " + e.Message);
			}
		}

		// Prefix on IL2CPP.GetIl2CppMethodByToken(IntPtr clazz, int token). False = answered here.
		private static bool Prefix(IntPtr clazz, int token, ref IntPtr __result)
		{
			if (clazz == IntPtr.Zero) return true;
			// A class this build no longer has (MissingTypeGuard stood System.Object in for it) does
			// not own the token being asked for. The stock lookup would then match that token against
			// System.Object and hand back SOME unrelated method, whose signature will not match what
			// the proxy invokes -- an access violation the moment a module calls it (VRC.Player's
			// prop_Player_0 getter is the one that took the whole game down).
			//
			// Resolving with a null class instead makes Il2CppInterop return its own "method not found"
			// stub, which throws a catchable managed exception when invoked. The module's try/catch
			// then degrades to "this feature is unavailable" instead of ending the process.
			// NEVER THROW HERE. Throwing fails the proxy's TYPE INITIALIZER, and .NET caches that
			// failure for the life of the process: one member it could not place condemns the entire
			// type, and with it every feature that touches it. That is what took out the per-user menu,
			// the Photon guard, the archive favourites and the force-clone path all at once.
			//
			// Resolving with a null class instead returns Il2CppInterop's own "missing member" stub. The
			// cctor COMPLETES, the type stays usable for casts and for the members that do resolve, and
			// only a call to the member that is genuinely gone raises -- as a catchable managed
			// exception, which every module already handles.
			if (MissingTypeGuard.IsPlaceholder(clazz))
			{
				return Unresolvable(token, ref __result);
			}
			ClassInfo ci;
			try { ci = Info(clazz); } catch { return true; }
			if (ci == null) return true;
			// NO ASSEMBLY-WIDE FALLBACK. It was tried and it is the dangerous one: Assembly-CSharp's
			// method tokens are PERMUTED, not shifted, so a "majority" delta taken from the few classes
			// that happen to look uniform points at a real but UNRELATED method of the right class --
			// and calling that with the caller's signature is an access violation. HighlightsFX's static
			// getter died exactly there. A class is trusted only when its own anchors, or its own method
			// block, say so.
			if (ci.Ok)
			{
				int d = ci.DeltaFor(token);
				// EVERY TRANSLATION IS CHECKED AGAINST THE MEMBER'S RECORDED SHAPE. A shift is measured
				// from a class's plain-named methods, and a member that moved on its own then lands on a
				// real method of the right class with the wrong signature -- which is an access
				// violation on the first call, not a wrong answer.
				if (ci.Live.TryGetValue(token + d, out var mi) && MemberAlign.ShapeOk(clazz, MemberAlign.KeyOf(clazz), token, mi)
					&& MemberAlign.Callable(mi))
				{ __result = mi; _fixed++; return false; }
				// The class is placed but this particular member is not where the shift says. Its own
				// SHAPE can still find it, and that is a better answer than the 1886 token, which on this
				// build names a different method of the same class -- called with the caller's signature,
				// that is an access violation rather than a wrong answer.
				if (MemberAlign.TryMethod(clazz, MemberAlign.KeyOf(clazz), token, out IntPtr late) && MemberAlign.Callable(late)) { __result = late; _aligned++; return false; }
				_unresolved++;
				if (_unresolved <= 25)
					VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] {ci.Key} : token 0x{token:X8} (+{d}) sans methode vivante — membre neutralise.");
				return Unresolvable(token, ref __result);
			}

			// NO USABLE ANCHOR. The member's own shape is the last honest way to place it: arity,
			// staticness and the kind of every parameter survive a rename, so lining the class's whole
			// member sequence up against the table finds it without trusting a single name or token.
			// This is what gives a RECOVERED class its members back -- the glow of HighlightsFX, the
			// fields of VRC.Player, everything that used to be identifiable but not callable.
			if (MemberAlign.TryMethod(clazz, MemberAlign.KeyOf(clazz), token, out IntPtr shaped) && MemberAlign.Callable(shaped)) { __result = shaped; _aligned++; return false; }

			if (ObfuscatedClassFinder.IsRecovered(clazz))
			{
				// A class recovered under a new name: its 1886 tokens mean NOTHING here, and letting the
				// stock lookup try them is worse than useless -- tokens are permuted, so one of them
				// occasionally does land on some other method of this very class, and calling that with
				// the caller's signature is an access violation (VRC.Player's prop_Player_0 getter, over
				// and over).
				//
				// So every member of a recovered class resolves to the missing-member stub. What the
				// recovery buys is the TYPE: casts, Il2CppType.From and type identity work again, which
				// is what the nameplate tags actually need — and a call to a member raises something
				// catchable instead of ending the process.
				return Unresolvable(token, ref __result);
			}
			else
			{
				// No anchor and no assembly shift: the stock answer could be some other method of the
				// same class, and calling THAT with the wrong signature is an access violation. The
				// missing-member stub is the safe answer -- same reasoning as above, and it keeps the
				// type alive instead of poisoning its initializer.
				_thrown++;
				if (_thrown <= 25) VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] {ci.Key} ({ci.Image}) : aucune ancre exploitable — membre neutralise (le type reste utilisable).");
				return Unresolvable(token, ref __result);
			}
		}

		private static unsafe ClassInfo Info(IntPtr clazz)
		{
			if (_classes.TryGetValue(clazz, out var ci)) return ci;
			ci = new ClassInfo();
			_classes[clazz] = ci;

			var kw = UnityVersionHandler.Wrap((Il2CppClass*)clazz);
			string ns = Str(kw.Namespace), name = Str(kw.Name), decl = "";
			var dt = kw.DeclaringType;
			if (dt != null) { var dw = UnityVersionHandler.Wrap(dt); decl = Str(dw.Name); if (ns.Length == 0) ns = Str(dw.Namespace); }
			ci.Key = ns + "|" + name + "|" + decl;
			try { var img = kw.Image; if (img != null) ci.Image = Str(UnityVersionHandler.Wrap(img).Name); } catch { }

			// live table: token -> MethodInfo, plus (name, params, static) for anchoring
			var byName = new Dictionary<string, List<(IntPtr mi, int tok, int pc, bool st)>>(StringComparer.Ordinal);
			IntPtr iter = IntPtr.Zero, m;
			int guard = 0;
			while ((m = IL2CPP.il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero && guard++ < 4096)
			{
				var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m);
				int tok = (int)mw.Token;
				ci.Live[tok] = m;
				string mn = Str(mw.Name);
				if (!byName.TryGetValue(mn, out var l)) byName[mn] = l = new List<(IntPtr, int, int, bool)>();
				l.Add((m, tok, mw.ParametersCount, ((ushort)mw.Flags & METHOD_ATTRIBUTE_STATIC) != 0));
			}

			// The positional fallback is separated out first: it is the answer for types the name rule
			// cannot anchor, and it must not be mistaken for one.
			Anchor positional = default;
			bool hasPositional = false;

			if (!_anchors.TryGetValue(ci.Key, out var anchors))
			{
				_noAnchor++;
				if (_noAnchor <= 12) VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] {ci.Key} : aucune ancre dans la table ({ci.Live.Count} methodes vivantes).");
				return ci;
			}
			var deltas = new List<KeyValuePair<int, int>>();
			int seenName = 0;
			foreach (var a in anchors)
			{
				if (a.Name == "*POS*") { positional = a; hasPositional = true; continue; }
				if (!byName.TryGetValue(a.Name, out var cands)) continue;
				seenName++;
				IntPtr hit = IntPtr.Zero; int hitTok = 0, n = 0;
				foreach (var c in cands)
					if (c.pc == a.Params && c.st == a.Static) { hit = c.mi; hitTok = c.tok; n++; }
				if (n != 1) continue;
				deltas.Add(new KeyValuePair<int, int>(a.Token, hitTok - a.Token));
			}
			// No name anchored: fall back on the block shift, but only when the method count still
			// matches -- a changed count means the type itself changed and no single shift is right.
			if (deltas.Count == 0 && hasPositional && positional.Params == ci.Live.Count && ci.Live.Count > 0)
			{
				int minLive = int.MaxValue;
				foreach (var t in ci.Live.Keys) if (t < minLive) minLive = t;
				deltas.Add(new KeyValuePair<int, int>(positional.Token, minLive - positional.Token));
				_positional++;
			}

			if (deltas.Count == 0)
			{
				_noAnchor++;
				if (_noAnchor <= 12)
				{
					// Say WHY, with one concrete anchor, so a wrong flag or arity check is visible.
					string sample = "";
					foreach (var a in anchors)
					{
						if (!byName.TryGetValue(a.Name, out var cands)) continue;
						var c = cands[0];
						sample = $" ex: '{a.Name}' table(params={a.Params},static={a.Static}) vivant(params={c.pc},static={c.st},candidats={cands.Count})";
						break;
					}
					VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] {ci.Key} : {anchors.Count} ancres, {seenName} noms retrouves, 0 exploitable.{sample}");
				}
				return ci;
			}
			deltas.Sort((x, y) => x.Key.CompareTo(y.Key));
			// collapse runs of equal delta; one piece = a uniform shift for the whole class
			foreach (var d in deltas)
				if (ci.Pieces.Count == 0 || ci.Pieces[ci.Pieces.Count - 1].Value != d.Value) ci.Pieces.Add(d);
			if (ci.Pieces.Count == 1) _uniform++; else _piecewise++;
			ci.Ok = true;
			// A uniformly shifted class is a vote for its assembly's shift, which classes with no
			// plain-text name of their own (fully obfuscated ones) then inherit.
			if (ci.Pieces.Count == 1 && ci.Image.Length > 0)
			{
				lock (_imageVotes)
				{
					if (!_imageVotes.TryGetValue(ci.Image, out var votes)) _imageVotes[ci.Image] = votes = new Dictionary<int, int>();
					votes.TryGetValue(ci.Pieces[0].Value, out int n); votes[ci.Pieces[0].Value] = n + 1;
				}
			}
			if (_uniform + _piecewise <= 40)
			{
				var sb = new System.Text.StringBuilder();
				foreach (var pc in ci.Pieces) sb.Append($" 0x{pc.Key:X8}:{pc.Value:+#;-#;0}");
				VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix] {ci.Key} : {deltas.Count} ancres exploitables / {anchors.Count}, {ci.Live.Count} vivantes, pieces:{sb}");
			}
			return ci;
		}

		private static void Note(Dictionary<FieldInfo, MethodBase> map, MethodBase mb)
		{
			FieldInfo fi = null;
			try { fi = ProxyGuard.NativeSlot(mb); } catch { }
			if (fi != null && !map.ContainsKey(fi)) map[fi] = mb;
		}

		// Rewrite the cached method pointers of a proxy type that was initialised with the stock lookup.
		private static unsafe void Rebind(Type proxy)
		{
			// THE OWNING CLASS COMES FROM THE PROXY, NOT FROM THE POINTER BEING REPAIRED.
			//
			// Every slot used to take its class from `cur` -- the very pointer this pass exists to
			// correct. When the stock lookup left a "missing method" stub there, its Class is null, so
			// the field was written off as unfixable and skipped: that is what the 1331 "stubs" were,
			// more than half of the core. And when the stock lookup landed on a method of a DIFFERENT
			// class, the repair went looking in that stranger's member list. Either way the one thing
			// that was never in doubt -- which type the field belongs to, since it is a static field OF
			// that type -- was the thing being inferred from the damage.
			// Il2CppInterop records it directly, so it is read once per type and used for every slot.
			IntPtr owner = IntPtr.Zero;
			try { owner = Il2CppClassPointerStore.GetNativeClassPointer(proxy); } catch { }
			if (owner != IntPtr.Zero && MissingTypeGuard.IsPlaceholder(owner)) owner = IntPtr.Zero;

			// WHICH PROXY METHOD EACH POINTER FIELD BELONGS TO.
			//
			// A name identifies a member only while the class carries it once. UnityEngine.Resources has
			// several FindObjectsOfTypeAll, so the name index refuses them all -- correctly, it cannot
			// choose -- and they fell through to the shape route, which picked the wrong one and turned
			// the launchpad's menu scan into an access violation. The proxy METHOD knows its own
			// parameter types, which is exactly what separates overloads; ProxyGuard already recovers
			// the field a method is bound to by reading its IL, so inverting that map gives each field
			// back its method.
			var ownerOf = new Dictionary<FieldInfo, MethodBase>();
			try
			{
				const BindingFlags All = BindingFlags.Instance | BindingFlags.Static
					| BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
				foreach (MethodBase mb in proxy.GetMethods(All)) Note(ownerOf, mb);
				foreach (MethodBase mb in proxy.GetConstructors(All)) Note(ownerOf, mb);
			}
			catch { }

			if (proxy.Name == "GUIStyle" && owner != IntPtr.Zero)
				VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] LIVE {proxy.Name}: {MemberAlign.DumpNames(owner)}");
			int n0 = _byName, r0 = _rebound, u0 = _neutralised, s0 = _stubFields, i0 = _noInfo, f0 = _seenFields;
			foreach (var f in proxy.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (!f.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal) || f.FieldType != typeof(IntPtr)) continue;
				IntPtr cur;
				try { cur = (IntPtr)f.GetValue(null); } catch { continue; }
				_seenFields++;

				// The name route needs nothing from the broken pointer, so it runs first and works even
				// on a stub or an empty slot. When a name is shared by several members it declines, and
				// the proxy's own parameter types settle which one this field is.
				ownerOf.TryGetValue(f, out MethodBase mb1);
				IntPtr named = owner != IntPtr.Zero ? MemberAlign.ResolveByFieldName(owner, f.Name, mb1) : IntPtr.Zero;
				if (named == IntPtr.Zero && mb1 != null)
				{
					try { named = MemberAlign.ResolveByProxySignature(mb1); } catch { }
				}
				if (named != IntPtr.Zero) { Commit(proxy, f, mb1, cur, named, 0, true); continue; }

				if (cur == IntPtr.Zero) continue;   // nothing to read a token from, and no name to go on
				try
				{
					var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)cur);
					IntPtr clazz = (IntPtr)mw.Class;
					if (clazz == IntPtr.Zero)
					{
						_stubFields++;
						if (proxy.Name == "Component") VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix]   STUB {proxy.Name}.{f.Name} (proxy connu: {mb1 != null})");
						continue;   // a fabricated "missing method" stub
					}
					int tok = (int)mw.Token;              // == the 1886 token the stock lookup matched on
					var ci = Info(clazz);
					if (ci == null || !ci.Ok) { _noInfo++; continue; }
					int d = ci.DeltaFor(tok);
					bool has = ci.Live.TryGetValue(tok + d, out var right);
					if (d != 0 && _shown++ < 12)
						VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix]   rebind {proxy.Name}.{f.Name}: cur=0x{cur.ToString("X")} tok=0x{tok:X8} d={d} cible={(has ? "0x" + right.ToString("X") : "aucune")}{(has && right == cur ? " (deja bon)" : "")}");

					// A SHIFT OF ZERO IS A MEASUREMENT, NOT A GUARANTEE.
					//
					// This used to `continue` on d == 0, taking "the class did not shift" to mean "this
					// slot is already right" -- the one path in the whole fix that wrote nothing and
					// checked nothing. It holds across VRChat builds, where a class keeps its member
					// order and only gains or loses types around it. It does NOT hold across an ENGINE
					// change: on Unity 6 the UnityEngine core types kept their names and tokens while
					// their member order moved underneath, so the shift honestly measured zero and the
					// stock 1903 pointer stayed in place, aimed at a different method of the right class.
					// UnityEngine.Application.get_unityVersion is one of those -- invoked as a string
					// getter, it returned something that was not a string and the dereference ended the
					// process during Load().
					// Prefix has always verified its answer with ShapeOk + Callable before handing it
					// back; Rebind now holds every slot to that same standard, whatever the shift says.
					// ShapeOk only disagrees when it HAS a recorded shape and the candidate contradicts
					// it -- with nothing on record it returns true -- so this rejects wrong methods
					// without touching the ones it cannot judge.
					IntPtr target = has ? right : cur;
					string key = MemberAlign.KeyOf(clazz);

					bool ok = MemberAlign.ShapeOk(clazz, key, tok, target) && MemberAlign.Callable(target);
					if (!ok && MemberAlign.TryMethod(clazz, key, tok, out IntPtr late)
						&& MemberAlign.ShapeOk(clazz, key, tok, late) && MemberAlign.Callable(late))
					{ target = late; ok = true; _aligned++; }
					if (!ok) { Neutralise(proxy, f, cur, tok, "aucun membre de la classe ne correspond a la forme enregistree"); continue; }
					Commit(proxy, f, mb1, cur, target, tok, false);
				}
				catch { }
			}
			VRChatArchiveModPlugin.Logger.LogInfo($"[TokenShiftFix]   {proxy.Name} (0x{owner.ToString("X")}) : {_seenFields - f0} champs, "
				+ $"{_byName - n0} par nom/signature, {_rebound - r0} par decalage, {_neutralised - u0} neutralises, "
				+ $"{_stubFields - s0} stubs laisses, {_noInfo - i0} sans info");
		}

		// ONE GATE FOR EVERY REPAIRED SLOT.
		//
		// Four routes can produce a pointer here -- the name, the proxy signature, the measured shift,
		// the recorded shape -- and each was trusted on its own say-so. They do not deserve equal trust,
		// and the weaker ones were writing pointers that only an invocation would disprove -- at which
		// point it is an access violation rather than a wrong answer. Whatever route chose it, the
		// pointer must agree with the signature the proxy DECLARES: same arity, same staticness, and
		// every slot whose type can be named. One pass over a method's parameters buys the difference
		// between a broken feature and a dead process.
		private static void Commit(Type proxy, FieldInfo f, MethodBase mb, IntPtr cur, IntPtr chosen, int tok, bool byName)
		{
			if (chosen == IntPtr.Zero) return;
			if (mb != null && !MemberAlign.SlotMatchesProxy(chosen, mb))
			{
				Neutralise(proxy, f, cur, tok, "le pointeur retenu ne correspond pas a la signature du proxy");
				return;
			}
			if (chosen == cur) { if (byName) _byName++; return; }
			if (!Write(proxy, f, chosen)) return;
			if (byName) _byName++; else _rebound++;
		}

		// A slot that cannot be made to agree is neutralised rather than left aimed at a stranger: the
		// interop's own missing-member stub raises a CATCHABLE exception when invoked, which every
		// module already degrades on.
		private static void Neutralise(Type proxy, FieldInfo f, IntPtr cur, int tok, string why)
		{
			IntPtr stub = IntPtr.Zero;
			try { stub = IL2CPP.GetIl2CppMethodByToken(IntPtr.Zero, tok != 0 ? tok : 0x06000001); } catch { }
			if (stub == IntPtr.Zero || stub == cur) return;   // cannot do better than leave it alone
			if (_badRoute++ < 20)
				VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] {proxy.Name}.{f.Name} : {why} — neutralise.");
			if (Write(proxy, f, stub)) _neutralised++;
		}

		// The generated fields are static readonly; .NET refuses FieldInfo.SetValue on an initonly
		// static once the type is initialised (silently, from inside a catch). A ref obtained through
		// ldsflda has no such rule.
		private static bool Write(Type proxy, FieldInfo f, IntPtr value)
		{
			try
			{
				ref IntPtr slot = ref AccessTools.StaticFieldRefAccess<IntPtr>(f)();
				slot = value;
				return true;
			}
			catch (Exception e)
			{
				if (_writeFail++ < 3) VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] ecriture de {proxy.Name}.{f.Name} refusee : {e.GetType().Name} {e.Message}");
				return false;
			}
		}

		private static IEnumerable<Type> CoreTypes()
		{
			// What BepInEx, Il2CppInterop and this plugin's own load path touch before the prefix
			// exists. Direct typeof references: the interop assemblies are loaded through BepInEx's
			// resolver, which Type.GetType(name) does not consult, so names resolved to nothing.
			return new[]
			{
				typeof(Il2CppSystem.Object), typeof(Il2CppSystem.String), typeof(Il2CppSystem.Type),
				typeof(Il2CppSystem.Delegate), typeof(Il2CppSystem.Exception), typeof(Il2CppSystem.Array),
				typeof(Il2CppSystem.Reflection.MemberInfo), typeof(Il2CppSystem.RuntimeType),
				// Il2CppInterop calls MakeGenericMethod through these to build every generic method it
				// proxies, inside a static initializer -- so one wrong pointer here kills the process
				// before the generic member is ever used.
				typeof(Il2CppSystem.Reflection.MethodInfo), typeof(Il2CppSystem.Reflection.MethodBase),
				typeof(Il2CppSystem.Reflection.PropertyInfo), typeof(Il2CppSystem.Reflection.FieldInfo),
				typeof(Il2CppSystem.Reflection.ConstructorInfo), typeof(Il2CppSystem.Reflection.Assembly),
				typeof(UnityEngine.Object), typeof(UnityEngine.GameObject), typeof(UnityEngine.Component),
				typeof(UnityEngine.Behaviour), typeof(UnityEngine.MonoBehaviour), typeof(UnityEngine.Transform),
				typeof(UnityEngine.Application), typeof(UnityEngine.Debug), typeof(UnityEngine.Time),
				typeof(UnityEngine.Resources), typeof(UnityEngine.SceneManagement.SceneManager),
				typeof(UnityEngine.Camera), typeof(UnityEngine.Material), typeof(UnityEngine.Renderer),
				typeof(UnityEngine.Texture2D), typeof(UnityEngine.Canvas), typeof(UnityEngine.EventSystems.EventSystem),
				typeof(UnityEngine.GUIUtility), typeof(UnityEngine.Event), typeof(UnityEngine.GUI),
				// THE ENGINE'S VALUE TYPES AND STATIC HELPERS BELONG HERE TOO.
				//
				// The list above was drawn up for what loads BEFORE the prefix exists, and that is a
				// smaller set than what breaks across an ENGINE change. Every type below is used all
				// over the mod and none of them was being repaired, so each one crashed the first
				// module to touch it, one build at a time: Vector3's constructor took down the gravity
				// module during RegisterModules. They are engine types, so the name route places them
				// outright -- adding them costs one cached name index each.
				typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector4),
				typeof(UnityEngine.Quaternion), typeof(UnityEngine.Color), typeof(UnityEngine.Color32),
				typeof(UnityEngine.Rect), typeof(UnityEngine.Bounds), typeof(UnityEngine.Matrix4x4),
				typeof(UnityEngine.Mathf), typeof(UnityEngine.Input), typeof(UnityEngine.Screen),
				typeof(UnityEngine.Physics), typeof(UnityEngine.LayerMask), typeof(UnityEngine.Random),
				typeof(UnityEngine.Shader), typeof(UnityEngine.Texture), typeof(UnityEngine.Sprite),
				typeof(UnityEngine.Graphics), typeof(UnityEngine.RectTransform), typeof(UnityEngine.CanvasGroup),
				typeof(UnityEngine.Animator), typeof(UnityEngine.Rigidbody), typeof(UnityEngine.AudioSource),
				typeof(UnityEngine.LineRenderer), typeof(UnityEngine.MeshRenderer),
				typeof(UnityEngine.SkinnedMeshRenderer), typeof(UnityEngine.Light),
				typeof(UnityEngine.Mesh), typeof(UnityEngine.Collider), typeof(UnityEngine.Collision),
				typeof(UnityEngine.Keyframe), typeof(UnityEngine.AnimationCurve), typeof(UnityEngine.Gradient),
				typeof(UnityEngine.SceneManagement.Scene), typeof(UnityEngine.Ray), typeof(UnityEngine.Plane),
				typeof(UnityEngine.Vector2Int), typeof(UnityEngine.Vector3Int), typeof(UnityEngine.RenderTexture),
				typeof(UnityEngine.Cursor), typeof(UnityEngine.GUIStyle), typeof(UnityEngine.GUIContent),
				typeof(UnityEngine.GUILayout), typeof(UnityEngine.Texture2D), typeof(UnityEngine.RaycastHit),
				typeof(UnityEngine.Font), typeof(UnityEngine.TextAsset), typeof(UnityEngine.AudioClip),
				typeof(UnityEngine.AnimationClip), typeof(UnityEngine.Avatar), typeof(UnityEngine.MaterialPropertyBlock),
			};
		}

		private static readonly string[] _asmGuesses =
		{
			"Assembly-CSharp.dll", "VRCCore-Standalone.dll", "VRC.SDKBase.dll", "VRC.SDK3.dll", "VRCSDKBase.dll",
		};

		private static void ProbePieces(string cls, string ns)
		{
			foreach (var asm in _asmGuesses)
			{
				IntPtr k;
				try { k = IL2CPP.GetIl2CppClass(asm, ns, cls); } catch { continue; }
				if (k == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(k)) continue;
				ClassInfo ci;
				try { ci = Info(k); } catch { continue; }
				string state = ci.Ok ? (ci.Pieces.Count == 1 ? $"DECALAGE UNIFORME ({ci.Pieces[0].Value:+#;-#;0})" : $"REORDONNE — {ci.Pieces.Count} morceaux") : "sans ancre";
				VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] classe jouable {ns}.{cls} @ {asm} : {ci.Live.Count} methodes, {state}");
				return;
			}
			VRChatArchiveModPlugin.Logger.LogWarning($"[TokenShiftFix] classe jouable {ns}.{cls} : introuvable dans les assemblys testes");
		}

		private static Dictionary<string, List<Anchor>> LoadTable()
		{
			using var raw = typeof(TokenShiftFix).Assembly.GetManifestResourceStream("tokens-1886.tsv.gz");
			if (raw == null) return null;
			using var gz = new GZipStream(raw, CompressionMode.Decompress);
			using var r = new StreamReader(gz);
			var table = new Dictionary<string, List<Anchor>>(StringComparer.Ordinal);
			MemberSpec lastSpec = null; string lastStamp = null;
			string line;
			while ((line = r.ReadLine()) != null)
			{
				var c = line.Split('\t');
				if (c.Length < 7) continue;
				// Il2CppInterop prefixes the namespaces that would clash with the BCL (Il2CppSystem.*,
				// Il2CppNewtonsoft.*); il2cpp itself reports them bare, and the key must match the live side.
				string ns = c[0];
				if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal) && ns.Length > 6 && char.IsUpper(ns[6])) ns = ns.Substring(6);
				string key = ns + "|" + c[1] + "|" + c[2];
				if (!table.TryGetValue(key, out var l)) table[key] = l = new List<Anchor>();
				l.Add(new Anchor
				{
					Token = Convert.ToInt32(c[3], 16), Name = c[4],
					Params = int.Parse(c[5]), Static = c[6] == "1",
					Fields = c[4] == "*POS*" ? int.Parse(c[6]) : 0,
					Parent = c.Length >= 9 ? c[8] : "",
					Ifaces = c.Length >= 10 && int.TryParse(c[9], out int ic) ? ic : -1,
					Attrs = c.Length >= 11 && int.TryParse(c[10], out int at) ? at : -1,
				});
				if (c[4] == "*POS*" && c.Length >= 8)
					try { _typeTokens[key] = Convert.ToUInt32(c[7], 16); } catch { }
				if (c[4] == "*POS*" && c.Length >= 12)
				{
					string mS = c[11], fS = c.Length >= 13 ? c[12] : "";
					if (mS.Length > 0 || fS.Length > 0)
					{
						string stamp = c[3] + "/" + c[7];
						if (lastSpec == null || lastStamp != stamp)
						{
							try { lastSpec = new MemberSpec { MinToken = Convert.ToInt32(c[3], 16), M = mS, F = fS }; }
							catch { lastSpec = null; }
							lastStamp = stamp;
						}
						if (lastSpec != null) _members[key] = lastSpec;
					}
				}
			}
			return table;
		}

		private static string Str(IntPtr p)
		{
			if (p == IntPtr.Zero) return "";
			try { return Marshal.PtrToStringAnsi(p) ?? ""; } catch { return ""; }
		}
	}
}
