using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// AFK · SEATED · IN STATION · VR — the game's own answers about the people around you.
	//
	// WHERE THEY COME FROM. VRChat drives a fixed set of parameters on every avatar's animator and
	// synchronises them, which is how a remote player's avatar knows to play its AFK pose or sit
	// down on your screen. So these are not inferred from how somebody is rendered: they are the
	// values VRChat itself sent. MimicPoseModule already reads the same four
	// (BoolParams/IntParams there) to copy a pose, so the mechanism is not a guess — it is the one
	// already running in this mod.
	//
	// WHY NOT "IMMOBILIZED". Because it cannot be read for anyone but yourself. A metadata scan of
	// VRCSDKBase, VRCSDK3, Assembly-CSharp and VRC.Udon.VRCWrapperModules finds only Immobilize(bool),
	// its backing Action, the ImmobilizeForVehicle enum value and the Udon extern — a SETTER and no
	// getter anywhere, and nothing on the wire. A flag that is always false is worse than no flag, so
	// it is not shown.
	//
	// THE COST, WHICH IS THE WHOLE DESIGN. This runs over every player, so it is exactly the shape of
	// thing that took ObjectGravity to 8 fps earlier today. Three defences:
	//   * TWICE A SECOND, on a time gate — not per frame, and not on the roster's refresh, which is
	//     driven by something else entirely.
	//   * THE ANIMATOR IS CACHED per player and only re-resolved when their avatar changes (compared
	//     by instance id, because a proxy is not reference-stable).
	//   * PARAMETER PRESENCE IS CACHED per animator. Reading animator.parameters allocates an array
	//     of proxies, and asking for a parameter a rig does not have is not free either — so the
	//     names are checked once per avatar and the reads use int hashes afterwards.
	// That is ~4 hashed reads per player per half-second: about 320 interop calls a second in a busy
	// 40-player instance, against the tens of thousands that made ObjectGravity a problem.
	public class PlayerStatesModule : IModule
	{
		public override string Name => "PlayerStates";

		public static string Status = "";

		// Unity resolves parameters by hash internally; doing it once here spares a string hash on
		// every read for every player.
		private static readonly int HAfk = Animator.StringToHash("AFK");
		private static readonly int HSeated = Animator.StringToHash("Seated");
		private static readonly int HInStation = Animator.StringToHash("InStation");
		private static readonly int HVrMode = Animator.StringToHash("VRMode");

		// "Are they in a headset", from VRChat rather than from their avatar. Own cache entry so it
		// works even when the avatar has no animator at all — which is the whole point.
		private static MethodInfo _isInVr;
		private static bool _isInVrTried, _isInVrLogged;

		private static void UpdateVr(VaTagsModule.PlayerEntry e, string uid)
		{
			try
			{
				if (uid.Length == 0) return;
				if (!_cache.TryGetValue(uid, out Cached c) || c == null) { c = new Cached(); _cache[uid] = c; }

				float now = VaClock.Now;
				if (c.VrOk && now - c.VrAt < 5f) { e.InVR = c.InVr; e.VrKnown = true; return; }

				if (c.Api == null)
				{
					try { c.Api = FewTagsModule.GetMemberByTypeName(e.Player, "VRCPlayerApi", "prop_VRCPlayerApi_0", "field_Public_VRCPlayerApi_0"); }
					catch { }
				}
				if (c.Api == null) return;
				if (!Core.NativeGuard.Alive(c.Api)) { c.Api = null; c.VrOk = false; return; }

				if (!_isInVrTried)
				{
					_isInVrTried = true;
					try { _isInVr = c.Api.GetType().GetMethod("IsUserInVR", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null); }
					catch { }
					if (_isInVr == null)
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[PlayerStates] VRCPlayerApi has no IsUserInVR() on this build — the VR tag falls back to the "
							+ "avatar's VRMode parameter, which most avatars do not declare.");
				}
				if (_isInVr == null) return;

				object r;
				try { r = _isInVr.Invoke(c.Api, null); }
				catch { c.Api = null; return; }      // the player object died between resolve and call
				if (!(r is bool b)) return;

				c.InVr = b; c.VrOk = true; c.VrAt = now;
				e.InVR = b; e.VrKnown = true;
				if (!_isInVrLogged)
				{
					_isInVrLogged = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[PlayerStates] VR state now comes from VRCPlayerApi.IsUserInVR() — headset users show VR whatever avatar they wear.");
				}
			}
			catch { }
		}

		private sealed class Cached
		{
			public Animator Anim;
			public int AnimId;
			public bool HasAfk, HasSeated, HasInStation, HasVrMode;
			// The SDK's own answer to "are they in a headset", and when it was last asked. Held per
			// player because resolving the VRCPlayerApi off the player object is reflection, and this
			// module is already one of the mod's more expensive passes.
			public object Api;
			public float VrAt;
			public bool VrOk, InVr;
		}

		// Keyed by userId: a player keeps theirs across avatar changes, and the entry is dropped when
		// they leave so the table cannot grow across instances.
		private static readonly Dictionary<string, Cached> _cache = new Dictionary<string, Cached>(StringComparer.Ordinal);
		private static float _nextPass;
		private static int _known, _total;

		public override void OnUpdate()
		{
			try
			{
				float now = VaClock.Now;
				if (now < _nextPass) return;
				// 1 s, not 0.5 s (2026-09-13). This walks every player for AFK / Seated / InStation /
				// VR — states that change on a human timescale, not twice a second — and in a
				// 40-player instance the pass was 40-70 ms. Halving the cadence halves the cost with
				// no visible change to the PLAYERS panel it feeds.
				_nextPass = now + 1.0f;
				long tPass = Core.PerfLog.Start();
				Pass();
				Core.PerfLog.Slow("PlayerStates/pass", tPass, 8.0,
					_known + "/" + _total + " player state(s) known");
			}
			catch (Exception e) { Status = "player states: " + e.Message; }
		}

		private static void Pass()
		{
			var roster = VaTagsModule.Roster;
			if (roster == null) return;

			var live = new HashSet<string>(StringComparer.Ordinal);
			int known = 0, total = 0;

			lock (roster)
			{
				for (int i = 0; i < roster.Count; i++)
				{
					var e = roster[i];
					if (e == null) continue;
					total++;
					string uid = e.UserId ?? "";
					if (uid.Length > 0) live.Add(uid);

					Animator anim = ResolveAnimator(e, uid, out Cached c);

					// VR FIRST, AND FROM THE SDK — NOT FROM THE AVATAR.
					//
					// This used to come only from the animator's VRMode parameter, which is an AVATAR
					// parameter: an avatar that does not declare it simply never answers, and an avatar
					// that has not loaded yet leaves StateKnown false and takes the whole tag with it.
					// That is why a PC player in a headset — Quest Link, Index, anything PCVR — showed
					// PC and no VR: nothing was wrong with the headset detection, there was no headset
					// detection, only an avatar convention that most avatars do not follow.
					//
					// VRCPlayerApi.IsUserInVR() is VRChat's own answer, present on every player from the
					// moment they join, independent of what they are wearing. It is a METHOD
					// (il2cpp_runtime_invoke), so it carries none of the field-offset or string-marshal
					// hazards this mod has been bitten by today.
					//
					// Asked at most every 5 s per player: a headset is not something you swap mid-lap,
					// and this module already costs ~80 ms/s.
					UpdateVr(e, uid);

					if (anim == null || c == null)
					{
						// Avatar not loaded yet, or no animator: say nothing rather than "not AFK".
						e.StateKnown = false;
						continue;
					}

					try
					{
						if (c.HasAfk) e.Afk = anim.GetBool(HAfk);
						if (c.HasSeated) e.Seated = anim.GetBool(HSeated);
						if (c.HasInStation) e.InStation = anim.GetBool(HInStation);
						// VRMode is an INT on VRChat's controller: 1 = VR, 0 = desktop. Kept ONLY as a
						// fallback now — the SDK's IsUserInVR above is authoritative, and this is an
						// avatar convention that most avatars do not implement.
						if (c.HasVrMode && !e.VrKnown) { e.InVR = anim.GetInteger(HVrMode) != 0; e.VrKnown = true; }
						e.StateKnown = c.HasAfk || c.HasSeated || c.HasInStation || c.HasVrMode;
						if (e.StateKnown) known++;
					}
					catch
					{
						// The avatar died between the resolve and the read. Drop the cache entry so the
						// next pass re-resolves rather than reading a dead proxy again.
						e.StateKnown = false;
						if (uid.Length > 0) _cache.Remove(uid);
					}
				}
			}

			// Players who left take their cache with them.
			if (_cache.Count > live.Count)
			{
				var gone = new List<string>();
				foreach (var kv in _cache) if (!live.Contains(kv.Key)) gone.Add(kv.Key);
				for (int i = 0; i < gone.Count; i++) _cache.Remove(gone[i]);
			}

			_known = known; _total = total;
			Status = known + "/" + total + " player(s) reporting state";
		}

		// The player's avatar animator, cached until their avatar changes. Compared by instance id
		// rather than by reference: an il2cpp proxy for the same object is not reference-stable, so
		// "!= _anim" would re-resolve constantly and re-read the parameter list with it.
		private static Animator ResolveAnimator(VaTagsModule.PlayerEntry e, string uid, out Cached c)
		{
			c = null;
			try
			{
				// CACHE FIRST (2026-09-13). The cache was consulted only AFTER a full
				// GetComponentInChildren<Animator>(true) — a walk of every transform in the avatar,
				// through il2cpp — plus two liveness syscalls, on EVERY pass for EVERY player. The
				// walk was therefore never skipped, and the cache only ever saved the parameter-list
				// read. In a 32-player instance that was this module's 43 ms/s and its 142 ms frame.
				//
				// A cached animator that is still alive IS this player's animator: an avatar change
				// destroys the old Animator with the old avatar, so Alive() turns false and the full
				// resolve below runs exactly as before. Same answers, one syscall instead of a
				// hierarchy walk.
				if (uid.Length > 0 && _cache.TryGetValue(uid, out c) && c != null && c.Anim != null)
				{
					if (NativeGuard.Alive(c.Anim)) return c.Anim;
					_cache.Remove(uid);   // dead: the avatar changed, fall through and re-resolve
					c = null;
				}

				var t = e.Transform;
				if (t == null || !NativeGuard.Alive(t)) return null;

				Animator anim = null;
				try { anim = t.GetComponentInChildren<Animator>(true); } catch { }
				if (anim == null || !NativeGuard.Alive(anim)) return null;

				int id;
				try { id = anim.GetInstanceID(); } catch { return null; }

				if (uid.Length > 0 && _cache.TryGetValue(uid, out c) && c != null && c.AnimId == id)
					return anim;

				// New avatar: read the parameter list ONCE. This is the allocating call the rest of
				// the module exists to avoid repeating.
				c = new Cached { Anim = anim, AnimId = id };
				try
				{
					var ps = anim.parameters;
					if (ps != null)
						for (int i = 0; i < ps.Length; i++)
						{
							string n;
							try { n = ps[i]?.name ?? ""; } catch { continue; }
							if (n == "AFK") c.HasAfk = true;
							else if (n == "Seated") c.HasSeated = true;
							else if (n == "InStation") c.HasInStation = true;
							else if (n == "VRMode") c.HasVrMode = true;
						}
				}
				catch { }
				if (uid.Length > 0) _cache[uid] = c;
				return anim;
			}
			catch { c = null; return null; }
		}

		public override void OnSceneLoaded(int buildIndex) { _cache.Clear(); _nextPass = 0f; }
	}
}
