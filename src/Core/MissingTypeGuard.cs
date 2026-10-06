using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// WHY A TYPE THAT NO LONGER EXISTS KILLS THE WHOLE GAME.
	//
	// The interop assemblies were generated against VRChat build 1886. The game is now 1903, and the
	// update did not just reshuffle members: some types are gone under the name the interop knows.
	// VRCPlusStatus is one of them — GetIl2CppClass("Assembly-CSharp.dll", "", "VRCPlusStatus")
	// returns 0x0 on this build.
	//
	// Il2CppInterop's generated type initializer stores that zero and then, with no null check of
	// its own, hands it straight to il2cpp_runtime_class_init. That is a P/Invoke into il2cpp, which
	// dereferences it: access violation, and a native AV is not catchable in .NET, so the process is
	// simply gone. No exception, no log line, nothing to handle.
	//
	// Measured rather than assumed. A probe called each resolver with IntPtr.Zero, flushing a line to
	// disk before every call:
	//     GetIl2CppMethodByToken(NULL, ...) -> returns a missing-method stub    (survives)
	//     GetIl2CppMethod(NULL, ...)        -> returns a missing-method stub    (survives)
	//     GetIl2CppField(NULL, ...)         -> 0                                (survives)
	//     GetIl2CppNestedType(NULL, ...)    -> 0                                (survives)
	//     il2cpp_runtime_class_init(NULL)   -> the log ends here                (kills the process)
	// So exactly one call in that chain is lethal, and it is a P/Invoke with no IL body, which
	// Harmony cannot prefix. The guard therefore goes one step upstream: stop the zero from ever
	// being produced.
	//
	// A missing class resolves to System.Object instead of to null. runtime_class_init on it is a
	// no-op, member lookups miss and fall back to the same stubs measured above, and a call on the
	// vanished type raises a normal managed exception a module can catch — instead of ending VRChat.
	// Nothing is faked: a type with no il2cpp class has no instances either, so no field offset or
	// cast taken against the placeholder can ever be applied to a real object.
	//
	// The names it swallows are kept. That list is the actual repair list for this VRChat build —
	// every one is a member the mod references and the game no longer has.
	internal static class MissingTypeGuard
	{
		private static IntPtr _placeholder = IntPtr.Zero;
		private static readonly HashSet<string> _missing = new HashSet<string>(StringComparer.Ordinal);

		internal static bool Armed => _placeholder != IntPtr.Zero;

		// Sorted, so the log reads the same way twice and can be diffed between builds.
		internal static List<string> Missing
		{
			get { var l = new List<string>(_missing); l.Sort(StringComparer.Ordinal); return l; }
		}

		// True for the substitute pointer, so code that genuinely wants to know whether a type is
		// present can still ask instead of being fooled by the placeholder.
		internal static bool IsPlaceholder(IntPtr klass) => _placeholder != IntPtr.Zero && klass == _placeholder;

		internal static void Install()
		{
			try
			{
				// The recorded type tokens have to be in hand before the first lookup can miss.
				TokenShiftFix.EnsureTable();

				// Resolved BEFORE the patch is installed, so this lookup cannot recurse into the
				// postfix. If System.Object itself cannot be found the runtime is too broken to
				// second-guess, and installing a guard on top of that would only hide the real fault.
				// System.Void, NOT System.Object.
				//
				// Object looked like the harmless stand-in, and it is the opposite: everything is
				// assignable to it, so TryCast<T> to a missing type SUCCEEDED for any object at all.
				// The caller then read a field of that type, the field offset resolved to 0 because the
				// field does not exist either, and what came back was the object's own class pointer
				// handed out as a reference — a fake object the GC eventually walks into, which is the
				// access violation inside coreclr that kept ending the run.
				//
				// Void is a real class, so runtime_class_init on it is still a no-op, but nothing is
				// ever an instance of it: casts fail cleanly and no field is ever read off it.
				_placeholder = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Void");
				if (_placeholder == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[MissingTypeGuard] System.Object introuvable — garde NON installe.");
					return;
				}

				Patch("GetIl2CppClass", nameof(ClassPostfix));
				Patch("GetIl2CppNestedType", nameof(NestedPostfix));

				VRChatArchiveModPlugin.Logger.LogInfo(
					"[MissingTypeGuard] arme — un type absent de ce build resout vers System.Object au lieu "
					+ "de 0x0, ce qui evite l'access violation dans il2cpp_runtime_class_init.");
			}
			catch (Exception e)
			{
				_placeholder = IntPtr.Zero;
				VRChatArchiveModPlugin.Logger.LogWarning("[MissingTypeGuard] non installe : " + e.Message);
			}
		}

		private static void Patch(string method, string postfix)
		{
			var target = typeof(IL2CPP).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
			if (target == null)
				throw new MissingMethodException("IL2CPP." + method + " absent de cet Il2CppInterop");
			VRChatArchiveModPlugin.HarmonyInstance.Patch(target, postfix: new HarmonyMethod(
				typeof(MissingTypeGuard).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
		}

		// Runs during the static init of practically every interop type, so it does the least
		// possible on the path that matters: one pointer compare, and nothing at all when the type
		// was found.
		// A PLAIN NAME LOOKUP, WITH NO RECOVERY BEHIND IT.
		//
		// The finder needs to ask "does this class exist under its own name in some other assembly?",
		// and asking through GetIl2CppClass re-enters this postfix, which recovers a class by SHAPE and
		// hands it back -- so the finder concluded that VRC.Player was "present under its own name"
		// while looking at a stranger recovered for a different image, and sixteen real recoveries were
		// lost in one run. While this is set, a miss stays a miss.
		[ThreadStatic] private static bool _rawLookup;

		internal static IntPtr LookupByNameOnly(string assemblyName, string ns, string className)
		{
			bool was = _rawLookup;
			_rawLookup = true;
			try { return IL2CPP.GetIl2CppClass(assemblyName, ns, className); }
			catch { return IntPtr.Zero; }
			finally { _rawLookup = was; }
		}

		private static void ClassPostfix(string assemblyName, string namespaze, string className, ref IntPtr __result)
		{
			if (__result != IntPtr.Zero) return;
			if (_rawLookup) return;

			// Not found by name does not mean gone: VRChat renames its OWN classes every build, and the
			// class is still there under an obfuscated one. Recover it from the token shift its
			// plain-named neighbours reveal, so the type keeps working instead of degrading to a
			// placeholder -- VRC.Player (the nameplate tags), VRCNetworkingClient (Photon guard, network
			// log) and PortalInternal (portal ESP) all come back this way.
			IntPtr real = ObfuscatedClassFinder.Resolve(assemblyName, namespaze ?? "", className);
			if (real != IntPtr.Zero) { __result = real; return; }

			Note(string.IsNullOrEmpty(namespaze) ? className : namespaze + "." + className, assemblyName);
			__result = _placeholder;
		}

		private static void NestedPostfix(IntPtr enclosingType, string nestedTypeName, ref IntPtr __result)
		{
			if (__result != IntPtr.Zero) return;
			Note("<imbrique>." + nestedTypeName, "?");
			__result = _placeholder;
		}

		private static void Note(string full, string asm)
		{
			lock (_missing)
			{
				if (!_missing.Add(full + "  (" + asm + ")")) return;
			}
			// One line the first time each type is seen, never again.
			VRChatArchiveModPlugin.Logger.LogWarning("[MissingTypeGuard] absent de ce build : " + full + "  (" + asm + ")");
		}
	}
}
