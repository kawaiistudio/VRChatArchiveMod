using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// UNITY'S LOG, WITHOUT ASKING IL2CPP FOR A DELEGATE.
	//
	// Three features listened to Unity's log the documented way:
	//
	//     Application.add_logMessageReceivedThreaded(
	//         Il2CppDelegates.TryConvert<Application.LogCallback>(handler));
	//
	// and all three have been dark since the delegate bridge closed on VRChat 1903 -- the instance
	// log's video URLs, the diagnostics report's Unity errors, and the world-favourites watcher.
	// ConvertDelegate ends the process on this build, so the gate refuses, so the feature never
	// starts. That is the same wall UiClick hit for button clicks.
	//
	// The way past it is the same one: a callback exists only because native code needs a way INTO
	// managed code, and Unity already has one. Application.CallLogCallback is the static managed
	// method Unity's native logger calls for every line it emits -- condition, stack trace, type,
	// and whether it came from the main thread. Harmony detours it natively, exactly like every
	// other game hook in this mod, and the subscribers below are plain C# delegates that never
	// cross the il2cpp boundary at all.
	//
	//     UnityEngine.Application.CallLogCallback(string, string, LogType, bool)
	//
	// One hook is shared by every listener: patching it three times would run Harmony's dispatch
	// three times per log line, and this is one of the hottest managed entry points in the game.
	internal static class UnityLog
	{
		// A plain managed list, copied before dispatch: a handler is free to unsubscribe, and one of
		// them writing to a file can take long enough for another to be added meanwhile.
		private static readonly List<Action<string, string, LogType>> _subs = new List<Action<string, string, LogType>>(4);
		private static bool _hooked, _failed;
		private static int _dispatched, _threw;

		internal static bool Hooked => _hooked;
		internal static int Dispatched => _dispatched;

		internal static bool Subscribe(Action<string, string, LogType> handler, string who)
		{
			if (handler == null) return false;
			if (!Ensure())
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[" + who + "] off: le log Unity n'a pas pu etre hooke.");
				return false;
			}
			lock (_subs) { if (!_subs.Contains(handler)) _subs.Add(handler); }
			return true;
		}

		internal static void Unsubscribe(Action<string, string, LogType> handler)
		{
			if (handler == null) return;
			lock (_subs) _subs.Remove(handler);
		}

		private static bool Ensure()
		{
			if (_hooked) return true;
			if (_failed) return false;
			try
			{
				// Two names, because Il2CppInterop spells a method either way. A member it can name
				// plainly keeps its own name; one it has to disambiguate gets the descriptive form,
				// and the metadata of this build carries both spellings. The parameter list is what
				// actually identifies it in either case.
				var args = new[] { typeof(string), typeof(string), typeof(LogType), typeof(bool) };
				MethodInfo m = AccessTools.Method(typeof(Application), "CallLogCallback", args)
					?? AccessTools.Method(typeof(Application),
						"CallLogCallback_Private_Static_Void_String_String_LogType_Boolean_0", args);
				if (m == null) throw new MissingMethodException("UnityEngine.Application.CallLogCallback");

				VRChatArchiveModPlugin.HarmonyInstance.Patch(m, postfix: new HarmonyMethod(
					typeof(UnityLog).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));

				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[UnityLog] log Unity cable SANS le pont de delegues — Application.CallLogCallback est hooke, "
					+ "donc le log d'instance, le rapport de diagnostic et le veilleur de mondes reviennent.");
				return true;
			}
			catch (Exception e)
			{
				_failed = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[UnityLog] impossible de hooker Application.CallLogCallback : "
					+ Unwrap.Describe(e));
				return false;
			}
		}

		// Runs for EVERY line Unity logs, on whichever thread logged it. Everything here is bounded:
		// no allocation when nobody is listening, and a handler that throws is dropped for that line
		// instead of propagating back into native Unity code, where an exception crossing the il2cpp
		// boundary would end the process.
		// BY POSITION, NOT BY NAME. Harmony binds an injected parameter to the original method's
		// parameter NAME, and an Il2CppInterop proxy does not keep Unity's names -- the first attempt
		// asked for "condition" and Harmony refused the patch outright with
		// `Parameter "condition" not found in method static void Application::CallLogCallback(...)`,
		// which took the whole feature down again. __0/__1/__2 are positional and cannot be renamed.
		private static void Postfix(string __0, string __1, LogType __2)
		{
			string condition = __0, stackTrace = __1;
			LogType type = __2;
			Action<string, string, LogType>[] subs;
			lock (_subs)
			{
				if (_subs.Count == 0) return;
				subs = _subs.ToArray();
			}
			_dispatched++;
			for (int i = 0; i < subs.Length; i++)
			{
				try { subs[i](condition, stackTrace, type); }
				catch
				{
					// Counted rather than logged: logging from inside the log dispatch is how a
					// single bad handler turns into an unbounded recursion.
					_threw++;
				}
			}
		}
	}
}
