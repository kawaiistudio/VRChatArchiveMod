using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace VRChatArchiveMod.Core
{
	// RECOVER A VRCHAT CLASS THAT WAS RENAMED BY THE BUILD'S OBFUSCATION.
	//
	// VRChat obfuscates its own class names and rotates them on every update. The interop assemblies
	// were generated against 1886, so on 1903 a name lookup for VRC.Player, VRCNetworkingClient,
	// PortalInternal or SelectedUserMenuQM finds nothing -- and everything built on them goes dark:
	// the nameplate tags, the Photon guard, the network log, portal ESP, the per-user menu.
	//
	// The classes did not disappear, only their names did. And class tokens move the same way method
	// tokens do: an assembly that gained or lost a type early shifts every later TypeDef token by a
	// constant. So the shift is measured, not guessed -- from the classes of that same assembly whose
	// names ARE plain text and therefore still resolve. Applying it to the recorded 1886 token of the
	// missing class gives the live class directly.
	//
	// Reading the live side needs two il2cpp entry points the pack does not bind (they still name the
	// standard symbol, which this binary does not export). Rather than repatch the pack -- binding
	// every corrected export at once was tried and broke startup -- they are imported here under the
	// obfuscated names proven for this build, so nothing outside the mod changes.
	internal static class ObfuscatedClassFinder
	{
		// THE EXPORT NAMES MOVE WITH EVERY BUILD, SO THEY ARE BOUND, NOT DECLARED.
		//
		// These two entry points are imported under the obfuscated names of the build, and VRChat
		// rotates those on every update: a [DllImport] pinned to one build throws
		// EntryPointNotFoundException on every other one, at the first call, with the finder already
		// believing itself usable. Unity 6 (pack v17) and Unity 2022 (pack v16) are both in the wild,
		// so the known sets are tried in order and the first that binds wins. A pack with neither
		// simply leaves the finder disabled -- the same outcome Map() already handles -- instead of
		// taking the renamed-class recovery down with an exception.
		// Read-only enumeration, nothing else.
		private static readonly string[][] _exportSets =
		{
			// Unity 6 (6000.0.67 / build 25686233), from the il2cpp API remap (header 6000.0.67 + Cecil).
			new[] { "BxIcjPCoBQd", "YyANpIPvkWF" },
			// Unity 2022.3.22 (pack v16 and earlier).
			new[] { "WRrMuHZiPoB", "QNJWHhkkxMm" },
		};

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint ImageClassCountFn(IntPtr image);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr ImageClassFn(IntPtr image, uint index);

		private static ImageClassCountFn ImageGetClassCount;
		private static ImageClassFn ImageGetClass;
		private static bool _bound;

		private static bool Bind()
		{
			if (_bound) return ImageGetClassCount != null;
			_bound = true;
			IntPtr lib;
			if (!NativeLibrary.TryLoad("GameAssembly", typeof(ObfuscatedClassFinder).Assembly, null, out lib)
				&& !NativeLibrary.TryLoad("GameAssembly.dll", out lib))
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[ObfClassFinder] GameAssembly introuvable — les classes renommees resteront introuvables.");
				return false;
			}
			foreach (var set in _exportSets)
			{
				if (!NativeLibrary.TryGetExport(lib, set[0], out IntPtr pCount)) continue;
				if (!NativeLibrary.TryGetExport(lib, set[1], out IntPtr pClass)) continue;
				ImageGetClassCount = Marshal.GetDelegateForFunctionPointer<ImageClassCountFn>(pCount);
				ImageGetClass = Marshal.GetDelegateForFunctionPointer<ImageClassFn>(pClass);
				VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] enumeration liee sur '{set[0]}' / '{set[1]}'.");
				return true;
			}
			VRChatArchiveModPlugin.Logger.LogWarning("[ObfClassFinder] aucun jeu de noms d'export connu ne correspond a ce build — les classes renommees resteront introuvables.");
			return false;
		}

		private struct Entry { public IntPtr K; public int M; public int F; public int I; public int A; public string Parent; public IntPtr ParentK; }

		private sealed class ImageMap { public List<Entry> All = new List<Entry>(); }

		private static readonly Dictionary<string, ImageMap> _images = new Dictionary<string, ImageMap>(StringComparer.Ordinal);
		private static bool _usable = true;
		private static int _found, _failed, _quiet, _depth;
		// When set, Resolve SKIPS its by-name shortcut and matches on SHAPE only. Needed for
		// obfuscated type names that VRChat REASSIGNED between builds: the 1886 name still exists on
		// 1903 but points at a different class, so "found by name" returns a stranger. The avatar
		// category panel is one of these.
		[ThreadStatic] private static bool _forceShape;

		internal static IntPtr ResolveByShape(string assemblyName, string ns, string name)
		{
			bool was = _forceShape; _forceShape = true;
			try { return Resolve(assemblyName, ns, name); }
			finally { _forceShape = was; }
		}

		// Classes recovered under a new name. Their members cannot be looked up by the 1886 names the
		// interop holds, so TokenShiftFix treats them leniently instead of condemning the type: a
		// member it cannot place resolves to Il2CppInterop's missing-member stub, which throws
		// something a module can catch, rather than poisoning the whole type initializer.
		private static readonly HashSet<IntPtr> _recovered = new HashSet<IntPtr>();
		// Every live class of an image, for callers that need to search the assembly by SHAPE rather
		// than by name -- the preview reversing walks this to find what builds an avatar model. The map is
		// the one this finder already builds and caches, so a second caller costs nothing.
		internal static List<IntPtr> LiveClasses(string assemblyName)
		{
			var r = new List<IntPtr>();
			try
			{
				IntPtr img = ImageByName(assemblyName);
				if (img == IntPtr.Zero) return r;
				var m = Map(assemblyName, img);
				foreach (var e in m.All) r.Add(e.K);
			}
			catch { }
			return r;
		}

		internal static bool IsRecovered(IntPtr klass) => klass != IntPtr.Zero && _recovered.Contains(klass);

		// THE NAME THE TABLE KNOWS A RECOVERED CLASS UNDER.
		//
		// Once recovered, the class answers to its NEW obfuscated name, so looking its members up by
		// what il2cpp now calls it finds nothing. MemberAlign needs the 1886 key the recovery started
		// from -- that is the only row that carries the shape of its members.
		private static readonly Dictionary<IntPtr, string> _recoveredKey = new Dictionary<IntPtr, string>();
		internal static string RecoveredKey(IntPtr klass)
		{
			if (klass == IntPtr.Zero) return null;
			lock (_recoveredKey) { return _recoveredKey.TryGetValue(klass, out string k) ? k : null; }
		}

		internal static int Found => _found;
		internal static int Failed => _failed;
		internal static bool Usable => _usable;

		// Every live class of one image, indexed by shape.
		//
		// Class tokens were the first idea and they do NOT work: measured on this build, 1839 classes
		// of VRCCore-Standalone that still resolve by name vote for no common shift at all, because
		// the obfuscator PERMUTES type tokens rather than shifting them the way it shifts method
		// tokens. The shape survives that: renaming a class does not change how many methods and
		// fields it has.
		private static unsafe ImageMap Map(string imageName, IntPtr image)
		{
			if (_images.TryGetValue(imageName, out var m)) return m;
			m = new ImageMap();
			_images[imageName] = m;

			if (!Bind()) { _usable = false; return m; }

			uint n;
			try { n = ImageGetClassCount(image); }
			catch (Exception e)
			{
				_usable = false;
				VRChatArchiveModPlugin.Logger.LogWarning("[ObfClassFinder] enumeration des classes indisponible (" + e.GetType().Name + ") — les classes renommees resteront introuvables.");
				return m;
			}
			if (n == 0 || n > 200000) return m;

			for (uint i = 0; i < n; i++)
			{
				IntPtr k;
				try { k = ImageGetClass(image, i); } catch { break; }
				if (k == IntPtr.Zero) continue;
				try
				{
					var kw = UnityVersionHandler.Wrap((Il2CppClass*)k);
					string parent = "";
					IntPtr parentK = IntPtr.Zero;
					var pp = kw.Parent;
					if (pp != null)
					{
						parentK = (IntPtr)pp;
						var pw = UnityVersionHandler.Wrap(pp);
						if (pw.Name != IntPtr.Zero) parent = Marshal.PtrToStringAnsi(pw.Name) ?? "";
					}
					m.All.Add(new Entry { K = k, M = kw.MethodCount, F = kw.FieldCount, I = kw.InterfaceCount, A = TypeAttrs(kw.Flags), Parent = parent, ParentK = parentK });
				}
				catch { }
			}
			VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] {imageName} : {m.All.Count} classes vivantes indexees (forme + parent).");
			return m;
		}

		// Il2CppInterop keeps its own image index but does not expose the lookup publicly in the
		// version this mod builds against, so it is reached by reflection rather than duplicated --
		// its index is the one GetIl2CppClass already resolves names through.
		private static System.Reflection.MethodInfo _getImage;
		private static IntPtr ImageByName(string assemblyName)
		{
			try
			{
				if (_getImage == null)
					_getImage = typeof(IL2CPP).GetMethod("GetIl2CppImage",
						System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
						null, new[] { typeof(string) }, null);
				if (_getImage == null) return IntPtr.Zero;
				return (IntPtr)_getImage.Invoke(null, new object[] { assemblyName });
			}
			catch { return IntPtr.Zero; }
		}

		// Interface / abstract / sealed, in the same three bits ECMA-335 uses, so the recorded value and
		// the live one are directly comparable. Cheap, and it separates most of the ties that shape
		// alone leaves behind.
		private const int T_SEALED = 0x100, T_ABSTRACT = 0x80, T_INTERFACE = 0x20;

		private static int TypeAttrs(Il2CppClassAttributes f)
		{
			int v = (int)f, r = 0;
			if ((v & T_INTERFACE) != 0) r |= T_INTERFACE;
			if ((v & T_ABSTRACT) != 0) r |= T_ABSTRACT;
			if ((v & T_SEALED) != 0) r |= T_SEALED;
			return r;
		}

		// Where a VRChat type lives when the assembly the caller named is not loaded on this build.
		private static readonly string[] _imageGuesses =
		{
			"Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "VRCCore-Standalone.dll", "VRCSDKBase.dll", "VRCSDK3.dll",
		};

		// null means "the assembly the derived class came from"; the rest are where a base type usually
		// lives when it is not there.
		private static readonly string[] _parentImages =
		{
			null, "UnityEngine.CoreModule.dll", "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll",
			"UnityEngine.UI.dll", "mscorlib.dll",
		};

		private static bool Plain(string n)
		{
			foreach (char c in n) if (c > 126 || c < 32) return false;
			return n.Length > 0;
		}

		// The live class of a BASE TYPE, found the same way as any other -- through the table, never
		// through GetIl2CppClass.
		//
		// A base is recorded by name alone, and that name may be plain (UIMenu), invented by
		// Il2CppInterop, or obfuscated. Asking il2cpp for it by name therefore fails exactly when it
		// matters, and asking through the guard made the finder report classes it was itself looking up.
		// The table knows the base's own shape, so it is recovered with the same measurement.
		private static IntPtr ResolveParent(string assemblyName, ImageMap m, string parentName)
		{
			// Present under its own name: nothing to recover. A base very often lives in ANOTHER
			// assembly than the class that derives from it -- MonoBehaviour is in UnityEngine.CoreModule,
			// not in Assembly-CSharp -- and asking only the derived class's assembly made the finder
			// hunt for MonoBehaviour by shape, over and over, and report it as unrecoverable.
			foreach (string asm in _parentImages)
			{
				try
				{
					// By name only, for the same reason as above: going through the guard would recover a
					// class by shape and present it as the base type we were looking for.
					IntPtr direct = MissingTypeGuard.LookupByNameOnly(asm ?? assemblyName, "", parentName);
					if (direct != IntPtr.Zero && !MissingTypeGuard.IsPlaceholder(direct)) return direct;
				}
				catch { }
			}

			if (!TokenShiftFix.KeyForClassName(parentName, out string key)) return IntPtr.Zero;
			if (!TokenShiftFix.RecordedShapeByKey(key, out int pm, out int pf, out string pp, out int pi, out int pa)) return IntPtr.Zero;

			Entry best = default; int bestD = int.MaxValue, secondD = int.MaxValue;
			foreach (var e in m.All)
			{
				if (pa >= 0 && e.A != pa) continue;
				int d = Math.Abs(e.M - pm) + Math.Abs(e.F - pf) + (pi >= 0 ? Math.Abs(e.I - pi) : 0);
				if (d < bestD) { secondD = bestD; best = e; bestD = d; }
				else if (d < secondD) secondD = d;
			}
			bool exactAlone = bestD == 0 && secondD > 0;
			if (bestD == int.MaxValue || bestD > 12 || (!exactAlone && secondD - bestD < 3)) return IntPtr.Zero;
			VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] base '{parentName}' retrouvee par sa forme ({pm}/{pf}/{pi}) -> 0x{best.K.ToString("X")}");
			return best.K;
		}

		// The live class for a type the interop knows only under its 1886 name.
		internal static IntPtr Resolve(string assemblyName, string ns, string name)
		{
			if (!_usable) return IntPtr.Zero;
			try
			{
				string full = (ns.Length > 0 ? ns + "." : "") + name;

				// THE INTEROP MAY FILE THE TYPE SOMEWHERE ELSE ENTIRELY.
				//
				// VRC.SDKBase.PlayerManager is the case that proved it. The mod asks for it under the
				// namespace and assembly the SDK documents, and the 1886 interop has it with NO namespace
				// at all, inside Assembly-CSharp -- so neither the shape lookup nor the image lookup could
				// ever hit, and it reported "absent de l'interop" for a type that is sitting right there.
				// Both are retried on the class's bare name, which the table only offers when it is
				// unambiguous across every assembly.
				string key = ns + "|" + name + "|";
				if (!TokenShiftFix.RecordedShapeByKey(key, out int methods, out int fields, out string parent, out int ifaces, out int attrs))
				{
					// Both of these used to return silently, which is how LoadBalancingClient disappeared
					// from the report entirely: no "retrouvee", no refusal, nothing to act on.
					if (!TokenShiftFix.KeyForClassName(name, out string alt)
						|| !TokenShiftFix.RecordedShapeByKey(alt, out methods, out fields, out parent, out ifaces, out attrs))
					{
						if (_depth == 0 && _quiet++ < 25) VRChatArchiveModPlugin.Logger.LogWarning($"[ObfClassFinder] {full} : aucune forme enregistree dans la table (type absent de l'interop 1886 ?).");
						return IntPtr.Zero;
					}
					key = alt;
					if (_depth == 0) VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] {full} : l'interop la classe sous '{alt.Replace('|', '.')}' — recherche sur cette forme.");
				}

				// A CLASS THAT STILL HAS ITS NAME DOES NOT NEED RECOVERING -- IT NEEDS THE RIGHT ASSEMBLY.
				//
				// Callers name the assembly they expect a type in, and they are not always right:
				// MonoBehaviour was asked for inside Assembly-CSharp, where it has never lived. The
				// search then hunted for it by shape in the wrong image, found two equally plausible
				// strangers, and refused -- a correct refusal to a question that should not have been
				// asked. Looking the name up in the assemblies it might actually be in settles it first.
				if (_depth == 0 && !_forceShape)
				{
					_depth++;
					try
					{
						foreach (string a in _parentImages)
						{
							if (a == null) continue;
							// BY NAME ONLY. Asking through the ordinary lookup re-enters the guard, which
							// recovers a class by SHAPE and hands it back -- so this probe concluded that
							// VRC.Player was "present under its own name" while looking at a stranger
							// recovered for a different image, and sixteen real recoveries were lost.
							IntPtr direct = MissingTypeGuard.LookupByNameOnly(a, ns, name);
							if (direct == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(direct)) continue;
							VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] {full} : presente sous son propre nom dans {a} — rien a recuperer.");
							return direct;
						}
					}
					finally { _depth--; }
				}

				IntPtr image = ImageByName(assemblyName);
				if (image == IntPtr.Zero)
					foreach (string a in _imageGuesses)
					{
						image = ImageByName(a);
						if (image != IntPtr.Zero) { assemblyName = a; break; }
					}
				if (image == IntPtr.Zero)
				{
					if (_quiet++ < 25) VRChatArchiveModPlugin.Logger.LogWarning($"[ObfClassFinder] {full} : l'image '{assemblyName}' n'est pas chargee dans il2cpp.");
					return IntPtr.Zero;
				}

				var m = Map(assemblyName, image);

				// The parent is the hard filter. An update adds and removes members, so the counts only
				// ever come CLOSE -- but a class keeps its base type, and for the ones that matter here
				// that base still has a readable name (VRCNetworkingClient derives from
				// LoadBalancingClient, VRC.Player from MonoBehaviour).
				// A parent name Il2CppInterop INVENTED (it names obfuscated types by their shape, and
				// always ends them in "Unique") exists nowhere in the live metadata, so filtering on it
				// matches nothing at all -- that is why VRCPlayer came back "aucun candidat". Only a real
				// type name is a usable filter; otherwise fall back to shape alone, which the ambiguity
				// check below still keeps honest.
				// Il2CppObjectBase is Il2CppInterop's OWN base for interfaces and value types; il2cpp has
				// no such class, so filtering on it matches nothing.
				bool parentUsable0 = parent.Length > 0 && Plain(parent)
					&& !parent.EndsWith("Unique", StringComparison.Ordinal)
					&& parent != "Il2CppObjectBase";
				bool parentUsable = parentUsable0;

				// THE PARENT MAY ITSELF HAVE BEEN RENAMED. VRCNetworkingClient derives from
				// LoadBalancingClient, and on this build that base carries an obfuscated name too — so
				// comparing names finds nothing. Resolve the base first and match on its CLASS POINTER
				// instead, which is immune to the rename. One level deep only: enough for a base that
				// was itself recovered, and it cannot loop.
				IntPtr wantParentK = IntPtr.Zero;
				if (parent.Length > 0 && parent != "Il2CppObjectBase" && _depth == 0)
				{
					_depth++;
					try { wantParentK = ResolveParent(assemblyName, m, parent); }
					catch { }
					finally { _depth--; }
				}
				if (wantParentK != IntPtr.Zero) parentUsable = false;   // the pointer is the better filter
				Entry best = default;
				int bestD = int.MaxValue, secondD = int.MaxValue, seen = 0;
				var near = new List<KeyValuePair<Entry, int>>();

				// THE PARENT FILTER IS AN AID, NOT A REQUIREMENT.
				//
				// A base type can be renamed too, and when it is, filtering on its 1886 name matches
				// nothing at all -- the search then reports "no candidate" for a class that is sitting
				// right there. HighlightsFX went dark exactly that way: its base, PostEffectsBase, is
				// itself obfuscated and too ambiguous to recover. So when the parent filter leaves
				// nothing, the search is redone on shape alone, where the ambiguity check still applies.
				for (int pass = 0; pass < 2; pass++)
				{
					bestD = secondD = int.MaxValue; seen = 0; near.Clear();
					foreach (var e in m.All)
					{
						if (attrs >= 0 && e.A != attrs) continue;   // interface/abstract/sealed must agree
						if (pass == 0)
						{
							if (wantParentK != IntPtr.Zero) { if (e.ParentK != wantParentK) continue; }
							else if (parentUsable && !string.Equals(e.Parent, parent, StringComparison.Ordinal)) continue;
						}
						seen++;
						int d = Math.Abs(e.M - methods) + Math.Abs(e.F - fields)
							+ (ifaces >= 0 ? Math.Abs(e.I - ifaces) : 0);
						if (d < bestD) { secondD = bestD; best = e; bestD = d; }
						else if (d < secondD) secondD = d;
						if (d <= 10 && near.Count < 96) near.Add(new KeyValuePair<Entry, int>(e, d));
					}
					if (seen > 0) break;                                     // the filter kept something
					if (wantParentK == IntPtr.Zero && !parentUsable) break;  // there was no filter to drop
					parentUsable = false; wantParentK = IntPtr.Zero;
				}

				// THE FINGERPRINT MUST SEE PAST THE PARENT FILTER.
				//
				// A base type can itself be recovered WRONGLY, and then the filter keeps only the children
				// of a stranger -- so the real class is never even considered, and the counting rule picks
				// whatever is left. HighlightsFX was recovered as an unrelated 19-method class that way,
				// sharing five of its nineteen signatures, and the ESP glow spent every session calling
				// into it. The member test is the reliable one, so it is given every plausible candidate
				// in the image, parent or no parent.
				var pool = new List<KeyValuePair<Entry, int>>(near);
				foreach (var e in m.All)
				{
					if (attrs >= 0 && e.A != attrs) continue;
					int d = Math.Abs(e.M - methods) + Math.Abs(e.F - fields)
						+ (ifaces >= 0 ? Math.Abs(e.I - ifaces) : 0);
					if (d > 10) continue;
					bool already = false;
					for (int q = 0; q < near.Count; q++) if (near[q].Key.K == e.K) { already = true; break; }
					if (already) continue;
					pool.Add(new KeyValuePair<Entry, int>(e, d));
				}
				// NEAREST FIRST, THEN CAP. Assembly-CSharp holds tens of thousands of classes and hundreds
				// of them sit within ten members of any given shape, so taking the first hundred and sixty
				// in image order threw the right one away before it was ever scored -- which is how
				// HighlightsFX kept its wrong identity even once the parent filter was out of the way.
				pool.Sort((x, y) => x.Value.CompareTo(y.Value));
				if (pool.Count > 160) pool.RemoveRange(160, pool.Count - 160);

				// Refuse rather than guess: a wrong class here would be silently wrong everywhere it is
				// used. The match has to be both close AND clearly ahead of the runner-up.
				// AN EXACT MATCH WINS. The gap rule exists for approximate matches, where a near miss
				// could belong to either candidate; requiring it of an exact one threw away perfectly
				// identified classes because some neighbour happened to be two members away.
				bool exactAlone = bestD == 0 && secondD > 0;
				bool ambiguous = bestD != int.MaxValue && bestD <= 24 && !exactAlone && secondD - bestD < 3;

				// COUNTING IS THE WEAK TEST. ASK THE MEMBERS.
				//
				// Two classes with the same number of methods and the same number of fields cannot be
				// told apart by counting, and that is exactly where the last VRChat classes were stuck:
				// "ecart 0, suivant 0", over and over, for types sitting right there in the image. But
				// their members are not interchangeable. The ordered sequence of arities, staticness and
				// parameter kinds is a fingerprint dozens of symbols long, and obfuscation leaves it
				// untouched -- so every plausible rival is made to match it member for member, and the
				// one that actually fits wins outright. Counting only decides what is worth fingerprinting.
				if (pool.Count > 0
					&& TokenShiftFix.RecordedMembers(key, out _, out string mSpec, out _)
					&& !string.IsNullOrEmpty(mSpec))
				{
					var want = mSpec.Split(',');
					if (want.Length >= 4)
					{
						Entry win = default; int s1 = -1, s2 = -1, scoreOfBest = -1;
						foreach (var kv in pool)
						{
							int sc;
							try { sc = MemberAlign.ShapeScore(kv.Key.K, want); } catch { continue; }
							if (kv.Key.K == best.K) scoreOfBest = sc;
							if (sc > s1) { s2 = s1; win = kv.Key; s1 = sc; }
							else if (sc > s2) s2 = sc;
						}

						// WHEN THE COUNTS AND THE MEMBERS POINT AT DIFFERENT CLASSES, BELIEVE THE MEMBERS.
						//
						// An exact count match short-circuits the gap rule, and that is how HighlightsFX was
						// recovered as a complete stranger: 19 methods and 8 fields on both sides, "ecart 0,
						// suivant 1" -- and a live member list sharing five of its nineteen shapes with the
						// recorded one. Two numbers agreed; nineteen signatures did not. Overruling only
						// where the gap is this wide keeps every recovery the counts got right.
						if (scoreOfBest >= 0 && s1 >= scoreOfBest * 2 + 4 && win.K != best.K)
						{
							VRChatArchiveModPlugin.Logger.LogWarning($"[ObfClassFinder] {full} : le comptage designait une classe qui ne partage que {scoreOfBest}/{want.Length} membres ; une autre en partage {s1} — c'est celle-la.");
							best = win; ambiguous = false; scoreOfBest = s1;
						}
						// TWO WAYS TO WIN, AND THE SECOND ONE MATTERS.
						//
						// Sharing three quarters of the recorded members is proof on its own. But VRChat
						// also reworks its own classes between builds, and a type that genuinely changed
						// can no longer clear that bar even when it is unmistakably the right one -- so a
						// candidate that simply leaves every rival far behind counts too. PortalInternal
						// sat at "ecart 2, suivant 4" for exactly this reason: one member away from the
						// counting rule, and silently below the absolute bar, so portal glow stayed dark
						// with nothing in the log to say why.
						int lead = Math.Max(4, want.Length / 8);
						if (s1 > s2 && (s1 * 4 >= want.Length * 3 || s1 >= s2 + lead))
						{
							bool changed = win.K != best.K;
							best = win; ambiguous = false;
							VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] {full} : identifiee par la forme de ses membres ({s1}/{want.Length}, suivant {s2})"
								+ (changed ? " — le comptage designait une autre classe." : "."));
						}
						else if (ambiguous && s1 >= 0)
							VRChatArchiveModPlugin.Logger.LogWarning($"[ObfClassFinder] {full} : la forme des membres ne departage pas non plus ({s1}/{want.Length} contre {s2}).");
						else if (!ambiguous && s1 >= 0 && s1 * 2 < want.Length)
						{
							// The counts are sure and the members disagree. That is worth SAYING, but not
							// worth overruling: refusing on it cost LoadBalancingClient, UIMenu and the
							// Photon stack their recovery the first time this ran, for classes the counts
							// had identified uniquely and correctly. The fingerprint only ever adds a
							// recovery here; it never takes one away.
							VRChatArchiveModPlugin.Logger.LogWarning($"[ObfClassFinder] {full} : retenue sur le comptage, mais seulement {s1}/{want.Length} membres partages — ses membres resteront muets.");
						}
					}
				}

				if (bestD == int.MaxValue || bestD > 24 || ambiguous)
				{
					_failed++;
					VRChatArchiveModPlugin.Logger.LogWarning($"[ObfClassFinder] {full} ({methods} methodes / {fields} champs, parent '{parent}') : "
						+ (bestD == int.MaxValue ? "aucun candidat" : $"meilleur ecart {bestD}, suivant {secondD} — trop ambigu") + " — refuse.");
					return IntPtr.Zero;
				}

				_found++;
				_recovered.Add(best.K);
				lock (_recoveredKey) { _recoveredKey[best.K] = key; }
				VRChatArchiveModPlugin.Logger.LogInfo($"[ObfClassFinder] {full} retrouvee : parent '{parent}', forme attendue {methods}/{fields}, "
					+ $"trouvee {best.M}/{best.F} (ecart {bestD}, suivant {secondD}) -> classe 0x{best.K.ToString("X")}");
				return best.K;
			}
			catch (Exception e)
			{
				_failed++;
				VRChatArchiveModPlugin.Logger.LogWarning("[ObfClassFinder] " + name + " : " + e.GetType().Name + " " + e.Message);
				return IntPtr.Zero;
			}
		}
	}
}
