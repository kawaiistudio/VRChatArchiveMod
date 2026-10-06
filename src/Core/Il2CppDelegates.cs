using System;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace VRChatArchiveMod.Core
{
	// ONE GATE IN FRONT OF A CALL THAT KILLS THE GAME.
	//
	// Il2CppInterop.DelegateSupport.ConvertDelegate wraps a managed delegate as the il2cpp delegate a
	// game method expects. On some VRChat builds it dies with an access violation, which .NET does not
	// let anyone catch — the process is simply gone. It is not a hook failing; it is the game not
	// starting. Six features call it, so a try/catch at each of them would be six pieces of code that
	// cannot work.
	//
	// The gate used to open as soon as FieldOffsetFix verified the field slot, on the theory that a
	// bad offset was the whole story. Build 1903 disproved that: with the offsets repaired and the
	// missing-type guard armed, the very first conversion still ends the process, right after
	// Il2CppInterop registers its Il2CppToMonoDelegateReference type.
	//
	// THE MARKER DESIGN, AND WHY IT WAS NOT ENOUGH (measured on a second machine, 2026-09-18)
	//
	// The gate used to open by default and only shut after a crash had left a marker on disk. That
	// budgeted "one crash per VRChat build, per machine" — already a poor bargain, and on 3.9.214 it
	// stopped being true at all:
	//
	//   * The owner's machine carries il2cpp-delegates-crash-237938176.marker, written 2026-09-16 by
	//     "ConvertDelegate<LogCallback> demande par Diagnostics". His bridge has been shut ever since,
	//     which is why every feature he has validated since was validated with it CLOSED.
	//   * 3.9.214 moved Diagnostics and the log feed off the bridge (Core/UnityLog hooks
	//     Application.CallLogCallback instead). That was the right fix, and it removed the only caller
	//     that went through TryConvert — the one path that writes the marker.
	//   * The first consumer then became UiClick, which asked Available and did the conversion itself,
	//     spelled as a cast. It crashed BEFORE writing anything. A second user's BepInEx log ends on
	//     Il2CppInterop's "Registered mono type ... Il2CppToMonoDelegateReference", every launch.
	//
	// So the mechanism meant to cost one crash cost that user every launch, forever, while the owner
	// was immune for a reason that had nothing to do with his pack.
	//
	// THE GATE NOW DEFAULTS SHUT, and nobody learns by crashing.
	//
	// This is the rule the rest of the mod already follows for scrambled il2cpp exports: something we
	// cannot verify is a refusal, never a call. The bridge has not been observed to survive a single
	// measured VRChat build, and both features that used to need it now have native routes that do not
	// (Core/UnityLog for the log feed, Core/UiClick's Button.Press hook for clicks). Turning it off by
	// default therefore costs nothing anyone has ever seen working.
	//
	// [Compatibility] AllowIl2CppDelegates = true opens it for testing — that is how a future build gets
	// re-tested, deliberately, by someone who chose to. The marker is still written around a forced
	// conversion so a crash leaves evidence behind instead of a silent loop.
	internal static class Il2CppDelegates
	{
		private static bool _explained;
		private static bool _checked;
		private static bool _knownLethal;

		private static string CookiePath
		{
			get
			{
				string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod");
				Directory.CreateDirectory(dir);
				long key = 0;
				try { key = new FileInfo(Path.Combine(BepInEx.Paths.GameRootPath, "GameAssembly.dll")).Length; }
				catch { }
				return Path.Combine(dir, "il2cpp-delegates-crash-" + key + ".marker");
			}
		}

		internal static bool Available
		{
			get
			{
				// The ONLY way this opens. Not a default, not an inference from a repaired field
				// offset — someone has to ask for it, on a build they are prepared to see die.
				bool forced = false;
				try { forced = ModConfig.AllowIl2CppDelegates != null && ModConfig.AllowIl2CppDelegates.Value; }
				catch { }

				if (!_checked)
				{
					_checked = true;
					try { _knownLethal = File.Exists(CookiePath); } catch { _knownLethal = false; }
					if (_knownLethal)
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[Il2CppDelegates] un lancement precedent est mort dans la conversion sur ce build "
							+ "(marqueur present).");
					else if (forced)
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[Il2CppDelegates] pont FORCE OUVERT par [Compatibility] AllowIl2CppDelegates. "
							+ "Sur les builds mesures jusqu'ici cette conversion tue le process — c'est un test, pas un reglage.");
				}

				// Field offsets must be repaired first even when forced: a conversion writes method_ptr,
				// invoke_impl, m_target and method, and doing that at the wrong slot is its own way to die.
				if (!forced) return false;
				try { return FieldOffsetFix.Verified; } catch { return false; }
			}
		}

		// Returns null when the bridge is off, so a caller that checks for null degrades to "this one
		// feature is missing" instead of taking everything down with it.
		internal static T TryConvert<T>(Delegate managed, string who) where T : Il2CppObjectBase
		{
			if (managed == null) return null;
			if (!Available) { Explain(who); return null; }

			// Written before the call and flushed, because an access violation gives no chance to
			// write anything afterwards. If the process survives, the marker goes away again.
			string cookie = null;
			try
			{
				cookie = CookiePath;
				using var fs = new FileStream(cookie, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
				var b = System.Text.Encoding.UTF8.GetBytes("ConvertDelegate<" + typeof(T).Name + "> demande par " + who);
				fs.Write(b, 0, b.Length);
				fs.Flush(true);
			}
			catch { cookie = null; }

			try
			{
				var r = DelegateSupport.ConvertDelegate<T>(managed);
				try { if (cookie != null) File.Delete(cookie); } catch { }
				return r;
			}
			catch (Exception e)
			{
				// A managed exception means the bridge is merely unhappy, not lethal — clear the
				// marker so one bad delegate does not shut the bridge for every other feature.
				try { if (cookie != null) File.Delete(cookie); } catch { }
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[" + who + "] il2cpp delegate conversion failed: " + Unwrap.Describe(e));
				return null;
			}
		}

		private static void Explain(string who)
		{
			if (_explained)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[" + who + "] off: il2cpp delegates disabled.");
				return;
			}
			_explained = true;
			VRChatArchiveModPlugin.Logger.LogWarning(
				"[" + who + "] off: Il2CppInterop's ConvertDelegate ends the process on this VRChat build, "
				+ "so features built on it are held back. Force with [Compatibility] AllowIl2CppDelegates = true.");
		}
	}
}
