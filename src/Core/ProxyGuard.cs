using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// DO NOT INVOKE A MEMBER OF A CLASS THIS BUILD NO LONGER HAS.
	//
	// VRChat renames its internal classes between builds. When the interop asks for one under its
	// old name, MissingTypeGuard stands System.Object in for it so the type initializer survives --
	// but every method of that proxy then resolves against System.Object, and INVOKING one is an
	// access violation, not an exception. VRC.Player, VRCPlayer and RoomManager all took the game
	// down this way, one after the other, each through a reflected getter.
	//
	// So every reflected call on a proxy asks here first: is there a real il2cpp class behind this
	// managed type? The answer is cached per type. A missing class makes the call return null, which
	// every caller already treats as "not available", and the feature degrades instead of the game.
	internal static class ProxyGuard
	{
		private static readonly Dictionary<Type, bool> _backed = new Dictionary<Type, bool>();
		private static int _refused;

		internal static int Refused => _refused;

		// REFUSING A PATCH IS SAFE; REFUSING INSIDE A CCTOR IS NOT.
		//
		// A member that cannot be placed now resolves to Il2CppInterop's missing-member stub, so the
		// type initializer completes and the type stays alive (throwing there poisoned it for the whole
		// process and took out most of the mod). But a stub must never be HARMONY-PATCHED: the detour
		// would be written over a fabricated MethodInfo, and the game dies the moment the real event
		// fires.
		//
		// Every patch goes through Il2CppType.From, which is an ordinary method — so throwing from here
		// is catchable, lands in the module's own try/catch as "hook unavailable on this build", and
		// costs that one feature instead of the process.
		internal static void Install()
		{
			try
			{
				var m = typeof(Il2CppType).GetMethod("From", BindingFlags.Public | BindingFlags.Static,
					null, new[] { typeof(Type), typeof(bool) }, null);
				if (m == null) { VRChatArchiveModPlugin.Logger.LogWarning("[ProxyGuard] Il2CppType.From introuvable — les patches ne seront pas filtres."); return; }
				VRChatArchiveModPlugin.HarmonyInstance.Patch(m, prefix: new HarmonyMethod(
					typeof(ProxyGuard).GetMethod(nameof(FromPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
				// AND A SECOND GATE, ON THE PATCH ITSELF.
				//
				// Refusing a missing TYPE is not enough. A member of a class that IS present can resolve
				// to the NEUTRAL method -- a real, healthy il2cpp method standing in for members this
				// build does not have -- and Harmony detours that without complaint. The Photon OnEvent
				// hook landed on UnityEngine.Time.get_realtimeSinceStartup exactly that way, so from then
				// on every read of the clock ran the network prefix with whatever happened to be in the
				// argument slots; the game died a few seconds in, never twice in the same place.
				//
				// The same applies to a member placed only by its shape among several of the same shape:
				// calling one returns nonsense, but detouring one hands VRChat's real arguments to a
				// prefix written for different ones, and Il2CppInterop converts them before any of the
				// mod's own code runs -- so nothing downstream is left to catch it.
				MethodInfo ph = null;
				foreach (var cand in typeof(Harmony).GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (cand.Name != "Patch") continue;
					var ps = cand.GetParameters();
					if (ps.Length == 0 || ps[0].ParameterType != typeof(MethodBase)) continue;
					ph = cand; break;
				}
				if (ph == null) VRChatArchiveModPlugin.Logger.LogWarning("[ProxyGuard] Harmony.Patch introuvable — les hooks ne sont pas filtres.");
				else
				{
					VRChatArchiveModPlugin.HarmonyInstance.Patch(ph, prefix: new HarmonyMethod(
						typeof(ProxyGuard).GetMethod(nameof(PatchPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
					VRChatArchiveModPlugin.Logger.LogInfo($"[ProxyGuard] filtre de hooks pose ({ph.GetParameters().Length} parametres).");
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[ProxyGuard] arme — un type absent de ce build ne peut plus etre patche ni invoque.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ProxyGuard] non installe : " + e.Message); }
		}

		// The il2cpp method a generated proxy is bound to: its body loads exactly one
		// NativeMethodInfoPtr_* field, so the body names it.
		private static readonly Dictionary<MethodBase, FieldInfo> _slot = new Dictionary<MethodBase, FieldInfo>();

		// Also used by TokenShiftFix's core rebind, to recover which proxy METHOD a generated pointer
		// field belongs to -- the only thing that separates two overloads sharing a name.
		internal static FieldInfo NativeSlot(MethodBase m)
		{
			lock (_slot) if (_slot.TryGetValue(m, out FieldInfo hit)) return hit;
			FieldInfo found = null;
			try
			{
				byte[] il = m.GetMethodBody()?.GetILAsByteArray();
				var mod = m.Module;
				if (il != null)
					for (int i = 0; i + 5 <= il.Length; i++)
					{
						if (il[i] != 0x7E) continue;   // ldsfld
						FieldInfo f;
						try { f = mod.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { continue; }
						if (f == null || f.FieldType != typeof(IntPtr)) continue;
						if (!f.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)) continue;
						found = f; break;
					}
			}
			catch { }
			lock (_slot) _slot[m] = found;
			return found;
		}

		private static int _patchRefused;
		private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

		private static void PatchPrefix(MethodBase original)
		{
			if (original == null) return;
			Type t = original.DeclaringType;
			if (t == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(t)) return;
			// A HOOK IS ONLY POSSIBLE IF THE BRIDGE CAN MARSHAL THE ARGUMENTS.
			//
			// Detouring is not just calling. VRChat invokes the real method, and Il2CppInterop's
			// il2cpp->managed bridge converts every argument into its proxy type BEFORE the prefix
			// runs -- so a parameter whose il2cpp class does not resolve on this build cannot be
			// converted, and the process dies inside the bridge with nothing of the mod's on the
			// stack. Checking the TARGET cannot predict it: on Unity 6 three VoiceMimic hooks passed
			// every identity test (PeerBase anchored 58/60, signatures matching, the members they call
			// all bound) and each still killed the session on the first Photon operation, because they
			// all take a Photon.Client.ParameterDictionary that no longer resolves.
			// So a parameter type that is a proxy must have a real il2cpp class behind it.
			foreach (ParameterInfo pi in original.GetParameters())
			{
				Type pt = pi.ParameterType;
				if (pt.IsByRef) pt = pt.GetElementType();
				if (pt == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(pt)) continue;
				IntPtr pk = IntPtr.Zero;
				try { pk = Il2CppClassPointerStore.GetNativeClassPointer(pt); } catch { }
				if (pk != IntPtr.Zero && !MissingTypeGuard.IsPlaceholder(pk)) continue;
				string pid = t.Name + "." + original.Name;
				bool firstSay;
				lock (_said) firstSay = _said.Add(pid);
				if (firstSay)
					VRChatArchiveModPlugin.Logger.LogWarning($"[ProxyGuard] hook refuse sur {pid} : le parametre {pt.Name} n'a pas de classe il2cpp sur ce build — le pont ne pourrait pas le convertir.");
				_patchRefused++;
				throw new MissingMethodException("parametre non convertible sur ce build : " + pt.Name + " (" + pid + ")");
			}

			FieldInfo f = NativeSlot(original);
			if (f == null) return;                       // not a bound proxy method: nothing to judge
			IntPtr target = IntPtr.Zero;
			try { target = (IntPtr)f.GetValue(null); } catch { return; }
			// CALLABLE IS NOT ENOUGH TO DETOUR, AND A DETOUR IS THE UNFORGIVING CASE.
			//
			// Callable only reports that there is executable code at the address, which a wrong method
			// satisfies as readily as the right one. For an ordinary CALL that costs a nonsense return
			// value; for a HOOK it costs the process, because VRChat invokes the real method with its
			// own arguments and Il2CppInterop converts them for the prefix's signature before any of
			// the mod's code runs -- exactly what the comment above describes. On the Unity 6 build the
			// voice-mimic watch landed on Photon's PeerBase.SerializeOperationToMessage that way and
			// killed the session every time the game sent an operation.
			// So the bound method must also AGREE with the proxy it is bound to: same arity, same
			// staticness, and every slot whose type can be named. This is the one filter every hook in
			// the mod passes through, so checking it here covers all of them.
			if (!MemberAlign.TooWeakToHook(target) && MemberAlign.Callable(target)
				&& MemberAlign.SlotMatchesProxy(target, original)) return;

			if (t.Name.StartsWith("Object3PublicObApCaBoObUnique") && (original as MethodInfo)?.ReturnType == typeof(UnityEngine.GameObject) && MemberAlign.Callable(target))
			{
				VRChatArchiveModPlugin.Logger.LogInfo($"[ProxyGuard] {t.Name}.{original.Name} (Unpack Validation) explicit hook allowed.");
				return;
			}

			// A SECOND CHANCE, ON THE PROXY'S OWN SIGNATURE.
			//
			// Kinds could not separate VRCPlusStatus's three reference-returning getters, so the VRC+
			// flag's own getter was never identified and its hook was refused. The proxy names its return
			// type exactly -- ReactiveProperty<bool> -- and Il2CppInterop knows which live class that is,
			// so comparing class pointers settles it outright. When it does, the proxy's cached pointer is
			// rewritten too: the member is then correct everywhere, not just for this hook.
			IntPtr exact = IntPtr.Zero;
			try { exact = MemberAlign.ResolveByProxySignature(original); } catch { }
			if (exact != IntPtr.Zero)
			{
				bool wrote = false;
				try
				{
					// The generated fields are static readonly; .NET refuses FieldInfo.SetValue on an
					// initonly static once the type is initialised. A ref obtained through ldsflda does not.
					ref IntPtr cell = ref AccessTools.StaticFieldRefAccess<IntPtr>(f)();
					cell = exact;
					wrote = true;
				}
				catch { }
				if (wrote)
				{
					VRChatArchiveModPlugin.Logger.LogInfo($"[ProxyGuard] {t.Name}.{original.Name} identifie par sa signature (types de retour et de parametres) — hook autorise.");
					return;
				}
			}

			_patchRefused++;
			// ONCE PER MEMBER. A module that cannot install its hook retries on a timer, and saying the
			// same thing forty times is how a correct refusal reads as a fault.
			string id = t.Name + "." + original.Name;
			bool first;
			lock (_said) first = _said.Add(id);
			if (first)
				VRChatArchiveModPlugin.Logger.LogWarning($"[ProxyGuard] hook refuse sur {id} : ce membre n'est place que par sa forme, un detour dessus donnerait des arguments invalides.");
			throw new MissingMethodException("membre non identifiable de facon sure sur ce build : " + t.Name + "." + original.Name);
		}

		private static void FromPrefix(Type type)
		{
			// ONLY REAL PROXIES. Il2CppType.From is also called with ordinary managed types -- the mod's
			// own helpers, Il2CppInterop's IL2CPP class -- which have no il2cpp class by nature and must
			// not be refused: doing so skipped FieldOffsetFix's static-field repair outright.
			if (type == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(type)) return;
			if (Backed(type)) return;
			_refused++;
			throw new MissingMemberException("classe absente de ce build de VRChat : " + type.FullName);
		}

		// Two different questions, and conflating them is what kept ending the run.
		//
		//   Backed        — is there a real il2cpp class behind this managed type? True even for a class
		//                   recovered under a new name, because TYPE IDENTITY is sound: casts,
		//                   Il2CppType.From and TryCast all mean the right thing. The nameplate tags
		//                   need exactly this and nothing more.
		//   MembersUsable — can its members be reached? False for a recovered class: the interop knows
		//                   them by their 1886 obfuscated names, which name nothing here, so every
		//                   lookup lands on a stub or, worse, on some unrelated method of the same
		//                   class. Invoking either one is an access violation.
		private static readonly Dictionary<Type, bool> _usable = new Dictionary<Type, bool>();

		internal static bool MembersUsable(Type proxy)
		{
			if (!Backed(proxy)) return false;
			if (!typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(proxy)) return true;
			bool ok;
			lock (_usable) { if (_usable.TryGetValue(proxy, out ok)) return ok; }
			try { ok = TokenShiftFix.MembersTrustworthy(Il2CppClassPointerStore.GetNativeClassPointer(proxy)); }
			catch { ok = false; }
			if (!ok) VRChatArchiveModPlugin.Logger.LogWarning("[ProxyGuard] " + proxy.FullName + " : ses membres ne peuvent pas etre places sur ce build — appels refuses (le type reste utilisable pour les casts).");
			lock (_usable) { _usable[proxy] = ok; }
			return ok;
		}

		internal static bool Backed(Type proxy)
		{
			if (proxy == null) return false;
			// Same reasoning: a managed type that was never an il2cpp proxy is not "missing".
			if (!typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(proxy)) return true;
			bool ok;
			lock (_backed) { if (_backed.TryGetValue(proxy, out ok)) return ok; }
			try
			{
				IntPtr k = Il2CppClassPointerStore.GetNativeClassPointer(proxy);
				ok = k != IntPtr.Zero && !MissingTypeGuard.IsPlaceholder(k);
				if (!ok)
					VRChatArchiveModPlugin.Logger.LogWarning("[ProxyGuard] " + proxy.FullName + " n'a pas de classe il2cpp sur ce build — ses membres ne seront pas invoques.");
			}
			catch { ok = false; }
			lock (_backed) { _backed[proxy] = ok; }
			return ok;
		}

		// CAN I CALL THIS MEMBER? -- NOT "DO I TRUST THIS CLASS".
		//
		// The class-level test asks whether nearly ALL of a type's members could be placed, and that is
		// the wrong question for a caller who wants exactly one of them. HighlightsFX has seven methods
		// that return void and take no arguments: nothing can ever tell them apart, so the class can
		// never clear that bar -- yet its highlight getter is placed exactly, and refusing it left the
		// ESP glow dark while the answer was sitting right there, a hundred and eighty times a session.
		//
		// The member itself gives a straight answer: the pointer its proxy is bound to is either the
		// neutral stand-in for something this build does not have, or a real method with real code.
		private static bool MemberUsable(MethodBase m, object target)
		{
			if (m == null) return false;
			Type t = m.DeclaringType;
			if (t == null || !typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(t)) return true;
			if (!Backed(t)) return false;
			if (target != null && !Backed(target.GetType())) return false;

			FieldInfo f = NativeSlot(m);
			if (f == null) return MembersUsable(t);       // not a bound proxy method: no better test

			// Verified once per member, then remembered: this runs on the per-frame path, and the check
			// below walks the class's method list.
			lock (_memberOk) { if (_memberOk.TryGetValue(m, out bool cached)) return cached; }
			bool verdict = Verify(m, t, f);
			lock (_memberOk) { _memberOk[m] = verdict; }
			return verdict;
		}

		private static readonly Dictionary<MethodBase, bool> _memberOk = new Dictionary<MethodBase, bool>();

		// "CALLABLE" IS NOT "CORRECT".
		//
		// The slot used to be trusted as soon as Callable said yes -- but Callable only asks whether
		// there is executable code at the method pointer, which any wrong-but-healthy method also
		// answers. Invoking one through a proxy that declares a different signature is an access
		// violation, and RoomManager's static ApiWorldInstance getter took the game down from the update
		// loop that way, once per frame, as soon as the nameplate tags asked for the instance owner.
		// The signature test below was already here, but only as the fallback for when Callable FAILED
		// -- exactly backwards: the weak test was deciding, and the strong one only cleaned up after it.
		// ResolveByProxySignature compares live class pointers slot by slot against the types the proxy
		// itself declares, so it identifies the member rather than merely approving it. It goes first,
		// and repairs the slot when it disagrees. Callable remains only for the members it cannot
		// separate -- overloads that agree on every slot -- where there is nothing stronger to ask.
		private static bool Verify(MethodBase m, Type t, FieldInfo f)
		{
			IntPtr slot;
			try { slot = (IntPtr)f.GetValue(null); } catch { return MembersUsable(t); }

			IntPtr exact = IntPtr.Zero;
			try { exact = MemberAlign.ResolveByProxySignature(m); } catch { }
			if (exact != IntPtr.Zero)
			{
				if (exact == slot) return true;
				try
				{
					ref IntPtr cell = ref AccessTools.StaticFieldRefAccess<IntPtr>(f)();
					cell = exact;
				}
				catch { return false; }
				VRChatArchiveModPlugin.Logger.LogInfo($"[ProxyGuard] {t.Name}.{m.Name} identifie par sa signature — pointeur corrige, appel autorise.");
				return true;
			}

			// Could not be identified outright. The bound pointer must at least AGREE with the proxy --
			// Callable on its own only says there is code at the address, which a wrong method satisfies
			// just as well as the right one.
			return slot != IntPtr.Zero && slot != TokenShiftFix.NoOpPointer
				&& MemberAlign.Callable(slot) && MemberAlign.SlotMatchesProxy(slot, m);
		}

		internal static object GetValue(PropertyInfo p, object target)
		{
			if (p == null) return null;
			MethodInfo g = null;
			try { g = p.GetGetMethod(true); } catch { }
			if (g == null) { _refused++; return null; }
			if (!MemberUsable(g, target)) { _refused++; return null; }
			return p.GetValue(target);
		}

		// A FIELD IS NOT A CALL. Reading one goes through il2cpp_field_get_offset, which FieldOffsetFix
		// already answers safely for a field this build does not have, so there is no entry point to
		// verify and no reason to refuse on the class's behalf.
		internal static object GetValue(FieldInfo f, object target)
		{
			if (f == null) return null;
			if (!Backed(f.DeclaringType) || (target != null && !Backed(target.GetType()))) { _refused++; return null; }
			return f.GetValue(target);
		}

		internal static object Invoke(MethodInfo m, object target, object[] args)
		{
			if (m == null) return null;
			if (!MemberUsable(m, target)) { _refused++; return null; }
			return m.Invoke(target, args);
		}
	}
}
