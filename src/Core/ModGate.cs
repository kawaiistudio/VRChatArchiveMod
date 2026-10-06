using System;

namespace VRChatArchiveMod.Core
{
	/// <summary>
	/// Supporter gate for the mod itself (Archive level 2+).
	///
	/// WHY IT LIVES HERE AND NOT IN EACH MODULE: a gate spread across sixty modules is a gate with
	/// holes — one new module written without it reopens everything. ModuleManager's dispatch is the
	/// single place every module passes through every frame, so the check sits there and a module
	/// cannot opt out by forgetting.
	///
	/// WHY IT IS RE-EVALUATED CONTINUOUSLY: the account level is not known at startup. It arrives
	/// asynchronously from the client bridge (VaAuth probes /status), and it can change mid-session
	/// when a subscription lapses or is upgraded. A one-shot test at OnInitialize would either block
	/// every legitimate user for the first seconds or let an expired account run forever.
	///
	/// WHAT THIS CAN AND CANNOT DO — stated plainly so nobody is surprised later: this mod runs on
	/// the user's own machine, so ANY local check is ultimately removable by someone determined
	/// enough. Obfuscation and this gate stop casual sharing, a DLL passed to a friend, and use
	/// under a third-party loader. They are not, and cannot be, absolute. The only gate that truly
	/// holds is the server-side one on the features that need the Archive API (hot swap, tags,
	/// decryptor) — those already refuse below their own tier and are unaffected by anything here.
	/// </summary>
	internal static class ModGate
	{
		/// <summary>Archive level required to run the gated modules. 2 = Supporter.</summary>
		public const int MinLevel = 2;

		/// <summary>Modules that stay free for everyone. Matched on TYPE NAME so the free set is
		/// explicit and auditable in one place rather than scattered as flags.
		/// MovementModule is the fly: the owner keeps it public and free.
		/// The menu and the mod's own core stay alive too — a blocked user must be able to SEE why
		/// nothing works, otherwise a deliberate gate is indistinguishable from a broken mod.</summary>
		private static readonly string[] AlwaysFree =
		{
			// INFRASTRUCTURE — MUST run for everyone, or the gate deadlocks itself. ModControlModule
			// is what calls VaAuth.Poll (the ONLY thing that ever connects the client bridge and
			// learns the account level). If it were gated, then: bridge=None -> gate locked ->
			// ModControlModule skipped -> VaAuth never polls -> level stays -1 -> bridge=None,
			// forever. It also carries the client<->mod data channel (player list, sync), so gating
			// it also left the client stuck on "WAITING FOR MOD DATA". The original design note in
			// ModControlModule says "THE PROBE RUNS FIRST, ABOVE EVERY GATE" — this honours that.
			"ModControlModule",
			"DiagnosticsModule",    // log capture; harmless and useful to keep running when locked
			"MovementModule",       // FLY — deliberately public and free (owner's call)
			// The menu surfaces stay alive so a blocked user can open the UI and READ why nothing
			// works. A silent gate is indistinguishable from a broken mod, and that turns into
			// support messages instead of subscriptions.
			"OverlayMenuModule",
			"QuickMenuTabModule",
			"MenuSkinModule",
			"MenuThemeModule",
			"UserMenuModule",
		};
		// NOTE: Menu / VaAuth live in Core, not in ModuleManager, so they are never dispatched here
		// and keep running regardless — which is what lets the gate learn the level and show itself.

		public static bool IsFree(string typeName)
		{
			if (string.IsNullOrEmpty(typeName)) return false;
			for (int i = 0; i < AlwaysFree.Length; i++)
				if (string.Equals(AlwaysFree[i], typeName, StringComparison.Ordinal)) return true;
			return false;
		}

		/// <summary>True when the connected Archive account may run the gated modules.
		///
		/// Requires a CLIENT BRIDGE specifically: a mod-side login never learns the tier (it reports
		/// -1), so accepting -1 would turn "we don't know" into a free pass, which is exactly the
		/// hole someone running the DLL outside the client would walk through. Unknown is refused.</summary>
		// GRACE WINDOW. Once an account has been verified, a bridge that blips — the client being
		// restarted, a probe timing out, the machine waking from sleep — must not rip every feature
		// away mid-session. Without this the gate would be indistinguishable from a bug at the worst
		// possible moment. It only ever extends an ALREADY verified session; it can never grant
		// access to an account that was never verified in the first place.
		private const double GraceSeconds = 90.0;
		private static double _lastGoodAt = double.NegativeInfinity;

		public static bool Allowed
		{
			get
			{
				try
				{
					bool verified = VaAuth.Current == VaAuth.Mode.ClientBridge
						&& (VaAuth.AccountAdmin || VaAuth.AccountLevel >= MinLevel);
					if (verified)
					{
						_lastGoodAt = VaClock.Now;
						return true;
					}

					// A DEFINITIVE refusal ends the grace immediately: the bridge answered and said
					// this account is below the bar (or signed out). Only an UNKNOWN state — no
					// bridge at all — is allowed to coast on the grace window.
					if (VaAuth.Current == VaAuth.Mode.ClientBridge) return false;

					return VaClock.Now - _lastGoodAt < GraceSeconds;
				}
				catch { return false; }   // never let the gate's own failure crash the dispatch loop
			}
		}

		// One log line per state change, never per frame.
		private static bool _lastState;
		private static bool _everLogged;

		/// <summary>Called from the dispatch loop; emits a single line when the gate opens or shuts
		/// so the reason is in the log without spamming it sixty times a second.</summary>
		public static void NoteState()
		{
			bool now = Allowed;
			if (_everLogged && now == _lastState) return;
			_everLogged = true;
			_lastState = now;
			try
			{
				if (now)
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[Gate] Supporter verified (level " + VaAuth.AccountLevel + ") — all features enabled.");
				else
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[Gate] locked: this mod needs a VRChat Archive account at Supporter (level "
						+ MinLevel + ") or higher, connected through the VRChat Archive Client. "
						+ "Fly stays available. Current: bridge=" + VaAuth.Current
						+ " level=" + VaAuth.AccountLevel);
			}
			catch { }
		}
	}
}
