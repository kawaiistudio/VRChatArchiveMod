using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VRChatArchiveMod.Core
{
	// IL2CPP EXPORTS, BOUND BY THEIR REAL 1903 NAMES INSTEAD OF THEIR PUBLISHED ONES.
	//
	// WHY THIS EXISTS, MEASURED RATHER THAN ASSUMED
	//
	// VRChat scrambles GameAssembly's export names. Checked directly against the shipped binary on
	// 2026-09-18: NOT ONE plain il2cpp name appears in it -- not il2cpp_class_get_interfaces, and not
	// il2cpp_class_get_methods or il2cpp_object_get_class either, which this mod calls successfully
	// thousands of times a session. So Il2CppInterop is not resolving them by name; it resolves the set
	// it knows positionally. Anything outside that set silently becomes a NULL function pointer.
	//
	// Calling a null pointer jumps to address 0. That is an uncatchable access violation, and it is
	// exactly what the owner's crash dialog reported -- EXCEPTION_ACCESS_VIOLATION, READ_ACCESS_VIOLATION,
	// ViolationAddress=0x0 -- twice, from a probe that called il2cpp_class_get_interfaces and
	// il2cpp_class_get_parent. Neither existed. Nothing about the surrounding code was wrong; the call
	// itself was a jump into nothing.
	//
	// vrchat-pack-tools/final_mapping.1903.csv holds the real names, and ObfuscatedClassFinder already
	// trusts three of them (class enumeration works). This resolves the rest the same way -- but by
	// GetProcAddress at runtime, so a name that is NOT there comes back as Zero and the caller skips the
	// call instead of dying on it. A missing export must be a refusal, never a crash.
	internal static class Il2CppRaw
	{
		[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetModuleHandleA(string name);

		[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string proc);

		// Real 1903 export names, from final_mapping.1903.csv. Only what this mod actually calls outside
		// the set Il2CppInterop resolves for us -- a short, auditable list beats a 232-entry paste.
		private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
		{
			{ "il2cpp_class_get_interfaces", "tnHEZPfMHfi" },
			{ "il2cpp_class_get_parent",     "AeQfdkESyHM" },
		};

		private static readonly Dictionary<string, IntPtr> _resolved = new Dictionary<string, IntPtr>();
		private static bool _said;

		private static IntPtr Addr(string plainName)
		{
			if (_resolved.TryGetValue(plainName, out IntPtr cached)) return cached;
			IntPtr p = IntPtr.Zero;
			try
			{
				IntPtr mod = GetModuleHandleA("GameAssembly.dll");
				if (mod == IntPtr.Zero) mod = GetModuleHandleA("GameAssembly");
				if (mod != IntPtr.Zero && Names.TryGetValue(plainName, out string real))
				{
					p = GetProcAddress(mod, real);
					// The plain name is tried too: a build that stops scrambling would export it, and
					// then the mapping is the thing that is out of date.
					if (p == IntPtr.Zero) p = GetProcAddress(mod, plainName);
				}
			}
			catch { p = IntPtr.Zero; }
			_resolved[plainName] = p;
			return p;
		}

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr ClassGetInterfacesFn(IntPtr klass, ref IntPtr iter);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr ClassGetParentFn(IntPtr klass);

		private static ClassGetInterfacesFn _getInterfaces;
		private static ClassGetParentFn _getParent;

		/// <summary>True when every export this helper needs was found. Nothing here may be called otherwise.</summary>
		internal static bool Ready
		{
			get
			{
				if (_getInterfaces == null)
				{
					IntPtr a = Addr("il2cpp_class_get_interfaces");
					if (a != IntPtr.Zero) _getInterfaces = Marshal.GetDelegateForFunctionPointer<ClassGetInterfacesFn>(a);
				}
				if (_getParent == null)
				{
					IntPtr a = Addr("il2cpp_class_get_parent");
					if (a != IntPtr.Zero) _getParent = Marshal.GetDelegateForFunctionPointer<ClassGetParentFn>(a);
				}
				bool ok = _getInterfaces != null && _getParent != null;
				if (!_said)
				{
					_said = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[Il2CppRaw] exports 1903 : interfaces="
						+ (_getInterfaces != null) + " parent=" + (_getParent != null)
						+ (ok ? " — resolus par leurs vrais noms." : " — MANQUANTS, aucun appel ne sera fait."));
				}
				return ok;
			}
		}

		// THE FIRST CALL IS THE ONE THAT CAN KILL US, SO IT LEAVES A NOTE.
		//
		// The mapping says these names with "high" confidence, not certainty: it was translated from the
		// 1886 table. If tnHEZPfMHfi turns out to be some other function, calling it with (klass, &iter)
		// is the same uncatchable access violation all over again -- and no try/catch downstream helps,
		// because the process is gone before the handler runs.
		//
		// So the first call writes a breadcrumb and erases it on return. Found at startup, it means that
		// call never came back, and these exports stay unused for good. Same discipline the preview probe
		// earned the hard way: a feature may be worth a risk, but never the SAME crash twice.
		private static bool _probed, _banned;

		private static string Flag()
		{
			try { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "va_il2cppraw.flag"); }
			catch { return null; }
		}

		private static bool Arm()
		{
			if (_banned) return false;
			if (_probed) return true;
			_probed = true;
			string f = Flag();
			try
			{
				if (f != null && System.IO.File.Exists(f))
				{
					_banned = true;
					System.IO.File.Delete(f);
					VRChatArchiveModPlugin.Logger.LogWarning("[Il2CppRaw] le premier appel n'est pas revenu la session precedente — "
						+ "ces exports sont DESACTIVES. La correspondance 1903 est probablement fausse pour eux.");
					return false;
				}
				if (f != null) System.IO.File.WriteAllText(f, "1");
			}
			catch { }
			return true;
		}

		private static void Disarm()
		{
			try { string f = Flag(); if (f != null) System.IO.File.Delete(f); } catch { }
		}

		internal static IntPtr ClassGetInterfaces(IntPtr klass, ref IntPtr iter)
		{
			if (!Ready || klass == IntPtr.Zero || !Arm()) return IntPtr.Zero;
			try { IntPtr r = _getInterfaces(klass, ref iter); Disarm(); return r; }
			catch { Disarm(); return IntPtr.Zero; }
		}

		internal static IntPtr ClassGetParent(IntPtr klass)
		{
			if (!Ready || klass == IntPtr.Zero || !Arm()) return IntPtr.Zero;
			try { IntPtr r = _getParent(klass); Disarm(); return r; }
			catch { Disarm(); return IntPtr.Zero; }
		}
	}
}
