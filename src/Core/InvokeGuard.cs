using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace VRChatArchiveMod.Core
{
	// THE LAST GATE, WHERE EVERY INTEROP CALL ACTUALLY HAPPENS.
	//
	// Guarding at resolution time was tried from every angle: refuse a member whose class is missing,
	// refuse one whose shape does not match, refuse one whose entry point is null, hand out a neutral
	// method instead. Each one removed a real crash and the next one appeared somewhere else, because
	// the guards sit in the mod's own helpers and half the modules reach il2cpp through plain
	// reflection, which never passes through them.
	//
	// il2cpp_runtime_invoke is the one place they all meet: every generated proxy method ends here, and
	// so does every reflected call on one. Two facts are checked, and they are the two that turn a bad
	// call into an access violation rather than a wrong answer:
	//
	//   * the method has real code behind it. VRChat strips methods nothing references and leaves the
	//     MethodInfo in place with a null entry point, so "resolved" never implied "callable".
	//   * the instance is actually an instance of the method's class. A class recovered under a new name
	//     can be the wrong one, and then a method reads fields at offsets its object does not have.
	//
	// A refused call returns null, which is what every caller already gets when a feature is
	// unavailable. Nothing is logged per call: a broken member is asked for every frame.
	internal static unsafe class InvokeGuard
	{
		private static readonly HashSet<IntPtr> _callable = new HashSet<IntPtr>();
		private static readonly HashSet<IntPtr> _refused = new HashSet<IntPtr>();
		private static readonly Dictionary<IntPtr, IntPtr> _owner = new Dictionary<IntPtr, IntPtr>();
		private static readonly HashSet<long> _compatible = new HashSet<long>();
		private static bool _armed;
		private static int _blockedMethod, _blockedType;

		internal static bool Armed => _armed;
		internal static string Summary => $"{_blockedMethod} appels sans code et {_blockedType} appels sur un objet du mauvais type ecartes";

		internal static void Install()
		{
			try
			{
				var target = typeof(IL2CPP).GetMethod("il2cpp_runtime_invoke", BindingFlags.Public | BindingFlags.Static);
				if (target == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[InvokeGuard] il2cpp_runtime_invoke introuvable — les appels ne sont pas filtres.");
					return;
				}
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
					typeof(InvokeGuard).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
				_armed = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[InvokeGuard] arme — un appel vers une methode sans code, ou sur un objet du mauvais type, rend null au lieu de tuer le process.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[InvokeGuard] non installe (" + e.GetType().Name + " " + e.Message + ") — les appels ne sont pas filtres.");
			}
		}

		// Positional names: the original is a P/Invoke declaration and its parameter names are not part
		// of any contract worth depending on. __3 is the exception out-slot, and CLEARING IT IS NOT
		// OPTIONAL: skipping the native call leaves whatever the caller's local held, and Il2CppInterop
		// reads that slot afterwards. Leaving it dirty sent RaiseExceptionIfNecessary into
		// Exception.ToString, which invokes, which is refused, which raises again -- a stack overflow in
		// three frames.
		private static bool Prefix(IntPtr __0, IntPtr __1, ref IntPtr __3, ref IntPtr __result)
		{
			if (Safe(__0, __1)) return true;
			__3 = IntPtr.Zero;
			__result = IntPtr.Zero;
			return false;
		}

		private static bool Safe(IntPtr method, IntPtr obj)
		{
			if (method == IntPtr.Zero) { _blockedMethod++; return false; }

			bool known;
			lock (_callable) known = _callable.Contains(method);
			if (!known)
			{
				lock (_refused) if (_refused.Contains(method)) { _blockedMethod++; return false; }
				if (!MemberAlign.Callable(method))
				{
					lock (_refused) _refused.Add(method);
					_blockedMethod++;
					return false;
				}
				lock (_callable) _callable.Add(method);
			}

			if (obj == IntPtr.Zero) return true;   // static call, or the caller's own problem

			IntPtr want = Owner(method);
			if (want == IntPtr.Zero) return true;  // could not read it: no opinion
			IntPtr have;
			try
			{
				if (!NativeGuard.IsReadable(obj, 8)) { _blockedType++; return false; }
				have = *(IntPtr*)obj;
			}
			catch { _blockedType++; return false; }
			if (have == want) return true;

			long key = (long)have ^ ((long)want * 31);
			lock (_compatible) if (_compatible.Contains(key)) return true;
			if (!Derives(have, want)) { _blockedType++; return false; }
			lock (_compatible) _compatible.Add(key);
			return true;
		}

		private static IntPtr Owner(IntPtr method)
		{
			lock (_owner) if (_owner.TryGetValue(method, out IntPtr k)) return k;
			IntPtr klass = IntPtr.Zero;
			try { klass = (IntPtr)UnityVersionHandler.Wrap((Il2CppMethodInfo*)method).Class; } catch { }
			if (klass != IntPtr.Zero && !NativeGuard.IsReadable(klass, 16)) klass = IntPtr.Zero;
			lock (_owner) _owner[method] = klass;
			return klass;
		}

		// Walk the object's class up to the method's. Interfaces are not covered and do not need to be:
		// an interface method is dispatched through the object's own vtable, not invoked directly.
		private static bool Derives(IntPtr have, IntPtr want)
		{
			try
			{
				for (int i = 0; i < 12 && have != IntPtr.Zero; i++)
				{
					if (have == want) return true;
					if (!NativeGuard.IsReadable(have, 16)) return false;
					var kw = UnityVersionHandler.Wrap((Il2CppClass*)have);
					var p = kw.Parent;
					have = p == null ? IntPtr.Zero : (IntPtr)p;
				}
			}
			catch { }
			return false;
		}
	}
}
