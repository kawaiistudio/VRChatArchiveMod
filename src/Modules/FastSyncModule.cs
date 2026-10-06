using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FAST SYNC — ask VRChat to serialise YOU at its fast rate.
	//
	// WHAT IT IS, AND WHAT IT IS NOT. This is not a hand-rolled increase of the Photon send rate,
	// which is the shape I warned the owner about: NetSendModule exists because two ServerTimeouts on
	// 2026-09-02 came from a STARVED CONNECTION, not a busy CPU, and MarkModule carries an event
	// budget for the same reason. This flips VRChat's OWN flag on VRChat's OWN serialiser —
	// FlatBufferNetworkSerializer.RequireFastRate — so the game keeps its own throttling, its own
	// batching and its own idea of what the network can take. It is the difference between opening a
	// tap the game controls and bypassing the tap.
	//
	// WHAT IT BUYS. Your position, rotation and pose reach the other clients more often, so you look
	// smoother to everyone else and the things you carry track your hands more closely. It costs
	// outbound bandwidth, which is why the game does not do it for everybody all the time.
	//
	// VERIFIED BEFORE IT WAS WRITTEN, not after: FlatBufferNetworkSerializer and get_/set_
	// RequireFastRate are both present in libs/interop's Assembly-CSharp. The lesson of the custom
	// username, which shipped on a grep of a name and crashed the game.
	//
	// RE-ASSERTED, because the component goes with the player. Joining a world rebuilds the local
	// player object and the fresh serialiser comes back at the default rate, so a one-shot write
	// would quietly stop applying after the first instance change.
	public class FastSyncModule : IModule
	{
		public override string Name => "FastSync";

		public static string Status = "";
		/// <summary>True once the flag has actually been written and read back as set.</summary>
		public static bool Applied { get; private set; }

		private static Type _serType;
		// Deliberately typed `object`, NOT Il2CppSystem.Type.
		//
		// A static field of an Il2Cpp* type forces the CLR to load that type when this class is
		// prepared -- i.e. at `new FastSyncModule()`, before any of our try/catch can help. Loading
		// Il2CppSystem.Type runs its generated type initializer, which resolves il2cpp field offsets,
		// and on VRChat build 1903 that path faults: an access violation, not a catchable exception,
		// so the process dies with no log at all. Traced to this exact module by flushing a line per
		// constructor: FastSyncModule was always the last one written.
		//
		// Holding it as `object` defers the type load to the first ACTUAL use, which is inside a
		// try/catch, so a failure degrades this one feature instead of killing the game.
		private static object _serIl2;
		private static PropertyInfo _fastRateProp;
		private static bool _resolveLogged;
		private static float _nextCheck;
		private static bool _lastWanted;

		public override void OnUpdate()
		{
			try
			{
				// THE SWITCH IS READ FIRST, AND AN EDGE BEATS THE THROTTLE (2026-09-13).
				//
				// The toggle used to be read AFTER the 0.5 s throttle, so both ON and OFF took up to
				// half a second to reach the serialiser — a toggle that answers late reads as a toggle
				// that does not work. The value is now read every frame (one bool) and a CHANGE
				// bypasses the throttle entirely, so the flag is written on the very frame it flips;
				// the throttle still governs the steady state, where it exists to avoid re-resolving
				// the component.
				bool want = false;
				try { want = ModConfig.FastSync != null && ModConfig.FastSync.Value; } catch { }

				float now = VaClock.Now;
				bool edge = want != _lastWanted;
				if (!edge && now < _nextCheck) return;
				_nextCheck = now + 0.5f;

				// NOTHING WANTED, NOTHING APPLIED, NOTHING TO UNDO: no work at all.
				//
				// Resolve() is a component lookup plus, on a miss, a GetComponents<Component>() and a
				// GetComponentInChildren(..., true) name scan — the module's own note measures that
				// path at 28-129 ms/s. It ran twice a second for every user whether or not fast sync
				// was ever switched on. With the switch off and nothing written there is no state to
				// restore, so the lookup is skipped outright; the moment it is switched on (or a
				// previous ON still has to be undone) the full path below runs exactly as before.
				if (!want && !Applied)
				{
					_lastWanted = false;
					Status = "fast sync: off";
					return;
				}

				var ser = Resolve();
				if (ser == null)
				{
					Applied = false;
					if (want) Status = "fast sync: waiting for the local player";
					// SAY IT ONCE. This was a bare return, and it is where the whole feature died: the
					// module armed, the button reached the mod, and the flag was never written because
					// the component was never found. Nothing in the log said so.
					if (want && !_missLogged)
					{
						_missLogged = true;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[FastSync] asked ON but the serialiser component is not reachable on the local "
							+ "player yet — nothing was written. " + _lastResolveNote);
					}
					return;
				}
				_missLogged = false;

				bool current;
				try { current = (bool)(_fastRateProp.GetValue(ser) ?? false); }
				catch (Exception re)
				{
					Status = "fast sync: could not read RequireFastRate";
					if (!_readFailLogged)
					{
						_readFailLogged = true;
						VRChatArchiveModPlugin.Logger.LogWarning("[FastSync] reading RequireFastRate threw: " + re.Message);
					}
					return;
				}

				if (current == want)
				{
					Applied = want;
					if (want) Status = "fast sync ON — VRChat is serialising you at its fast rate";
					else Status = "fast sync off";
					_lastWanted = want;
					return;
				}

				try { _fastRateProp.SetValue(ser, want); }
				catch (Exception e) { Status = "fast sync: " + e.Message; return; }

				// Read back rather than assume, the way the custom username does — "set" and "took"
				// are different claims.
				bool after;
				try { after = (bool)(_fastRateProp.GetValue(ser) ?? false); }
				catch { after = false; }
				Applied = after == want && want;

				if (want != _lastWanted || after != want)
				{
					_lastWanted = want;
					if (after == want)
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[FastSync] RequireFastRate = " + want + " — verified by read-back. VRChat's own "
							+ "serialiser rate, so its own throttling still applies.");
					else
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[FastSync] wrote RequireFastRate = " + want + " but read back " + after
							+ " — the game refused it. Not retrying in a loop.");
				}
				Status = Applied ? "fast sync ON" : "fast sync off";
			}
			catch (Exception e) { Status = "fast sync: " + e.Message; }
		}

		// The serialiser lives on the LOCAL PLAYER's GameObject. Resolved fresh each pass rather than
		// cached: the object is rebuilt on every world change, and a cached proxy would be dead — the
		// lookup is one call and this runs twice a second.
		// Cached. The old version re-resolved on EVERY pass — two NativeGuard probes plus a recursive
		// GetComponentInChildren, twice a second, and it never succeeded, which is exactly why FastSync
		// shows up at 28-129 ms/s in the Perf warnings. It is re-resolved when it dies or the scene
		// changes, which is the only time the player object is rebuilt.
		private static object _cached;
		private static bool _missLogged, _readFailLogged, _foundLogged;
		private static string _lastResolveNote = "";

		private static object Resolve()
		{
			try
			{
				if (_cached != null)
				{
					var alive = _cached as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
					if (alive != null && NativeGuard.Alive(alive)) return _cached;
					_cached = null;
				}

				if (_serType == null && !ResolveType()) return null;

				// THE ROOT GHOST USES. The serialiser lives on the local player's own object
				// ("VRCPlayer[Local] …"). LocalTransform alone was not it.
				GameObject go = null;
				try
				{
					var root = PlayerRef.LocalPlayer();
					if (root != null && NativeGuard.Alive(root)) go = root.gameObject;
				}
				catch { go = null; }
				if (go == null)
				{
					try
					{
						var lt = PlayerRef.LocalTransform();
						if (lt != null && NativeGuard.Alive(lt)) go = lt.gameObject;
					}
					catch { go = null; }
				}
				if (go == null) { _lastResolveNote = "the local player object is not up yet."; return null; }

				// 1. The typed lookup. Fast when it works — and it silently does not when the generated
				//    proxy type does not match the runtime type, which is the false negative that made
				//    this whole feature dead. It is tried first and trusted only when it answers.
				Component c = null;
				if (_serIl2 != null)
				{
					try { c = go.GetComponent((Il2CppSystem.Type)_serIl2); } catch { }
					if (c == null) { try { c = go.GetComponentInChildren((Il2CppSystem.Type)_serIl2, true); } catch { } }
				}

				// 2. BY NAME, the way GhostModule finds this exact component and why Ghost works where
				//    this did not. Matching the il2cpp class name cannot be defeated by a proxy mismatch.
				string how = c != null ? "typed lookup" : null;
				if (c == null) { c = ByName(go.GetComponents<Component>()); if (c != null) how = "name scan on the root"; }
				if (c == null) { c = ByName(go.GetComponentsInChildren<Component>(true)); if (c != null) how = "name scan in children"; }

				if (c == null || !NativeGuard.Alive(c))
				{
					// Name what WAS there. "not found" on its own has cost this project days before.
					string names = "";
					try
					{
						var all = go.GetComponents<Component>();
						if (all != null)
							foreach (var x in all)
								{ try { names += (Core.MenuCard.Il2CppNameOf(x) ?? "?") + " "; } catch { } }
					}
					catch { }
					_lastResolveNote = "No *" + SerializerName + "* on '" + go.name + "'. Components there: " + names;
					return null;
				}

				// A component interop cannot cast is still that component natively: re-wrap its pointer
				// into the generated type so the PropertyInfo below still applies. Same move as Ghost's
				// `new Behaviour(c.Pointer)`.
				object typed = c;
				if (!_serType.IsInstanceOfType(typed))
				{
					try { typed = Activator.CreateInstance(_serType, c.Pointer); }
					catch (Exception we)
					{
						_lastResolveNote = "found the component but could not wrap it: " + we.Message;
						return null;
					}
				}

				_cached = typed;
				if (!_foundLogged)
				{
					_foundLogged = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[FastSync] serialiser found on '" + go.name + "' by " + how + ".");
				}
				return typed;
			}
			catch (Exception e) { _lastResolveNote = "resolve threw: " + e.Message; return null; }
		}

		// The il2cpp class name of the serialiser, matched loosely — the same test GhostModule uses.
		private const string SerializerName = "FlatBufferNetworkSerializer";

		private static Component ByName(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Component> comps)
		{
			if (comps == null) return null;
			foreach (var c in comps)
			{
				try
				{
					if (c == null || !NativeGuard.Alive(c)) continue;
					string n = Core.MenuCard.Il2CppNameOf(c) ?? "";
					if (n.IndexOf(SerializerName, StringComparison.OrdinalIgnoreCase) >= 0) return c;
				}
				catch { }
			}
			return null;
		}

		// Type and property, once per session. Splitting this out of Resolve is what lets the
		// component lookup be cached without re-running the assembly walk.
		private static bool ResolveType()
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { _serType = asm.GetType("VRC.Networking." + SerializerName, false) ?? asm.GetType(SerializerName, false); }
				catch { continue; }
				if (_serType != null) break;
			}
			if (_serType == null)
			{
				if (!_resolveLogged) { _resolveLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[FastSync] " + SerializerName + " is not present on this build — fast sync unavailable."); }
				return false;
			}
			try { _serIl2 = Il2CppType.From(_serType); } catch { _serIl2 = null; }
			_fastRateProp = _serType.GetProperty("RequireFastRate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (_fastRateProp == null)
			{
				if (!_resolveLogged) { _resolveLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[FastSync] RequireFastRate not found on " + SerializerName + " — fast sync unavailable."); }
				_serType = null;
				return false;
			}
			// NOTE the changed wording: "armed" used to be printed here and read like the feature was
			// working. It only ever meant the TYPE was found; the component is a separate question, and
			// that is precisely the gap the switch fell into.
			if (!_resolveLogged) { _resolveLogged = true; VRChatArchiveModPlugin.Logger.LogInfo("[FastSync] type ready — " + SerializerName + ".RequireFastRate. Looking for the component on the local player next."); }
			return true;
		}

		// A new world means a new player object; force the next pass to write the flag again.
		public override void OnSceneLoaded(int buildIndex)
		{
			Applied = false; _lastWanted = false; _nextCheck = 0f;
			_cached = null; _missLogged = false; _foundLogged = false;
		}
	}
}
