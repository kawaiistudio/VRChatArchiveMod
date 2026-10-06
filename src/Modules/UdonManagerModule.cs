using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// UDON MANAGER — switch a world's scripts off, one at a time, on YOUR client.
	//
	// The mod already had the blunt version of this: "Anti-crasher" and PANIC, which block Udon
	// wholesale. PANIC works but takes the world with it — no doors, no video player, no game. This
	// is the same idea with a scalpel: find the one behaviour that is spamming, flashing or lagging
	// you, and turn off just that.
	//
	// LOCAL ONLY, and that is not a detail. Disabling a component stops it running in THIS process;
	// nothing is sent, and no other player's world changes. What it can do is desync you — a script
	// you switched off is a script that stops tracking the state everyone else still shares, so
	// doors, scores and games can drift until you turn it back on.
	//
	// Everything we switch off is remembered, so RESTORE puts back exactly what we touched and
	// never re-enables something the world had already disabled itself.
	public class UdonManagerModule : IModule
	{
		public override string Name => "UdonManager";

		public sealed class Entry
		{
			// THE HANDLE THE CLIENT HOLDS. The desktop app cannot keep a Behaviour reference across a
			// socket, so every row it draws is addressed by this number and nothing else. Unity gives
			// each object an instance id unique for its lifetime, and it survives a rescan — exactly
			// the lifetime a selection needs.
			public int Id;
			public Behaviour B;
			public string Path;
			public string Short;
			public float Dist;
			public bool OffByUs;
			public string Owner = "";   // display name of the player who owns the object (network owner), "" if unknown
			public bool Mine;           // WE are the network owner right now (refreshed on scan and on selection)
		}

		private static readonly List<Entry> Items = new List<Entry>();
		private static readonly object Gate = new object();
		private static readonly HashSet<int> OurOff = new HashSet<int>();

		public static string Status = "";
		public static int Count { get { lock (Gate) return Items.Count; } }
		public static int DisabledByUs { get { lock (Gate) return OurOff.Count; } }

		public static List<Entry> Snapshot() { lock (Gate) return new List<Entry>(Items); }

		// Address a row by instance id — the only way the desktop client can name one, since it
		// cannot hold a Behaviour. Null when the world changed underneath it, which is the honest
		// answer: the object it was looking at does not exist any more.
		public static Entry ById(int id)
		{
			lock (Gate)
				for (int i = 0; i < Items.Count; i++)
					if (Items[i] != null && Items[i].Id == id) return Items[i];
			return null;
		}

		// On demand only. This walks every loaded object, which is the single most expensive call
		// the mod can make — it is not something to run on a timer.
		public static void Rescan()
		{
			try
			{
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) { Status = "UdonBehaviour type not found"; return; }

				Vector3 me = Vector3.zero;
				try { var t = PlayerRef.LocalTransform(); if (t != null) me = t.position; } catch { }

				var found = Resources.FindObjectsOfTypeAll(Il2CppType.From(ub));
				var list = new List<Entry>(found != null ? found.Length : 0);

				if (found != null)
					for (int i = 0; i < found.Length; i++)
					{
						try
						{
							// CAST TO THE REAL TYPE, NOT TO Behaviour. Every reflected Invoke on this
							// page (Interact, SendCustomEvent, GetPrograms, GetProgramVariable, the
							// setter) takes a MethodInfo declared on UdonBehaviour and needs the
							// target's MANAGED type to be that class — a Behaviour-typed proxy over
							// the same pointer throws "Object does not match target type" on every
							// one of them, which is why INTERACT and RUN never did anything. The
							// UdonBehaviour proxy derives from Behaviour, so Entry.B keeps its type
							// and .enabled keeps working.
							var b = found[i]?.TryCast<VRC.Udon.UdonBehaviour>();
							if (b == null) continue;

							var go = b.gameObject;
							if (go == null) continue;
							var tr = b.transform;
							if (tr == null) continue;

							// WORLD SCRIPTS ONLY. FindObjectsOfTypeAll returns everything in memory,
							// which is why the list was full of the wrong things:
							//   * PREFAB ASSETS — not instantiated, so their scene is invalid AND
							//     their .enabled is meaninglessly false. That is why every row read
							//     OFF. A real scene object has a LOADED scene, not just a valid one.
							//   * AVATAR objects — FollowHead, FollowHandL, Udon_BreathSystem: those
							//     hang under a player, not the world. Excluded by walking up to the
							//     root and rejecting anything parented under a VRCPlayer.
							if (b.hideFlags == HideFlags.HideAndDontSave) continue;
							Scene sc;
							try { sc = go.scene; } catch { continue; }
							if (!sc.IsValid() || !sc.isLoaded) continue;   // drops prefab assets
							if (IsUnderPlayer(tr)) continue;               // drops avatar objects

							string path = PathOf(tr);
							list.Add(new Entry
							{
								Id = b.GetInstanceID(),
								B = b,
								Path = path,
								Short = tr.name ?? "?",
								Dist = Vector3.Distance(me, tr.position),
								OffByUs = OurOff.Contains(b.GetInstanceID()),
								Owner = OwnerOf(tr),
								Mine = IsMine(tr.gameObject),
							});
						}
						catch { }
					}

				// Nearest first: the script bothering you is almost always the one you are standing
				// next to, and a 1200-entry list sorted by nothing is unusable.
				list.Sort((x, y) => x.Dist.CompareTo(y.Dist));

				lock (Gate) { Items.Clear(); Items.AddRange(list); }
				Status = list.Count + " Udon behaviour(s) in this world";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] {list.Count} behaviour(s) found.");
			}
			catch (Exception e)
			{
				Status = "scan failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[UdonManager] " + e.Message);
			}
		}

		public static void SetEnabled(Entry e, bool on)
		{
			try
			{
				if (e == null || e.B == null) return;
				e.B.enabled = on;
				int id = e.B.GetInstanceID();
				lock (Gate)
				{
					if (on) OurOff.Remove(id);
					else OurOff.Add(id);
				}
				e.OffByUs = !on;
				Status = (on ? "re-enabled " : "disabled ") + Trunc(e.Short, 40);
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] {(on ? "enabled" : "disabled")} {e.Path}");
			}
			catch (Exception ex) { Status = "could not change it: " + ex.Message; }
		}

		// Only what WE switched off. A world ships plenty of behaviours disabled on purpose, and
		// turning those on would be a different kind of breaking it.
		public static void RestoreAll()
		{
			int n = 0;
			try
			{
				List<Entry> all = Snapshot();
				foreach (var e in all)
				{
					if (e == null || e.B == null || !e.OffByUs) continue;
					try { e.B.enabled = true; e.OffByUs = false; n++; } catch { }
				}
				lock (Gate) OurOff.Clear();
			}
			catch { }
			Status = n > 0 ? ("restored " + n + " behaviour(s)") : "nothing of ours was off";
			VRChatArchiveModPlugin.Logger.LogInfo("[UdonManager] " + Status);
		}

		// Bulk switch for everything currently listed by the filter — the case where one prefab is
		// instantiated two hundred times and turning them off one by one is not a real option.
		public static void SetMany(List<Entry> rows, bool on)
		{
			int n = 0;
			foreach (var e in rows)
			{
				if (e == null || e.B == null) continue;
				try { SetEnabled(e, on); n++; } catch { }
			}
			Status = (on ? "re-enabled " : "disabled ") + n + " behaviour(s)";
		}

		// A world change destroys them all; our record of what we switched off means nothing after
		// that, and keeping it would make RESTORE claim to fix things that no longer exist.
		public override void OnSceneLoaded(int buildIndex)
		{
			lock (Gate) { Items.Clear(); OurOff.Clear(); }
			Status = "";
		}

		// ------------------------------------------------------------------ entry points

		// The events a behaviour exposes. Read once per selected row, never during the scan — this
		// costs a reflected call per behaviour and a world can hold a thousand of them.
		public static List<string> EntryPoints(Entry e)
		{
			var outp = new List<string>();
			try
			{
				if (e == null || e.B == null) return outp;
				RefreshOwner(e);
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var mi = ub?.GetMethod("GetPrograms");
				object arr = mi?.Invoke(e.B, null);
				if (arr == null) return outp;

				// ImmutableArray<string>: enumerate it properly. Calling ToString() here is what
				// made every earlier dump print the type name instead of the events.
				var at = arr.GetType();
				var lenP = at.GetProperty("Length") ?? at.GetProperty("Count");
				var item = at.GetProperty("Item");
				if (!(lenP?.GetValue(arr) is int len) || item == null) return outp;
				for (int i = 0; i < len && i < 120; i++)
				{
					try
					{
						string n = item.GetValue(arr, new object[] { i }) as string;
						if (!string.IsNullOrEmpty(n)) outp.Add(n);
					}
					catch { }
				}
			}
			catch { }
			return outp;
		}

		// GLOBAL or LOCAL — VRChat's own network boundary. VRChat's SendCustomNetworkEvent REFUSES
		// any entry point whose name starts with '_': the Udon/Unity lifecycle hooks (_start,
		// _update, _interact, _onPickup…) and every author event they chose to prefix can ONLY ever
		// run on the client that owns them. Everything else is network-ELIGIBLE, so we badge it
		// GLOBAL.
		//
		// "Eligible" is the honest word. A non-'_' event CAN be broadcast, but whether a given world
		// actually broadcasts it is a property of that world's CODE, not of the event name — a world
		// is free to define "ObjectOrbit" and only ever fire it locally, so your friends never see
		// it. No client-side check can tell that apart from a truly global event without decompiling
		// the world's Udon program. So GLOBAL = "VRChat would let this be sent to everyone", LOCAL =
		// "VRChat guarantees it never can be".
		//
		// The old GetNetworkCallingMetadata(name) probe returned non-null for EVERY non-'_' event —
		// it measured this exact boundary through a fragile reflected native call. The name test is
		// the same answer for free, and never wrong about the LOCAL set.
		public static bool IsGlobalEvent(Entry e, string ev)
			=> !string.IsNullOrEmpty(ev) && ev[0] != '_';

		// RUNS THE EVENT ON THIS CLIENT ONLY.
		//
		// SendCustomEvent executes the entry point in our own process. Nothing is transmitted, no
		// other player's world moves, and the world's networked state stays whatever the owner says
		// it is. That is what makes it a debugging tool: you can see what a script does from your
		// side and undo it by leaving the instance.
		//
		// There is a sibling call, SendCustomNetworkEvent, which broadcasts the same event to
		// everyone. It is deliberately NOT wired here and should not be added: it is the same
		// function whether you are inspecting a door or ending someone else's game, and the
		// difference is not something the mod can judge.
		public static bool RunLocal(Entry e, string ev)
		{
			try
			{
				if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return false;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var mi = ub?.GetMethod("SendCustomEvent", new[] { typeof(string) });
				if (mi == null) { Status = "SendCustomEvent not found"; return false; }
				mi.Invoke(e.B, new object[] { ev });
				Status = "ran " + Trunc(ev, 32) + " locally";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] local SendCustomEvent '{ev}' on {e.Path}");
				return true;
			}
			catch (Exception ex)
			{
				Status = "event failed: " + ex.Message;
				return false;
			}
		}

		// RUNS THE EVENT FOR EVERYONE. SendCustomNetworkEvent broadcasts the entry point to every
		// client in the instance over VRChat's own networking — the same path the world's own
		// scripts use. VRChat refuses this for '_'-prefixed events (see IsGlobalEvent), so it is
		// only ever OFFERED for network-eligible ones, and the menu colours the button apart from
		// RUN so the "affects the whole instance" one is never a slip of the mouse. Use it to drive
		// a world's shared state — reset a game, open a door for the room — not to disrupt it.
		private static MethodInfo _sendNet;
		private static Type _netTarget;
		private static bool _netResolved;

		// WHO OWNS IT. An Udon event can be sent to everyone or to the object's OWNER, and the owner
		// is the one handle VRChat gives you on a specific player: the pen in their hand, the video
		// player they took, the chair they sit on. Read once per scan; the roster shows the name.
		private static string OwnerOf(Transform tr)
		{
			try
			{
				var api = VRC.SDKBase.Networking.GetOwner(tr.gameObject);
				if (api == null) return "";
				string n = api.displayName;
				return string.IsNullOrEmpty(n) ? "" : n;
			}
			catch { return ""; }
		}

		// ARE WE THE OWNER. Networking.IsOwner(localPlayer, go) -- the same test a world makes before it
		// writes a synced variable. Read at scan time and refreshed whenever a row is selected or acted
		// on, so the label and the TAKE OWNERSHIP button reflect the object as it is now.
		private static bool IsMine(GameObject go)
		{
			try
			{
				if (go == null) return false;
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (me == null) return false;
				return VRC.SDKBase.Networking.IsOwner(me, go);
			}
			catch { return false; }
		}

		// Owner and Mine of one row, re-read now. Ownership moves every time somebody picks something
		// up; a label read at scan time is stale within seconds, so every user-triggered path re-reads
		// it for the row it touches (one native call, never on the whole list).
		public static void RefreshOwner(Entry e)
		{
			try
			{
				if (e == null || e.B == null) return;
				var tr = e.B.transform;
				if (tr == null) return;
				e.Owner = OwnerOf(tr);
				e.Mine = IsMine(tr.gameObject);
			}
			catch { }
		}

		// CAN THIS OBJECT BE OWNED AT ALL. Networking.SetOwner on an object with no network state
		// dereferences state that was never allocated and takes the whole game down (VideoUrlModule
		// learned that the hard way). Network state exists when the object carries a VRCObjectSync or
		// a pickup, OR when the UdonBehaviour itself synchronises (Manual / Continuous SyncMethod).
		// Anything unclear is "no", and "no" means we never call SetOwner on it.
		public static bool CanOwnObject(GameObject go)
		{
			try
			{
				if (go == null) return false;
				for (Transform cur = go.transform; cur != null; cur = cur.parent)
				{
					var cgo = cur.gameObject;
					if (cgo == null) continue;
					var ub = cgo.GetComponent<VRC.Udon.UdonBehaviour>();
					if (ub != null && (HasSync(ub) || ub.IsNetworkingSupported)) return true;
					var comps = cgo.GetComponents<Component>();
					if (comps == null) continue;
					foreach (var c in comps)
					{
						if (c == null) continue;
						string n;
						try { n = MenuCard.Il2CppNameOf(c); } catch { continue; }
						if (string.IsNullOrEmpty(n)) continue;
						if (n.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) >= 0
						 || n.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0
						 || n.IndexOf("UdonSync", StringComparison.OrdinalIgnoreCase) >= 0) return true;
					}
				}
			}
			catch { }
			return false;
		}

		public static bool CanOwn(Entry e)
		{
			if (e == null || e.B == null) return false;
			return CanOwnObject(e.B.gameObject);
		}

		public static bool TakeOwnershipDirect(GameObject go)
		{
			try
			{
				if (go == null) return false;
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (me == null) return false;

				bool took = false;
				for (Transform cur = go.transform; cur != null; cur = cur.parent)
				{
					var cgo = cur.gameObject;
					if (cgo == null) continue;
					bool canOwnThis = false;
					var ub = cgo.GetComponent<VRC.Udon.UdonBehaviour>();
					if (ub != null && (HasSync(ub) || ub.IsNetworkingSupported)) canOwnThis = true;
					else
					{
						var comps = cgo.GetComponents<Component>();
						if (comps != null)
						{
							foreach (var c in comps)
							{
								if (c == null) continue;
								string n;
								try { n = MenuCard.Il2CppNameOf(c); } catch { continue; }
								if (string.IsNullOrEmpty(n)) continue;
								if (n.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) >= 0
								 || n.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0
								 || n.IndexOf("UdonSync", StringComparison.OrdinalIgnoreCase) >= 0) { canOwnThis = true; break; }
							}
						}
					}

					if (canOwnThis)
					{
						if (!VRC.SDKBase.Networking.IsOwner(me, cgo))
						{
							VRC.SDKBase.Networking.SetOwner(me, cgo);
						}
						took = true;
					}
				}
				return took;
			}
			catch { return false; }
		}

		// Manual or Continuous: the behaviour has synced variables and therefore network state of its own.
		public static bool HasSync(Behaviour b)
		{
			try
			{
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				object sm = ub?.GetProperty("SyncMethod")?.GetValue(b);
				if (sm == null) return false;
				string n = sm.ToString();
				return n == "Manual" || n == "Continuous";
			}
			catch { return false; }
		}

		// TAKE THE OBJECT. Networking.SetOwner(localPlayer, go): the SDK call a world uses to hand an
		// object to a player. After it, our writes to its synced variables are the ones VRChat keeps, and
		// RUN ON OWNER runs on us. A networked act, so it is a button of its own and never implicit --
		// except under SET & SYNC, which is exactly "make my write stick" and says so.
		public static bool TakeOwnership(Entry e)
		{
			try
			{
				if (e == null || e.B == null) { Status = "no script selected"; return false; }
				if (!CanOwn(e)) { Status = Trunc(e.Short, 28) + " has no network state - nothing to own"; return false; }
				var go = e.B.gameObject;
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (go == null || me == null) { Status = "no local player yet"; return false; }
				if (VRC.SDKBase.Networking.IsOwner(me, go)) { RefreshOwner(e); Status = "you already own " + Trunc(e.Short, 28); return true; }
				Probe("SetOwner on " + e.Path);
				VRC.SDKBase.Networking.SetOwner(me, go);
				RefreshOwner(e);
				Status = (e.Mine ? "you now own " : "ownership requested for ") + Trunc(e.Short, 28);
				VRChatArchiveModPlugin.Logger.LogInfo("[UdonManager] SetOwner -> local on " + e.Path + " (mine=" + e.Mine + ")");
				return true;
			}
			catch (Exception ex) { Status = "take ownership failed: " + ex.Message; return false; }
		}

		// PUSH THE SYNCED VARIABLES NOW. UdonBehaviour.RequestSerialization(): a Manual-sync script never
		// sends otherwise; on Continuous it is harmless. Only meaningful when we own the object.
		public static bool RequestSerialization(Entry e)
		{
			try
			{
				if (e == null || e.B == null) return false;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				var rs = ub?.GetMethod("RequestSerialization", Type.EmptyTypes);
				if (rs == null) { Status = "RequestSerialization not found on this build"; return false; }
				Probe("RequestSerialization on " + e.Path);
				rs.Invoke(e.B, null);
				return true;
			}
			catch (Exception ex) { Status = "serialization failed: " + ex.Message; return false; }
		}

		// RUN ON THE OWNER: SendCustomNetworkEvent(NetworkEventTarget.Owner, ev). The event runs on
		// the client of whoever owns the object -- and only there. This is how a world targets ONE
		// player with Udon, so it is how the manager does it too. Same '_' rule as RunGlobal.
		private static void ResolveNet()
		{
			_netResolved = true;
			try
			{
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				_netTarget = FindType("VRC.Udon.Common.Interfaces.NetworkEventTarget");
				if (ub != null && _netTarget != null)
					foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.Instance))
					{
						if (m.Name != "SendCustomNetworkEvent") continue;
						var ps = m.GetParameters();
						if (ps.Length == 2 && ps[1].ParameterType == typeof(string)) { _sendNet = m; break; }
					}
			}
			catch { }
		}

		public static bool RunOwner(Entry e, string ev)
		{
			try
			{
				if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return false;
				if (ev[0] == '_') { Status = "'" + Trunc(ev, 22) + "' is local-only — VRChat blocks networking it"; return false; }
				if (!_netResolved) ResolveNet();
				if (_sendNet == null || _netTarget == null) { Status = "network event API not found"; return false; }
				object owner;
				try { owner = Enum.Parse(_netTarget, "Owner"); }
				catch { owner = Enum.ToObject(_netTarget, 1); }   // All=0, Owner=1 in VRC.Udon.Common.Interfaces.NetworkEventTarget
				_sendNet.Invoke(e.B, new object[] { owner, ev });
				string who = string.IsNullOrEmpty(e.Owner) ? "the owner" : e.Owner;
				Status = "sent " + Trunc(ev, 26) + " to " + who;
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] OWNER SendCustomNetworkEvent '{ev}' on {e.Path} (owner: {who})");
				return true;
			}
			catch (Exception ex)
			{
				Status = "owner event failed: " + ex.Message;
				return false;
			}
		}

		public static bool RunGlobal(Entry e, string ev)
		{
			try
			{
				if (!_netResolved) ResolveNet();
				if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return false;
				if (ev[0] == '_') { Status = "'" + Trunc(ev, 22) + "' is local-only — VRChat blocks networking it"; return false; }

				if (_sendNet == null || _netTarget == null) { Status = "network event API not found"; return false; }

				object all;
				try { all = Enum.Parse(_netTarget, "All"); }
				catch { all = Enum.ToObject(_netTarget, 0); }   // All is the first value of the enum

				_sendNet.Invoke(e.B, new object[] { all, ev });
				Status = "sent " + Trunc(ev, 26) + " to EVERYONE";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] GLOBAL SendCustomNetworkEvent '{ev}' on {e.Path}");
				return true;
			}
			catch (Exception ex)
			{
				Status = "global event failed: " + ex.Message;
				return false;
			}
		}

		public static bool RunGlobalDirect(VRC.Udon.UdonBehaviour ub, string ev)
		{
			try
			{
				if (ub == null || string.IsNullOrEmpty(ev)) return false;
				if (ev[0] == '_') return false;
				if (!_netResolved) ResolveNet();
				if (_sendNet == null || _netTarget == null) return false;

				object all;
				try { all = Enum.Parse(_netTarget, "All"); }
				catch { all = Enum.ToObject(_netTarget, 0); }

				_sendNet.Invoke(ub, new object[] { all, ev });
				return true;
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] RunGlobalDirect failed: {ex.Message}");
				return false;
			}
		}

		// FIRE ONE EVENT ON EVERY SCRIPT WHOSE OBJECT NAME MATCHES A PATTERN — the engine behind the
		// client's world presets. "Break Doors" is one event (SyncOpenR) on every object called
		// Door*, "Kill All" is Damage250 on every PlayerData*. It never invents a name: it matches
		// against the leaf names the LAST scan actually found, so a preset whose object is not in
		// this world simply hits nothing and says so — it can never fire the wrong event at the wrong
		// object.
		//
		// PACED, NOT BURSTED. The matching happens here; the FIRING does NOT. A global preset in a
		// 200-door world would be 200 SendCustomNetworkEvent calls in one frame — the very burst that
		// disconnects you (badapple-network-budget). So the caller (ModControl) fires the matched
		// list one object per paced main-thread step. MatchEntries returns the objects; RunOne fires
		// exactly one; MatchBegin / RunOneTracked keep the "N of M" progress the client shows.
		//
		// SCOPE IS A CLOSED SET. Anything that is not exactly global/local/owner/interact is REFUSED,
		// not quietly turned into a global broadcast: a preset file with a mistyped scope must never
		// become the most network-impactful path (the same rule udonRun/udonRunGlobal keep).
		public static bool IsScope(string scope)
			=> string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(scope, "owner", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(scope, "interact", StringComparison.OrdinalIgnoreCase);

		public static List<Entry> MatchEntries(string pattern, string match)
		{
			var outp = new List<Entry>();
			try
			{
				foreach (var e in Snapshot())
					if (e != null && e.B != null && NameMatches(e.Short ?? "", pattern, match)) outp.Add(e);
			}
			catch { }
			return outp;
		}

		private static int _mDone, _mHit, _mTotal;
		private static string _mWhat = "", _mPat = "";

		public static void MatchBegin(int total, string what, string pattern)
		{
			_mTotal = total; _mDone = 0; _mHit = 0; _mWhat = what ?? ""; _mPat = pattern ?? "";
			Status = total == 0
				? "no object named like '" + Trunc(_mPat, 22) + "' in this world — press SCAN, or the world renamed them"
				: "firing " + Trunc(_mWhat, 20) + " on " + total + " '" + Trunc(_mPat, 16) + "' object(s)…";
		}

		// Fires ONE matched object for the given scope, and updates the running "N of M" line. Unknown
		// scope is refused here too, as a second guard behind IsScope.
		public static bool RunOneTracked(string scope, string ev, Entry e)
		{
			bool ok = false;
			try
			{
				if (e != null && e.B != null)
				{
					if (string.Equals(scope, "interact", StringComparison.OrdinalIgnoreCase)) ok = Interact(e);
					else if (string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase)) ok = RunLocal(e, ev);
					else if (string.Equals(scope, "owner", StringComparison.OrdinalIgnoreCase)) ok = RunOwner(e, ev);
					else if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)) ok = RunGlobal(e, ev);
				}
			}
			catch { }
			_mDone++;
			if (ok) _mHit++;
			Status = "ran " + Trunc(_mWhat, 20) + " on " + _mHit + " of " + _mTotal + " '" + Trunc(_mPat, 16) + "' object(s)";
			return ok;
		}

		// FIRE ONE EVENT AIMED AT ONE PLAYER. There is no generic "run this on that player" in Udon —
		// a world targets a player through its OWN objects (a per-player node, a health script, a
		// grenade it moves onto them). So this does the honest, world-agnostic thing: it fires the
		// event on the matching Udon object CLOSEST to the player. "by" chooses what "closest" means:
		//   near  — nearest to the player's own position (a script that follows them, e.g. their body/data)
		//   label — nearest to the world-UI text that shows their name (a player-list node)
		// pattern/match narrow which objects count (e.g. only "PlayerData*"); empty pattern = any script.
		// It reuses the last scan's objects, so it can only ever fire on something that exists, and the
		// status line says exactly what it resolved to.
		public static int RunOnPlayer(string displayName, Vector3 playerPos, bool hasPos, string scope, string ev, string pattern, string match, string by)
			=> RunOnPlayer(displayName, playerPos, hasPos, scope, ev, pattern, match, by, "", -1);

		public static int RunOnPlayer(string displayName, Vector3 playerPos, bool hasPos, string scope, string ev, string pattern, string match, string by, string byVar, int playerId)
		{
			try
			{
				// BY VARIABLE — the only correct answer for a world that keeps its per-player scripts in
				// one folder instead of on the players.
				//
				// The two modes below are both GEOMETRIC: they pick the matching object closest to the
				// player, or closest to a UI label showing their name. That works when a world puts a
				// script on each person. It is meaningless when it does not, and Among Us is the case
				// that proved it: its thirty "Player Node (N)" all hang off Game Logic/Player Nodes, at
				// effectively one place, and each says which person it belongs to in a VARIABLE —
				// playerID, the VRChat actor number. "Nearest node to their nameplate" there resolves to
				// an arbitrary node, so "kill this player" would kill somebody else and report success.
				//
				// So this asks the object instead of measuring it. No distance, no MaxReach, no guess:
				// either a candidate's variable holds this player's id or none does, and if none does
				// the honest answer is that the world does not have a node for them right now.
				if (string.Equals(by, "var", StringComparison.OrdinalIgnoreCase))
					return RunOnPlayerByVar(displayName, scope, ev, pattern, match, byVar, playerId);

				bool byLabel = string.Equals(by, "label", StringComparison.OrdinalIgnoreCase);
				Vector3 target = playerPos;
				if (byLabel)
				{
					if (TryFindNameLabel(displayName, out Vector3 lp)) target = lp;
					else { Status = "no world label showing '" + Trunc(displayName, 18) + "' — is their name in the world's player list?"; return 0; }
				}
				else if (!hasPos)
				{
					// NEVER AIM AT THE ORIGIN. A player still resolving after a join reports no position, and
					// PlayerEntry.Position falls back to (0,0,0); "nearest to that" would be whatever sits at the
					// world origin — another player's node, a shared manager — reported as a success.
					Status = Trunc(displayName, 18) + " has no position yet — try again in a second";
					return 0;
				}

				var cands = string.IsNullOrEmpty(pattern) ? Snapshot() : MatchEntries(pattern, match);
				Entry best = null; float bestD = float.MaxValue;
				foreach (var e in cands)
				{
					if (e == null || e.B == null) continue;
					Vector3 ep;
					try { var t = e.B.transform; if (t == null || !Core.NativeGuard.Alive(t)) continue; ep = t.position; }
					catch { continue; }
					float d = Vector3.Distance(target, ep);
					if (d < bestD) { bestD = d; best = e; }
				}
				// A DISTANCE FLOOR. "Nearest" with no limit is "the one and only such object, wherever it is":
				// aiming at two different players would hit the same shared node and call both a success.
				// Past this radius the object is not that player's, and the honest answer is a refusal.
				const float MaxReach = 30f;
				if (best == null || bestD > MaxReach)
				{
					Status = best == null
						? (string.IsNullOrEmpty(pattern)
							? "no world script found near " + Trunc(displayName, 18)
							: "no '" + Trunc(pattern, 16) + "' object near " + Trunc(displayName, 18) + " — press SCAN, or check the name")
						: "nearest '" + Trunc(pattern.Length > 0 ? pattern : best.Short, 16) + "' is " + bestD.ToString("0", CultureInfo.InvariantCulture) + " m from " + Trunc(displayName, 18) + " — not theirs, nothing sent";
					return 0;
				}

				bool ok;
				if (string.Equals(scope, "interact", StringComparison.OrdinalIgnoreCase)) ok = Interact(best);
				else if (string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase)) ok = RunLocal(best, ev);
				else if (string.Equals(scope, "owner", StringComparison.OrdinalIgnoreCase)) ok = RunOwner(best, ev);
				else if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)) ok = RunGlobal(best, ev);
				else { Status = "unknown scope '" + scope + "'"; return 0; }

				Status = ok
					? "sent " + Trunc(string.Equals(scope, "interact", StringComparison.OrdinalIgnoreCase) ? "interact" : ev, 20) + " to " + Trunc(displayName, 16) + " via " + Trunc(best.Short, 20) + " (" + bestD.ToString("0.0", CultureInfo.InvariantCulture) + " m)"
					: "found " + Trunc(best.Short, 20) + " for " + Trunc(displayName, 16) + " but the event did not fire";
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] player event {scope} '{ev}' -> {displayName} via {best.Short} at {bestD:0.0} m (by {by})");
				return ok ? 1 : 0;
			}
			catch (Exception ex) { Status = "player event failed: " + ex.Message; return 0; }
		}

		// Picks the candidate whose variable <byVar> holds this player's actor number, and fires there.
		//
		// Reads through GetProgramVariable, the same non-generic overload Variables() uses, and ONLY on
		// behaviours that are active and enabled: asking a behaviour that never ran forces its Udon
		// program to deserialise, which is the 0.1-3 s per script that made DUMP ALL unusable. A
		// per-player node in a world you are standing in is live, so nothing is lost by skipping the
		// dead ones.
		private static int RunOnPlayerByVar(string displayName, string scope, string ev, string pattern, string match, string byVar, int playerId)
		{
			if (string.IsNullOrEmpty(byVar))
			{
				Status = "this action says by:var but names no variable (byVar) — nothing sent";
				VRChatArchiveModPlugin.Logger.LogWarning("[UdonManager] by:var with no byVar — nothing sent.");
				return 0;
			}
			if (playerId < 0)
			{
				Status = "no actor number for " + Trunc(displayName, 18) + " yet — try again in a second";
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[UdonManager] by:var: no actor number for " + displayName + " (PlayerEntry.PlayerId is -1) "
					+ "— VRCPlayerApi.playerId never read. Nothing sent.");
				return 0;
			}

			MethodInfo getVar = _getVar;
			if (getVar == null)
			{
				try
				{
					Type ubT = FindType("VRC.Udon.UdonBehaviour");
					if (ubT != null)
						foreach (var m in ubT.GetMethods(BindingFlags.Public | BindingFlags.Instance))
						{
							if (m.Name != "GetProgramVariable" || m.IsGenericMethodDefinition) continue;
							var ps = m.GetParameters();
							if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { _getVar = m; break; }
						}
				}
				catch { }
				getVar = _getVar;
			}
			if (getVar == null)
			{
				Status = "cannot read Udon variables on this build — nothing sent";
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[UdonManager] by:var: GetProgramVariable(string) not found on UdonBehaviour — nothing sent.");
				return 0;
			}

			var cands = string.IsNullOrEmpty(pattern) ? Snapshot() : MatchEntries(pattern, match);
			Entry hit = null;
			int looked = 0, readable = 0, asleep = 0;
			var seen = new List<int>();
			foreach (var e in cands)
			{
				if (e == null || e.B == null) continue;
				looked++;
				if (!IsStarted(e)) { asleep++; continue; }
				object raw;
				try { raw = getVar.Invoke(e.B, new object[] { byVar }); }
				catch { continue; }
				if (raw == null) continue;
				readable++;
				// UNBOX THE IL2CPP WAY. GetProgramVariable returns a BOXED il2cpp value, so
				// Convert.ToInt32 threw on every single node and the catch read it as "not a
				// number" — 30 readable candidates and not one usable value. Render() is the
				// class's own safe unboxer (dispatch on the il2cpp class name, then Unbox<T>),
				// and it already covers every integer width.
				int got;
				string shown;
				try { shown = Render(raw); }
				catch { continue; }
				if (!int.TryParse(shown, NumberStyles.Integer, CultureInfo.InvariantCulture, out got)) continue;
				if (seen.Count < 40) seen.Add(got);
				if (got != playerId) continue;
				hit = e; break;
			}

			// ALWAYS SAY WHAT WAS COMPARED. Four of the returns above used to set Status — a UI
			// string — and write nothing to the log, so a per-player action that resolved to nothing
			// looked exactly like one nobody pressed. This line also settles the open question in a
			// single run: whether the world's variable holds VRChat's ACTOR NUMBER or its own 0..N
			// seat index. Small dense values against a large target id means a seat index, and no
			// dump can reveal that — every node reads -1 while nobody is playing.
			VRChatArchiveModPlugin.Logger.LogInfo(
				"[UdonManager] by:var " + byVar + " looking for " + playerId + " (" + Trunc(displayName, 18) + "): "
				+ looked + " candidate(s), " + asleep + " not started, " + readable + " readable, values ["
				+ string.Join(",", seen.ToArray()) + "]" + (hit != null ? " -> MATCHED " + hit.Short : " -> NO MATCH"));

			if (hit == null)
			{
				// EVERY CANDIDATE DORMANT is its own answer, and a different one from "nobody
				// claims this player". A world keeps its per-player scripts switched off until a
				// round starts, and nothing can be read from them until then — saying "no match"
				// there sends you hunting for a bug that is not in the mod.
				if (asleep > 0 && readable == 0)
				{
					Status = "all " + asleep + " '" + Trunc(pattern.Length > 0 ? pattern : "script", 16)
						+ "' are switched off right now — this world only wakes them during a round";
					VRChatArchiveModPlugin.Logger.LogWarning("[UdonManager] by:var: " + Status);
					return 0;
				}
				Status = readable == 0
					? "no '" + Trunc(pattern.Length > 0 ? pattern : "script", 16) + "' exposes " + Trunc(byVar, 14)
					  + " (looked at " + looked + ") — press SCAN, or check the action's byVar"
					: "no " + Trunc(pattern.Length > 0 ? pattern : "script", 16) + " has " + Trunc(byVar, 14) + " = "
					  + playerId + " — " + Trunc(displayName, 16) + " may not be in the world's game right now";
				return 0;
			}

			bool ok;
			if (string.Equals(scope, "interact", StringComparison.OrdinalIgnoreCase)) ok = Interact(hit);
			else if (string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase)) ok = RunLocal(hit, ev);
			else if (string.Equals(scope, "owner", StringComparison.OrdinalIgnoreCase)) ok = RunOwner(hit, ev);
			else if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)) ok = RunGlobal(hit, ev);
			else { Status = "unknown scope '" + scope + "'"; return 0; }

			Status = ok
				? "sent " + Trunc(ev, 20) + " to " + Trunc(displayName, 16) + " via " + Trunc(hit.Short, 20) + " (" + byVar + "=" + playerId + ")"
				: "found " + Trunc(hit.Short, 20) + " for " + Trunc(displayName, 16) + " but the event did not fire";
			VRChatArchiveModPlugin.Logger.LogInfo(
				$"[UdonManager] player event {scope} '{ev}' -> {displayName} via {hit.Short} matched on {byVar}={playerId}");
			return ok ? 1 : 0;
		}

		// The world position of a UI label whose text equals the player's name — UnityEngine.UI.Text
		// first, then TextMeshPro. Read on demand (a button press), never on a timer.
		private static bool TryFindNameLabel(string displayName, out Vector3 pos)
		{
			pos = Vector3.zero;
			if (string.IsNullOrEmpty(displayName)) return false;
			string want = displayName.Trim();
			try
			{
				var tType = Il2CppType.Of<UnityEngine.UI.Text>();
				var found = VRChatArchiveMod.Core.Live.AllOfType(tType, FindObjectsSortMode.None);
				if (found != null)
					for (int i = 0; i < found.Length; i++)
					{
						var txt = found[i] != null ? found[i].TryCast<UnityEngine.UI.Text>() : null;
						if (txt == null || !Core.NativeGuard.Alive(txt)) continue;
						string s; try { s = txt.text; } catch { continue; }
						if (!string.IsNullOrEmpty(s) && string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase))
						{ try { pos = txt.transform.position; return true; } catch { } }
					}
			}
			catch { }
			try
			{
				var tType = Il2CppType.Of<TMPro.TMP_Text>();
				var found = VRChatArchiveMod.Core.Live.AllOfType(tType, FindObjectsSortMode.None);
				if (found != null)
					for (int i = 0; i < found.Length; i++)
					{
						var txt = found[i] != null ? found[i].TryCast<TMPro.TMP_Text>() : null;
						if (txt == null || !Core.NativeGuard.Alive(txt)) continue;
						string s; try { s = txt.text; } catch { continue; }
						if (!string.IsNullOrEmpty(s) && string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase))
						{ try { pos = txt.transform.position; return true; } catch { } }
					}
			}
			catch { }
			return false;
		}

		private static bool NameMatches(string name, string pattern, string match)
		{
			if (string.IsNullOrEmpty(pattern)) return false;
			switch ((match ?? "contains").ToLowerInvariant())
			{
				case "exact": return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
				case "prefix": return name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
				default: return name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
			}
		}

		// PRESS THE OBJECT'S INTERACT BUTTON. UdonBehaviour.Interact() is the exact call VRChat makes
		// when you look at an interactable and press the interact key: it fires the world's own
		// _interact handler. That handler is where a world button does its work \u2014 and a well-made
		// button broadcasts its effect over the network ITSELF, so pressing it here lands for EVERYONE
		// without us touching VRChat's networking directly. This is the "global interact": _interact
		// is an underscore event SendCustomNetworkEvent refuses, but Interact() is the sanctioned door
		// to it and the world networks the result.
		//
		// It bypasses only the WALK and the AIM, not the world's logic: whatever the button checks
		// (ownership, cooldown, game state) still runs. So it drives shared state a normal press would
		// \u2014 open a door, start a game \u2014 and nothing a normal press could not.
		private static MethodInfo _interactMi;
		private static bool _interactResolved;

		public static bool Interact(Entry e)
		{
			try
			{
				if (e == null || e.B == null) { Status = "no script selected"; return false; }
				var go = e.B.gameObject;
				if (go == null) { Status = "no gameobject"; return false; }

				// DIAGNOSTIC: what IS this object? Log its components so a press that does nothing can be
				// told apart \u2014 a world-space _interact, a uGUI Button, or neither.
				try
				{
					string names = "";
					var comps = go.GetComponents<Component>();
					if (comps != null)
						foreach (var c in comps)
						{
							if (c == null) continue;
							try { names += (MenuCard.Il2CppNameOf(c) ?? c.GetType().Name) + " "; } catch { }
						}
					VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] INTERACT {e.Path} :: components = {names}");
				}
				catch { }

				// 1) uGUI BUTTON ON THE SCRIPT'S OWN OBJECT first. A settings panel, a bloom slider, a
				//    world menu \u2014 these are Canvas buttons whose action is an onClick (usually wired to
				//    SendCustomEvent on an Udon script), NOT the world-space _interact.
				//    UdonBehaviour.Interact() does nothing for them, which is why pressing INTERACT
				//    looked dead. onClick.Invoke() is the real press.
				try
				{
					var btn = go.GetComponent<UnityEngine.UI.Button>();
					if (btn != null && btn.onClick != null)
					{
						if (!btn.interactable) VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] Button on {e.Path} is not interactable; clicking anyway.");
						btn.onClick.Invoke();
						Status = "clicked UI button " + Trunc(e.Short, 28);
						VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] uGUI Button.onClick.Invoke() on {e.Path}");
						return true;
					}
				}
				catch (Exception bex) { VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] Button click failed on {e.Path}: {bex.Message}"); }

				// 1b) uGUI TOGGLE ON THE SCRIPT'S OWN OBJECT. Many menus (settings, switches, options)
				//     use Toggle components whose onValueChanged drives the Udon script.
				try
				{
					var tog = go.GetComponent<UnityEngine.UI.Toggle>();
					if (tog != null)
					{
						tog.isOn = !tog.isOn;
						Status = (tog.isOn ? "enabled toggle " : "disabled toggle ") + Trunc(e.Short, 28);
						VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] uGUI Toggle.isOn={tog.isOn} on {e.Path}");
						return true;
					}
				}
				catch (Exception tex) { VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] Toggle click failed on {e.Path}: {tex.Message}"); }

				// 2) ANY uGUI BUTTON OR TOGGLE WIRED TO THIS SCRIPT. The usual layout is a Button or Toggle
				//    on a menu object whose onClick/onValueChanged targets an UdonBehaviour living SOMEWHERE
				//    ELSE in the hierarchy — the script the user selected has no Button/Toggle of its own, so
				//    stage 1 finds nothing and Interact() below is a no-op for it. The persistent listeners
				//    of every scene Button and Toggle name their target object; the ones aimed at OUR behaviour
				//    (same instance id) are the controls that actually drive it, so those are triggered.
				//    Same filters as Rescan: no assets (HideAndDontSave), no unloaded-scene objects.
				try
				{
					int clicked = 0;
					var allBtn = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(UnityEngine.UI.Button)));
					if (allBtn != null)
						for (int i = 0; i < allBtn.Length; i++)
						{
							try
							{
								var btn = allBtn[i]?.TryCast<UnityEngine.UI.Button>();
								if (btn == null || btn.hideFlags == HideFlags.HideAndDontSave) continue;
								var bgo = btn.gameObject;
								if (bgo == null) continue;
								Scene sc;
								try { sc = bgo.scene; } catch { continue; }
								if (!sc.IsValid() || !sc.isLoaded) continue;
								var ev = btn.onClick;
								if (ev == null) continue;
								int n = ev.GetPersistentEventCount();
								for (int k = 0; k < n; k++)
								{
									var target = ev.GetPersistentTarget(k);
									if (target == null || target.GetInstanceID() != e.Id) continue;
									if (!btn.interactable) VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] Button {PathOf(btn.transform)} is not interactable; clicking anyway.");
									btn.onClick.Invoke();
									clicked++;
									VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] uGUI Button {PathOf(btn.transform)} \u2192 {ev.GetPersistentMethodName(k)} on {e.Path}");
									break;
								}
							}
							catch { }
						}

					var allTog = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(UnityEngine.UI.Toggle)));
					if (allTog != null)
						for (int i = 0; i < allTog.Length; i++)
						{
							try
							{
								var tog = allTog[i]?.TryCast<UnityEngine.UI.Toggle>();
								if (tog == null || tog.hideFlags == HideFlags.HideAndDontSave) continue;
								var tgo = tog.gameObject;
								if (tgo == null) continue;
								Scene sc;
								try { sc = tgo.scene; } catch { continue; }
								if (!sc.IsValid() || !sc.isLoaded) continue;
								var ev = tog.onValueChanged;
								if (ev == null) continue;
								int n = ev.GetPersistentEventCount();
								for (int k = 0; k < n; k++)
								{
									var target = ev.GetPersistentTarget(k);
									if (target == null || target.GetInstanceID() != e.Id) continue;
									tog.isOn = !tog.isOn;
									clicked++;
									VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] uGUI Toggle {PathOf(tog.transform)} \u2192 {ev.GetPersistentMethodName(k)} on {e.Path}");
									break;
								}
							}
							catch { }
						}

					if (clicked > 0)
					{
						Status = "clicked/toggled " + clicked + " UI control(s) wired to " + Trunc(e.Short, 24);
						return true;
					}
				}
				catch (Exception sex) { VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] UI controls scan failed for {e.Path}: {sex.Message}"); }

				// 3) WORLD-SPACE interactable (_interact) \u2014 doors, levers, pickups. Interact() only
				//    fires a script's _interact entry point, so when the export list is readable and
				//    has none, say so instead of pressing a door that is not there.
				if (!_interactResolved)
				{
					_interactResolved = true;
					Type ub = FindType("VRC.Udon.UdonBehaviour");
					if (ub != null)
						foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
							if (m.Name == "Interact" && m.GetParameters().Length == 0) { _interactMi = m; break; }
				}
				if (_interactMi == null)
				{
					Status = "no UI Button here, and Interact() not found";
					VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] {e.Path}: no uGUI Button and no Interact() method.");
					return false;
				}
				var eps = EntryPoints(e);
				if (eps.Count > 0 && !eps.Contains("_interact"))
				{
					Status = Trunc(e.Short, 24) + " has no _interact and no UI button drives it \u2014 nothing to press";
					VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] {e.Path}: no _interact entry point, no wired button.");
					return false;
				}
				_interactMi.Invoke(e.B, null);
				Status = eps.Count == 0
					? "tried Interact() on " + Trunc(e.Short, 24) + " (entry points unknown)"
					: "interacted with " + Trunc(e.Short, 32);
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] Interact() on {e.Path}");
				return true;
			}
			catch (Exception ex)
			{
				Status = "interact failed: " + ex.Message;
				VRChatArchiveModPlugin.Logger.LogWarning($"[UdonManager] interact threw on {e?.Path}: {ex.Message}");
				return false;
			}
		}

		// WHAT DID THE EVENT DO? Snapshot the behaviour's variables, run the entry point LOCALLY,
		// snapshot again, and report what moved. This is the empirical answer the mod can give
		// without decompiling the world: fire "_OpenDoor" and watch "doorOpen" flip false->true.
		// LOCAL only (SendCustomEvent): a networked run's effects land on OTHER clients, so there
		// would be nothing here to diff. SYNCHRONOUS only — a variable a coroutine sets a frame later
		// is not caught, and the diff honestly says "no change" rather than guessing.
		public sealed class VarChange { public string Name; public string Before; public string After; }

		public static List<VarChange> RunAndDiff(Entry e, string ev)
		{
			var changes = new List<VarChange>();
			if (e == null || e.B == null || string.IsNullOrEmpty(ev)) return changes;
			var before = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var v in Variables(e)) before[v.Name] = v.Value;
			RunLocal(e, ev);
			foreach (var v in Variables(e))
			{
				string b;
				bool had = before.TryGetValue(v.Name, out b);
				if (!had || !string.Equals(b, v.Value, StringComparison.Ordinal))
					changes.Add(new VarChange { Name = v.Name, Before = had ? b : "(new)", After = v.Value });
			}
			Status = changes.Count == 0
				? "ran " + Trunc(ev, 24) + " locally — no variable changed"
				: "ran " + Trunc(ev, 24) + " — " + changes.Count + " variable(s) changed";
			return changes;
		}

		// ------------------------------------------------------------------ variables
		//
		// THE STATE, beside the events. Switching a script off tells you what it stops doing; its
		// variables tell you what it THINKS — which door is open, whose turn it is, how many points are
		// on the board. That is the difference between silencing a world and understanding it.
		//
		// PUBLIC VARIABLES ONLY. They are the set a world author chose to expose, they are the set
		// VRChat keeps synced, and they are the one table reachable without walking the compiled
		// program. A private symbol is left out rather than guessed at.
		public sealed class Var
		{
			public string Name;
			public string Type;      // the declared type's short name, "?" when it will not say
			public string Value;     // a SAFE rendering — see Render; never a call into a live object
			public bool Editable;    // text can become this type, AND a setter was actually resolved
			public bool Synced;      // an [UdonSynced] symbol: SET alone is undone by the owner, SET & SYNC is the real write
		}

		private static MethodInfo _getVar;    // GetProgramVariable(string) — the non-generic overload

		// HAS THIS BEHAVIOUR STARTED? GetProgramVariable / the program's symbol table on a behaviour that
		// never ran force its Udon program to initialise (deserialise + build the heap): 0.1-3 s each, and
		// a hidden UI page can hold hundreds of them. The dump asks first. Resolved once by shape: a bool
		// member that says "initialized" (VRChat's own flag, whatever this build calls it); when the build
		// exposes none, "active and enabled" is the safe stand-in — a wrong "false" only costs values.
		// METHOD-BACKED ONLY. The first version found VRChat's private `_initialized` and read it through
		// the interop proxy — a FIELD read, i.e. the field-offset trap (2026-08-28): the offset resolves
		// wrong on this build, the read lands outside the object, and the AccessViolation cannot be
		// caught: VRChat closed the instant DUMP ALL touched its first script (2026-09-05 01:4x).
		// isActiveAndEnabled is a native METHOD call and is enough: a behaviour that is active and
		// enabled has run Start, so its live program is there to read from.
		public static bool IsStarted(Entry e)
		{
			try { return e != null && e.B != null && e.B.isActiveAndEnabled; }
			catch { return false; }
		}


		public static List<Var> Variables(Entry e)
		{
			var outp = new List<Var>();
			try
			{
				if (e == null || e.B == null) return outp;
				RefreshOwner(e);
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) return outp;

				object table = ub.GetProperty("publicVariables")?.GetValue(e.B);
				if (table == null) return outp;
				object symbols = table.GetType().GetProperty("VariableSymbols")?.GetValue(table);
				if (symbols == null) return outp;

				// GetProgramVariable(string) is NOT found with GetMethod(name, types): the interop
				// type carries a generic GetProgramVariable<T>(string) beside it and the strict lookup
				// throws AmbiguousMatchException — which the catch below turned into an empty table,
				// every time. Scanned once by shape instead (non-generic, one string parameter), like
				// ResolveSetter. Resolution failures are recorded and the symbol loop still runs, so
				// the names and types are listed even when a value cannot be read.
				MethodInfo getType = null;
				try
				{
					if (_getVar == null)
						foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.Instance))
						{
							if (m.Name != "GetProgramVariable" || m.IsGenericMethodDefinition) continue;
							var ps = m.GetParameters();
							if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { _getVar = m; break; }
						}
					getType = ub.GetMethod("GetProgramVariableType", new[] { typeof(string) });
					ResolveSetter(ub);
				}
				catch (Exception rex) { Status = "variable API only partly resolved: " + rex.Message; }
				var getVar = _getVar;

				Probe("variables: reading symbols of " + e.Path);
				// EXPORTED SYMBOLS FIRST (a string[] off the program's symbol table); the variable table's
				// KeyCollection only as a fallback, and then only through CopyTo -- see Core.UdonSymbols
				// and Core.Il2CppSeq for the crash both of these replace.
				var names = Core.UdonSymbols.Exported(e.B);
				string via = "UdonSymbols." + Core.UdonSymbols.LastPath;
				if (names.Count == 0) { names = new List<string>(Strings(symbols)); via = "Il2CppSeq." + Core.Il2CppSeq.LastPath + " (UdonSymbols gave nothing: " + Core.UdonSymbols.LastPath + ")"; }
				Probe("variables: " + names.Count + " symbol(s) via " + via);
				// Which symbols are [UdonSynced], from the program's sync metadata table (empty when the
				// route is closed on this build; then no SET & SYNC is offered rather than a wrong one).
				var synced = Core.UdonSymbols.Synced(e.B);
				int probed = 0;
				foreach (string sym in names)
				{
					if (probed++ < 2) Probe("variables: type+value of " + sym);
					if (string.IsNullOrEmpty(sym)) continue;
					if (outp.Count >= 200) break;   // a table this long is a generated one, not a read

					string tn = "?";
					try { tn = TypeNameOf(getType?.Invoke(e.B, new object[] { sym })); } catch { }

					object val = null;
					try { val = getVar?.Invoke(e.B, new object[] { sym }); } catch { }

					outp.Add(new Var
					{
						Name = sym,
						Type = tn,
						Value = Render(val),
						Editable = CanWrite && Parsable(tn),
						Synced = synced.Contains(sym),
					});
				}
			}
			catch (Exception ex) { Status = "could not read its variables: " + ex.Message; }
			return outp;
		}

		// WRITES LOCALLY, like everything else on this page. A synced variable belongs to whoever owns
		// the object: if that is not you, the owner's next sync tick overwrites what you wrote and the
		// value snaps back. That is VRChat working correctly rather than the write failing — and taking
		// ownership to force it through is a networked act this does not perform.
		public static bool SetVariable(Entry e, string name, string text, bool sync = false)
		{
			try
			{
				if (e == null || e.B == null || string.IsNullOrEmpty(name)) return false;
				Type ub = FindType("VRC.Udon.UdonBehaviour");
				if (ub == null) return false;
				ResolveSetter(ub);
				if (!CanWrite) { Status = "this build exposes no way to write variables"; return false; }

				string tn = "?";
				try
				{
					var gt = ub.GetMethod("GetProgramVariableType", new[] { typeof(string) });
					tn = TypeNameOf(gt?.Invoke(e.B, new object[] { name }));
				}
				catch { }

				object boxed = ParseValue(tn, text, out Type managed);
				if (boxed == null) { Status = "cannot turn that into a " + tn; return false; }

				Probe("set " + name + " as " + managed.Name + " on " + e.Path);
				_setGeneric.MakeGenericMethod(managed).Invoke(e.B, new object[] { name, boxed });
				Probe("set ok");

				Status = "set " + Trunc(name, 28) + " = " + Trunc(text, 24);
				VRChatArchiveModPlugin.Logger.LogInfo($"[UdonManager] set {name} = {text} on {e.Path}");

				// SET & SYNC: make the write the one VRChat keeps. Ownership first (a synced variable belongs
				// to the owner; anybody else's write is undone on the owner's next tick), then a serialization
				// request so a Manual-sync script sends it now. Refused, with the reason, on an object that has
				// no network state -- SetOwner there is the crash VideoUrlModule documents.
				if (sync)
				{
					if (!CanOwn(e)) { Status = "set " + Trunc(name, 20) + " locally only: " + Trunc(e.Short, 20) + " has no network state to sync"; return true; }
					if (!e.Mine && !TakeOwnership(e)) return true;
					RequestSerialization(e);
					RefreshOwner(e);
					Status = "set " + Trunc(name, 24) + " = " + Trunc(text, 20) + (e.Mine ? " and synced it to everyone" : " (ownership still pending)");
				}
				return true;
			}
			catch (Exception ex) { Status = "write failed: " + ex.Message; return false; }
		}

		// WRITING IS RESOLVED, NOT ASSUMED. VRChat has shipped more than one shape of
		// SetProgramVariable, and Il2CppInterop does not always generate the overload a strict
		// GetMethod(name, types) asks for — the video-URL code already carries a fallback for exactly
		// that. So the setter is found by scanning, once, and what is found decides whether a SET
		// button is offered at all. No button beats a button that silently does nothing.
		//
		// ONLY THE GENERIC OVERLOAD IS USED. The plain SetProgramVariable(string, object) takes an
		// Il2CppSystem.Object, and a managed int/string is never an instance of that — so the old
		// IsInstanceOfType guard refused every value, and on a build with no generic overload the
		// page offered a SET button that could never write. Hand-boxing a managed primitive into an
		// il2cpp object is exactly the step that takes the process down when it is wrong; the generic
		// overload lets the interop layer do that marshalling. So SET is offered when, and only when,
		// SetProgramVariable<T> exists.
		// STEP PROBES (see VideoUrlModule.Probe): an access violation leaves no exception, only
		// the last log line. Emitted on user-triggered paths only, never on the scan.
		private static void Probe(string step)
		{
			try { VRChatArchiveModPlugin.Logger.LogInfo("[UdonManager] probe: " + step); } catch { }
		}

		private static bool _setterResolved;
		private static MethodInfo _setGeneric;    // SetProgramVariable<T>(string, T)

		public static bool CanWrite => _setGeneric != null;

		private static void ResolveSetter(Type ub)
		{
			if (_setterResolved) return;
			_setterResolved = true;
			try
			{
				foreach (var m in ub.GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (m.Name != "SetProgramVariable") continue;
					var ps = m.GetParameters();
					if (ps.Length != 2 || ps[0].ParameterType != typeof(string)) continue;
					if (m.IsGenericMethodDefinition) { if (_setGeneric == null) _setGeneric = m; }
				}
			}
			catch { }
		}

		// The types a text box can honestly produce. Everything else is shown and not offered for
		// editing — a world's variable can be a Transform, a material or a whole script, and none of
		// those come out of a line of text.
		private static bool Parsable(string typeName)
		{
			switch (typeName)
			{
				case "Boolean": case "String":
				case "Int32": case "UInt32": case "Int64": case "UInt64":
				case "Int16": case "UInt16": case "Byte": case "SByte":
				case "Single": case "Double":
					return true;
				default: return false;
			}
		}

		private static object ParseValue(string typeName, string text, out Type managed)
		{
			managed = null;
			text = text ?? "";
			var inv = CultureInfo.InvariantCulture;
			try
			{
				switch (typeName)
				{
					case "String":  managed = typeof(string); return text;
					case "Boolean": managed = typeof(bool);
						return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Trim() == "1";
					case "Int32":   managed = typeof(int);    return int.Parse(text, inv);
					case "UInt32":  managed = typeof(uint);   return uint.Parse(text, inv);
					case "Int64":   managed = typeof(long);   return long.Parse(text, inv);
					case "UInt64":  managed = typeof(ulong);  return ulong.Parse(text, inv);
					case "Int16":   managed = typeof(short);  return short.Parse(text, inv);
					case "UInt16":  managed = typeof(ushort); return ushort.Parse(text, inv);
					case "Byte":    managed = typeof(byte);   return byte.Parse(text, inv);
					case "SByte":   managed = typeof(sbyte);  return sbyte.Parse(text, inv);
					case "Single":  managed = typeof(float);  return float.Parse(text, NumberStyles.Float, inv);
					case "Double":  managed = typeof(double); return double.Parse(text, NumberStyles.Float, inv);
				}
			}
			catch { }
			managed = null;
			return null;
		}

		// GetProgramVariableType hands back a managed Type on some builds and an il2cpp Type proxy on
		// others, and the difference is not visible from here. Both answer to Name.
		private static string TypeNameOf(object t)
		{
			if (t == null) return "?";
			if (t is Type mt) return mt.Name ?? "?";
			try
			{
				string s = t.GetType().GetProperty("Name")?.GetValue(t) as string;
				if (!string.IsNullOrEmpty(s)) return s;
			}
			catch { }
			return "?";
		}

		// THE VALUE, OR THE TYPE — never a gamble. A managed primitive came back as itself and is
		// safe to print. On this build GetProgramVariable(string) never returns one, though: every
		// value arrives as an Il2CppSystem.Object proxy over a BOXED il2cpp primitive, which is why
		// the table used to print "<Int32>" and "<String>" for everything. So a live proxy is
		// unboxed by its il2cpp class name — reading the name is metadata only, and Unbox<T> copies
		// the payload out of an object we have just checked is alive. Anything that is not a boxed
		// primitive or a string is printed as what it IS and left alone: calling ToString() on a
		// proxy whose native object is gone is an access violation no try/catch here can survive.
		private static string Render(object val)
		{
			if (val == null) return "(null)";
			try
			{
				if (val is string s) return s.Length > 120 ? s.Substring(0, 119) + "…" : s;
				if (val is bool || val is int || val is uint || val is long || val is ulong
				 || val is short || val is ushort || val is byte || val is sbyte
				 || val is float || val is double)
					return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "?";
			}
			catch { }
			try
			{
				var o = val as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (o != null && Core.NativeGuard.Alive(o))
				{
					string n = MenuCard.Il2CppNameOf(o);
					var inv = CultureInfo.InvariantCulture;
					try
					{
						switch (n)
						{
							case "String":
							{
								var s = IL2CPP.Il2CppStringToManaged(o.Pointer) ?? "";
								return s.Length > 120 ? s.Substring(0, 119) + "…" : s;
							}
							case "Boolean": return o.Unbox<bool>() ? "true" : "false";
							case "Int32":   return o.Unbox<int>().ToString(inv);
							case "UInt32":  return o.Unbox<uint>().ToString(inv);
							case "Int64":   return o.Unbox<long>().ToString(inv);
							case "UInt64":  return o.Unbox<ulong>().ToString(inv);
							case "Int16":   return o.Unbox<short>().ToString(inv);
							case "UInt16":  return o.Unbox<ushort>().ToString(inv);
							case "Byte":    return o.Unbox<byte>().ToString(inv);
							case "SByte":   return o.Unbox<sbyte>().ToString(inv);
							case "Single":  return o.Unbox<float>().ToString(inv);
							case "Double":  return o.Unbox<double>().ToString(inv);
						}
					}
					catch { }
					if (!string.IsNullOrEmpty(n)) return "<" + n + ">";
				}
				if (o != null) return "<dead reference>";
			}
			catch { }
			try { return "<" + val.GetType().Name + ">"; } catch { return "<?>"; }
		}

		// VariableSymbols is an IReadOnlyCollection<string> PROXY on this build — neither a managed
		// enumerable nor something reflection can index, which is why the table listed zero symbols.
		// The shared reader walks its native enumerator (and still accepts the managed and
		// ImmutableArray shapes older builds handed back); VideoUrl reads the same table with it.
		private static IEnumerable<string> Strings(object seq) => Il2CppSeq.Strings(seq);

		// ------------------------------------------------------------------ helpers

		// True when this transform hangs under a player rather than the world — an avatar's own
		// Udon (FollowHead, breath systems, pickups) has nothing to do with the world script the
		// user is trying to manage.
		private static readonly string[] PlayerMarkers =
		{
			"VRCPlayer", "Player[Local]", "Player[Remote]", "AvatarRoot", "SelectRegion",
		};

		private static bool IsUnderPlayer(Transform t)
		{
			try
			{
				for (Transform p = t; p != null; p = p.parent)
				{
					string n = p.name ?? "";
					for (int i = 0; i < PlayerMarkers.Length; i++)
						if (n.IndexOf(PlayerMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
		}

		private static Type FindType(string full)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type t = null;
				try { t = asm.GetType(full, false); } catch { }
				if (t != null) return t;
			}
			return null;
		}

		private static string PathOf(Transform t)
		{
			var sb = new StringBuilder(96);
			try
			{
				var stack = new List<string>();
				for (Transform p = t; p != null; p = p.parent) stack.Add(p.name);
				for (int i = stack.Count - 1; i >= 0; i--) { sb.Append(stack[i]); if (i > 0) sb.Append('/'); }
			}
			catch { }
			return sb.ToString();
		}

		private static string Trunc(string s, int n)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
	}
}
