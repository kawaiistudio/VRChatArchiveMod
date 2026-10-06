using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod
{
	// A NULL TYPE HANDED TO A NATIVE API IS AN ACCESS VIOLATION, NOT AN EMPTY RESULT.
	//
	// Resources.FindObjectsOfTypeAll is how the mod finds everything it did not create -- the
	// QuickMenu, sprites, audio sources, whole Transform trees -- and it is called from a dozen
	// places. Both of its forms broke on the Unity 6 build, for two different reasons.
	//
	// The NON-GENERIC form takes an Il2CppSystem.Type, and the callers obtain it with
	// Il2CppType.Of<T>(). For a VRChat type this build renamed, that returns null, and the null went
	// straight into il2cpp, which dereferenced it and ended the process. The method pointer was
	// correct the whole time -- MenuDonor asking for VRC.UI.Elements.QuickMenu was enough to crash the
	// game, and the failure looked exactly like a mis-bound member.
	//
	// The GENERIC form never reaches il2cpp at all here: Il2CppInterop builds it through a static
	// MethodInfoStoreGeneric whose initializer calls MethodInfo.MakeGenericMethod, and that crashed
	// during Load() on this build. Routing it through the non-generic call and casting the results
	// gives the same answer without the generic machinery -- and it is what the callers wanted anyway,
	// since the generic overload is documented in MenuDonor as returning nothing for il2cpp classes.
	//
	// This sits in the mod's root namespace, so C# binds every unqualified Resources.* call site in
	// VRChatArchiveMod.* here in preference to the UnityEngine one, with no call site edited.
	internal static unsafe class Resources
	{
		private static readonly Il2CppReferenceArray<UnityEngine.Object> Empty =
			new Il2CppReferenceArray<UnityEngine.Object>(0);

		// NULL IS NOT THE ONLY BAD TYPE -- A STAND-IN IS WORSE, BECAUSE IT LOOKS FINE.
		//
		// MissingTypeGuard deliberately resolves a type this build no longer has to System.Object
		// instead of 0x0, which is what keeps il2cpp_runtime_class_init from crashing. So
		// Il2CppType.Of<VRC.UI.Elements.QuickMenu>() does not come back null on a build that renamed
		// that class -- it comes back as System.Object, passes every null check, and is handed to a
		// native function that requires a UnityEngine.Object subclass. It then reads the scripting
		// class off something that has none: access violation, inside il2cpp, uncatchable.
		// So the guard is DESCENT, not nullity: unless the type really derives from UnityEngine.Object
		// this call does not happen at all and the caller gets an empty result, which every caller
		// already handles as "not found yet".
		internal static Il2CppReferenceArray<UnityEngine.Object> FindObjectsOfTypeAll(Il2CppSystem.Type type)
		{
			return VRChatArchiveMod.Core.Live.AllOfTypeAll(type);
		}

		// The callers all walk the result; returning a List keeps foreach, Count and indexing working
		// while the elements arrive already cast.
		internal static List<T> FindObjectsOfTypeAll<T>() where T : Il2CppObjectBase
		{
			var hits = new List<T>();
			Il2CppSystem.Type t;
			try { t = Il2CppType.Of<T>(); } catch { return hits; }
			if (!VRChatArchiveMod.Core.Live.UsableType(t)) return hits;

			Il2CppReferenceArray<UnityEngine.Object> all;
			all = VRChatArchiveMod.Core.Live.AllOfTypeAll(t);
			if (all == null) return hits;

			for (int i = 0; i < all.Length; i++)
			{
				UnityEngine.Object o = all[i];
				if (o == null) continue;
				T c;
				try { c = o.TryCast<T>(); } catch { continue; }
				if (c != null) hits.Add(c);
			}
			return hits;
		}

		internal static UnityEngine.Object Load(string path) => UnityEngine.Resources.Load(path);
		internal static void UnloadUnusedAssets() => UnityEngine.Resources.UnloadUnusedAssets();
	}
}
