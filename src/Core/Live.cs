using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// HANDING AN IL2CPP TYPE TO A NATIVE SCENE SEARCH IS THE MOD'S MOST DANGEROUS CALL.
	//
	// Object.FindObjectsOfType, Object.FindObjectsByType, Object.FindObjectOfType and
	// Resources.FindObjectsOfTypeAll all take an Il2CppSystem.Type and all do the same thing with it:
	// read the scripting class off it, with no validation. The mod obtains that type with
	// Il2CppType.Of<T>(), and on this build that is not safe for a VRChat type the update renamed --
	// it returns neither null nor a usable Type, but an object whose pointer is not live memory at
	// all. Every one of these calls then dies inside il2cpp, uncatchably, from the update loop:
	// HighlightEsp's pickup sweep killed the game four seconds into every session.
	//
	// So the type is vetted here, once, before any of them is allowed to run -- and vetted with
	// VirtualQuery rather than by asking il2cpp, because asking il2cpp about a bad pointer crashes
	// exactly as hard as using it (il2cpp_class_from_system_type was tried, and it took the process
	// down in the guard instead of in the caller).
	internal static unsafe class Live
	{
		private static readonly Il2CppReferenceArray<UnityEngine.Object> Empty =
			new Il2CppReferenceArray<UnityEngine.Object>(0);

		private static int _refused;

		// True only when `type` really is a live reflection Type standing for a UnityEngine.Object
		// subclass. Anything else -- null, a dead pointer, or the System.Object placeholder
		// MissingTypeGuard substitutes for a class this build dropped -- is refused.
		internal static bool UsableType(Il2CppSystem.Type type)
		{
			if (type == null) return false;
			try
			{
				// An Il2CppObject opens with { klass, monitor }; a reflection Type carries the
				// Il2CppType* it stands for immediately after them, at +0x10.
				IntPtr p = type.Pointer;
				if (p == IntPtr.Zero || !NativeGuard.IsReadable(p, 24)) return false;
				IntPtr t = Marshal.ReadIntPtr(p, 0x10);
				if (t == IntPtr.Zero || !NativeGuard.IsReadable(t, 8)) return false;

				IntPtr k = IL2CPP.il2cpp_class_from_type(t);
				if (k == IntPtr.Zero || !NativeGuard.IsReadable(k, 288) || MissingTypeGuard.IsPlaceholder(k)) return false;

				IntPtr want = Il2CppClassPointerStore.GetNativeClassPointer(typeof(UnityEngine.Object));
				if (want == IntPtr.Zero) return true;        // cannot judge; do not refuse on no evidence
				// Walk the parent chain with the handler's own offsets rather than an il2cpp export:
				// the pack rebinds those exports per build, the struct offsets are verified against it.
				for (int depth = 0; k != IntPtr.Zero && depth < 16; depth++)
				{
					if (k == want) return true;
					if (!NativeGuard.IsReadable(k, 288)) return false;
					k = (IntPtr)UnityVersionHandler.Wrap((Il2CppClass*)k).Parent;
				}
				return false;
			}
			catch { return false; }
		}

		// IS THIS PROXY METHOD BOUND TO ANYTHING REAL?
		//
		// Validating the TYPE argument is only half the job: the proxy's own method pointer can be a
		// missing-member stub, and invoking one of those is an access violation on this build, not the
		// catchable exception the interop intends. Unity 6 is full of them because it DELETED whole
		// overloads -- Component.GetComponent(Type) and GameObject.GetComponent(Type) are simply gone,
		// the live classes only carry GetComponent/0, GetComponentFastPath/2 and TryGetComponent/2 --
		// so the 1903 interop keeps proxies for members that no longer exist anywhere.
		// A stub is recognisable: its MethodInfo has no declaring class. Asked once per proxy method
		// and remembered, this lets a caller pick an overload the build actually has instead of
		// crashing on the one it used to have.
		private static readonly Dictionary<MethodBase, bool> _slotAlive = new Dictionary<MethodBase, bool>();

		internal static bool SlotAlive(MethodBase m)
		{
			if (m == null) return false;
			lock (_slotAlive) { if (_slotAlive.TryGetValue(m, out bool known)) return known; }
			bool ok = false;
			try
			{
				FieldInfo f = ProxyGuard.NativeSlot(m);
				if (f != null)
				{
					IntPtr p = (IntPtr)f.GetValue(null);
					if (p != IntPtr.Zero && NativeGuard.IsReadable(p, 88))
					{
						var mw = UnityVersionHandler.Wrap((Il2CppMethodInfo*)p);
						ok = (IntPtr)mw.Class != IntPtr.Zero && MemberAlign.Callable(p);
					}
				}
			}
			catch { ok = false; }
			lock (_slotAlive) { _slotAlive[m] = ok; }
			return ok;
		}

		// The proxy method for an exact overload, or null when the interop has none.
		internal static MethodBase Overload(Type owner, string name, params Type[] args)
		{
			try { return owner.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null); }
			catch { return null; }
		}

		// Is this proxy PROPERTY safe to read on this build?
		//
		// A getter is a proxy method like any other, so it can be bound to a missing-member stub and
		// take the process down when read -- and a try/catch around the read buys nothing, because an
		// AccessViolationException is a corrupted-state exception the CLR never offers to a catch.
		// VRC.Core.ApiContentModel<T>.get_name is one of those here, and the avatar index read it for
		// every avatar the game parsed. Resolved once per property and remembered.
		private static readonly Dictionary<string, bool> _readable = new Dictionary<string, bool>(StringComparer.Ordinal);

		internal static bool Readable(Type owner, string property)
		{
			if (owner == null || property == null) return false;
			string key = owner.FullName + "." + property;
			lock (_readable) { if (_readable.TryGetValue(key, out bool known)) return known; }
			bool ok = false;
			try
			{
				PropertyInfo pi = owner.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				MethodBase g = pi?.GetGetMethod(true);
				ok = g != null && SlotAlive(g);
			}
			catch { ok = false; }
			lock (_readable) { _readable[key] = ok; }
			if (!ok) VRChatArchiveModPlugin.Logger.LogWarning($"[Live] {key} n'est pas lisible sur ce build (getter non lie) — ignore.");
			return ok;
		}

		private static void Refused(string api)
		{
			if (_refused++ < 10)
				VRChatArchiveModPlugin.Logger.LogWarning($"[Live] {api} refuse : le type demande n'existe pas sur ce build (appel natif evite).");
		}

		internal static Il2CppReferenceArray<UnityEngine.Object> AllOfType(Il2CppSystem.Type type)
		{
			if (!UsableType(type)) { Refused("FindObjectsOfType"); return Empty; }
			try { return UnityEngine.Object.FindObjectsOfType(type) ?? Empty; }
			catch { return Empty; }
		}

		internal static Il2CppReferenceArray<UnityEngine.Object> AllOfType(Il2CppSystem.Type type, FindObjectsSortMode sort)
		{
			if (!UsableType(type)) { Refused("FindObjectsByType"); return Empty; }
			try { return UnityEngine.Object.FindObjectsByType(type, sort) ?? Empty; }
			catch { return Empty; }
		}

		internal static UnityEngine.Object FirstOfType(Il2CppSystem.Type type)
		{
			if (!UsableType(type)) { Refused("FindObjectOfType"); return null; }
			try { return UnityEngine.Object.FindObjectOfType(type); }
			catch { return null; }
		}

		internal static Il2CppReferenceArray<UnityEngine.Object> AllOfTypeAll(Il2CppSystem.Type type)
		{
			if (!UsableType(type)) { Refused("FindObjectsOfTypeAll"); return Empty; }
			try { return UnityEngine.Resources.FindObjectsOfTypeAll(type) ?? Empty; }
			catch { return Empty; }
		}
	}
}
