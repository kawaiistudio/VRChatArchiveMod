using System;
using System.Reflection;
using HarmonyLib;

namespace VRChatArchiveMod.Core
{
	// GIVE Il2CppInterop BACK ITS NESTED TYPES.
	//
	// Il2CppInterop resolves a nested type by calling the game's `il2cpp_class_get_nested_types`
	// iterator. VRChat's GameAssembly does not export that name any more — it exports 264 functions
	// under random eleven-character names that are re-rolled at every game update — so the call dies
	// with `Unable to find an entry point named 'OfYRWVorWbz'`, and with it every type initializer
	// that needs a nested type. In this mod that is LogCapture (UnityEngine.Application.LogCallback),
	// Diagnostics, the InstancePanels video hook, and every submenu button on the menu, all of which
	// fail with the same unhelpful "type initializer for Il2CppClassPointerStore`1 threw".
	//
	// The obvious repair is to find the export's new name and point Il2CppInterop at it. That is a
	// bad trade: guessing wrong is not a failure, it is a fatal access violation, and the answer
	// expires at the next VRChat update. Disassembly says the iterator with the nested-type counter
	// is `npApjJKKUuO`, reading its array from Il2CppClass+0x88 on build 1886 (0x10 on 1903 - which is why it is FOUND, not assumed) — but the struct offset is the
	// durable half of that answer, so this walks the array itself and never calls the export.
	//
	// Which slot holds the array is not assumed either. VRChat reorders these structs, so the offset
	// is FOUND, using the one property only the real array has: every entry in it is a class whose
	// declaringType points back at the enclosing class. Nothing else in an Il2CppClass looks like
	// that. Every read is checked against the page map first, so a wrong guess during the search
	// costs nothing instead of killing the process.
	internal static class NestedTypeFix
	{
		// Measured on this build (VRChatStructFix reports the same values at startup).
		private const int OffDeclaringType = 0x48;   // build 1903 (etait 0x58)
		private const int OffName = 0x58;   // build 1903 (etait 0x98)
		private const int OffNestedCount = 0x128;
		private const int Il2CppClassSize = 0x138;

		private static int _arrayOffset = -1;    // -1 until found
		private static bool _logged;

		internal static void Install()
		{
			try
			{
				MethodInfo target = typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(
					"GetIl2CppNestedType",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					null, new[] { typeof(IntPtr), typeof(string) }, null);

				if (target == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[NestedTypeFix] IL2CPP.GetIl2CppNestedType not found — nested types stay broken.");
					return;
				}

				var pre = new HarmonyMethod(typeof(NestedTypeFix)
					.GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: pre);
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[NestedTypeFix] armed — nested types resolved by walking Il2CppClass, not by export name.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError("[NestedTypeFix] install failed: " + e);
			}
		}

		// Returning false skips Il2CppInterop's version. When we cannot answer we return true and let
		// it run, so a case we do not handle fails exactly the way it does today rather than handing
		// back a null class pointer, which would turn a catchable startup error into a crash later.
		private static bool Prefix(IntPtr enclosingType, string nestedTypeName, ref IntPtr __result)
		{
			IntPtr found = Resolve(enclosingType, nestedTypeName);
			if (found == IntPtr.Zero) return true;
			__result = found;
			return false;
		}

		private static unsafe IntPtr Resolve(IntPtr enclosing, string name)
		{
			try
			{
				if (string.IsNullOrEmpty(name)) return IntPtr.Zero;
				if (!NativeGuard.IsReadable(enclosing, Il2CppClassSize)) return IntPtr.Zero;

				int off = _arrayOffset;
				if (off < 0)
				{
					off = Detect(enclosing);
					if (off < 0) return IntPtr.Zero;   // this class has none; try again on the next one
					_arrayOffset = off;
					if (!_logged)
					{
						_logged = true;
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[NestedTypeFix] nested-type array located at Il2CppClass+0x" + off.ToString("X")
							+ " (found from " + name + "'s enclosing type, header count "
							+ (*(ushort*)((byte*)enclosing + OffNestedCount)) + ").");
					}
				}

				IntPtr arr = *(IntPtr*)((byte*)enclosing + off);
				if (!NativeGuard.IsReadable(arr, 8)) return IntPtr.Zero;

				int count = *(ushort*)((byte*)enclosing + OffNestedCount);
				for (int i = 0; i < count; i++)
				{
					if (!NativeGuard.IsReadable(IntPtr.Add(arr, i * 8), 8)) break;
					IntPtr k = ((IntPtr*)arr)[i];
					if (!NativeGuard.IsReadable(k, Il2CppClassSize)) break;
					if (*(IntPtr*)((byte*)k + OffDeclaringType) != enclosing) break;
					if (NameOf(k) == name) return k;
				}
			}
			catch { }
			return IntPtr.Zero;
		}

		// The array is the only member of an Il2CppClass whose entries all declare the class back.
		private static unsafe int Detect(IntPtr enclosing)
		{
			int best = -1, bestRun = 0;
			int count = *(ushort*)((byte*)enclosing + OffNestedCount);
			for (int off = 0; off <= Il2CppClassSize - 8; off += 8)
			{
				IntPtr arr = *(IntPtr*)((byte*)enclosing + off);
				if (!NativeGuard.IsReadable(arr, 8)) continue;

				int run = 0;
				for (int i = 0; i < 64; i++)
				{
					if (!NativeGuard.IsReadable(IntPtr.Add(arr, i * 8), 8)) break;
					IntPtr k = ((IntPtr*)arr)[i];
					if (!NativeGuard.IsReadable(k, Il2CppClassSize)) break;
					if (*(IntPtr*)((byte*)k + OffDeclaringType) != enclosing) break;
					run++;
				}
				// A slot whose run matches the header count exactly is the array; a slot that merely
				// happens to start with one self-declaring class (a type implementing an interface
				// nested inside itself, say) does not. Length alone would not tell them apart.
				if (count > 0 && run == count) return off;
				if (run > bestRun) { bestRun = run; best = off; }
			}

			// NO GUESSING. The old fallback returned the longest partial run, i.e. a slot that
			// merely starts with one self-declaring class. A wrong array here is not a missed
			// lookup, it is a fatal access violation in the caller, so an inexact match must fail
			// closed: Resolve() then returns Zero, Il2CppInterop takes its normal path and throws
			// a MANAGED exception that ModuleManager catches. Degraded beats dead.
			return -1;
		}

		private static unsafe string NameOf(IntPtr klass)
		{
			IntPtr p = *(IntPtr*)((byte*)klass + OffName);
			if (!NativeGuard.IsReadable(p, 1, requireAligned: false)) return null;
			// PtrToStringAnsi walks to the first NUL with NO bound. One readable byte is not a
			// promise that a NUL arrives before the page ends, and running off it is fatal. Read
			// it manually, re-checking readability, and give up past a length no type name has.
			byte* b = (byte*)p;
			int n = 0;
			while (n < 256)
			{
				if (!NativeGuard.IsReadable((IntPtr)(b + n), 1, requireAligned: false)) return null;
				if (b[n] == 0) break;
				n++;
			}
			if (n == 0 || n >= 256) return null;
			return System.Text.Encoding.ASCII.GetString(b, n);
		}
	}
}
