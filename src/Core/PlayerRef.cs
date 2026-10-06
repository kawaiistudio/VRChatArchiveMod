using System;
using System.Reflection;
using UnityEngine;

namespace VRChatArchiveMod.Core
{
	// Reflection-based access to the local VRChat player. Uses the same entry points as
	// the reference fly mod (VRC.Player.prop_Player_0 → the local player Component, whose
	// field_Private_VRCPlayerApi_0 exposes VRCPlayerApi.SetVelocity). Reflection keeps
	// this decoupled from compile-time obfuscated signatures, and every call is guarded.
	//
	// EVERYTHING HERE IS RESOLVED ONCE AND CACHED FOR A FRAME, and that is not a micro-optimisation.
	//
	// LocalApi() used to do, on EVERY call: Assembly.Load, GetType, enumerate every static property
	// of VRCPlayer, then enumerate every instance property of the instance, invoking il2cpp getters
	// along the way. Fourteen modules call into this class — Speed, Movement, Orbit, Gravity, the
	// two ESPs, Radar's map camera, Soundboard, SpawnSound, InstancePanels — and several of them
	// call it every single frame. The profiler measured Speed alone at 205 ms per second of wall
	// clock, i.e. a fifth of the machine's time spent re-answering a question whose answer had not
	// changed.
	//
	// Two caches, with different lifetimes, because they have different risks:
	//   * the REFLECTION METADATA (Type, PropertyInfo, MethodInfo) never changes for the life of the
	//     process, so it is resolved once and kept forever.
	//   * the RESOLVED OBJECT is kept for one frame only. Il2Cpp proxies can outlive the object
	//     behind them, and a stale player held across a world change is exactly the dead-proxy read
	//     that ends the process — so the object is looked up again next frame, which by then costs
	//     two getter calls instead of two full property enumerations.
	public static class PlayerRef
	{
		private static bool _deadProxyLogged;

		// ---------------------------------------------------------------- reflection, resolved once

		private static bool _resolved;
		private static PropertyInfo _propLocalPlayer;   // static VRC.Player.prop_Player_0
		private static PropertyInfo _propVrcPlayerSelf; // static VRCPlayer self-pointer
		private static PropertyInfo _propApiOnSelf;     // instance VRCPlayerApi on that self
		private static Type _tVrcPlayer;

		private static void Resolve()
		{
			if (_resolved) return;
			_resolved = true;
			try
			{
				Assembly asm = Assembly.Load("Assembly-CSharp");

				Type tPlayer = asm.GetType("VRC.Player");
				_propLocalPlayer = tPlayer?.GetProperty("prop_Player_0",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

				_tVrcPlayer = asm.GetType("VRCPlayer");
				if (_tVrcPlayer != null)
				{
					// The static self-pointer: the one static property whose type is VRCPlayer.
					foreach (PropertyInfo p in _tVrcPlayer.GetProperties(
						BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
					{
						if (p.PropertyType.Name == "VRCPlayer") { _propVrcPlayerSelf = p; break; }
					}
					// And the instance property that hands out the SDK handle.
					foreach (PropertyInfo p in _tVrcPlayer.GetProperties(
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
					{
						if (p.PropertyType.Name == "VRCPlayerApi") { _propApiOnSelf = p; break; }
					}
				}
			}
			catch { }
		}

		private static PropertyInfo LocalPlayerProp
		{
			get { Resolve(); return _propLocalPlayer; }
		}

		// ---------------------------------------------------------------- local player Component

		private static Component _player;

		// The local player as a Unity Component (null until the player has spawned).
		//
		// KEPT ACROSS FRAMES, revalidated cheaply. A per-frame cache was not enough: the first
		// module to ask each frame still paid a PropertyInfo.GetValue through the il2cpp interop,
		// which is the expensive half — so the cost did not go away, it just moved to whichever
		// module happened to ask first. The player does not change while you are in a world, so the
		// reference is held and only the LIVENESS is checked each time: a pointer read plus a
		// VirtualQuery, against a reflected property invocation.
		// The local player does not change while you are in a world, and Invalidate() clears this on
		// every scene load — so the cached reference is good for the whole session. Revalidating it
		// with a VirtualQuery EVERY frame was the mod's single biggest cost: Speed, Movement and
		// SpawnSound all call this per frame, and when the il2cpp wrapper was briefly collected the
		// check failed and each re-resolved through reflection — measured at 180+110+110 ms/s. The
		// liveness check now runs a few times a second instead of ninety, which is plenty to notice a
		// real teardown while costing almost nothing. The crash it originally guarded against was the
		// field-offset bug, now fixed at the source (FieldOffsetFix).
		private static float _playerCheckedAt;
		private const float RevalidateSec = 0.25f;

		public static Component LocalPlayer()
		{
			if (!VaPlayers.Ready) return null;   // AllPlayers accessor is mis-bound on this build: do not read it
			try
			{
				var all = VRChatArchiveMod.Core.VaPlayers.All();
				if (all == null || all.Count == 0) return null;
			}
			catch { return null; }

			if (_player != null)
			{
				float now = VaClock.Now;
				if (now - _playerCheckedAt < RevalidateSec) return _player;   // trust the cache between checks
				_playerCheckedAt = now;
				if (NativeGuard.Alive(_player)) return _player;
			}
			_player = ResolveLocalPlayer();
			_playerCheckedAt = VaClock.Now;
			return _player;
		}

		// Called on a scene change so a stale player cannot survive into the next world even if it
		// still looks alive for a moment.
		public static void Invalidate()
		{
			_player = null;
			_api = null;
			_playerCheckedAt = 0f;
			_apiCheckedAt = 0f;
		}

		private static Component ResolveLocalPlayer()
		{
			try
			{
				Resolve();
				// Do not invoke the getter unless VRC.Player is a real class on this build. Through the
				// MissingTypeGuard placeholder it is an access violation, not a null, so the SDK-stable
				// VRCPlayerApi is used to reach the same Component instead.
				if (!PlayerClassIsReal()) return LocalPlayerFromApi();
				// prop_Player_0 answers from the first frame, well before the local player exists,
				// and what it hands back then is not a live object — reading any field off it is
				// fatal. Everything downstream already treats null as "not spawned yet", which is
				// exactly what this is.
				var c = _propLocalPlayer?.GetValue(null) as Component;
				if (c == null || NativeGuard.Alive(c)) return c;
				// Said once, because it is the difference between "the guard is doing something"
				// and "the crash moved somewhere else".
				if (!_deadProxyLogged)
				{
					_deadProxyLogged = true;
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[PlayerRef] prop_Player_0 handed back a proxy with no live object behind it "
						+ "(normal before spawn) — refused instead of read.");
				}
				return null;
			}
			catch { return null; }
		}

		// VRC.Player is one of VRChat's obfuscated internal classes: its name is not stable across
		// builds, so on this one GetIl2CppClass cannot find it and MissingTypeGuard stands System.Object
		// in for it. Invoking prop_Player_0 through that placeholder is the access violation that took
		// the game down. This is the cheap, safe check before the call: a real, present class or nothing.
		private static readonly System.Collections.Generic.Dictionary<string, bool> _realClass = new System.Collections.Generic.Dictionary<string, bool>();
		private static bool ClassIsReal(string ns, string name)
		{
			string key = ns + "." + name;
			if (_realClass.TryGetValue(key, out bool real)) return real;
			try
			{
				IntPtr k = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", ns, name);
				// Recovered-but-renamed counts as unusable HERE: this path invokes a getter, and a
				// recovered class's members cannot be named on this build.
				real = k != IntPtr.Zero && !MissingTypeGuard.IsPlaceholder(k) && !ObfuscatedClassFinder.IsRecovered(k);
				VRChatArchiveModPlugin.Logger.LogInfo("[PlayerRef] " + key + " il2cpp class=0x" + k.ToString("X")
					+ " reelle=" + real + (real ? "" : " — ses getters sont evites, le SDK (VRCPlayerApi) prend le relais."));
			}
			catch { real = false; }
			_realClass[key] = real;
			return real;
		}
		private static bool PlayerClassIsReal() => ClassIsReal("VRC", "Player");
		private static bool VrcPlayerClassIsReal() => ClassIsReal("", "VRCPlayer");

		// The same Component reached through the SDK-stable VRCPlayerApi: its gameObject's Transform IS
		// a Component, and it is what every consumer actually wants (a transform, a position). The one
		// thing it lacks is field_Private_VRCPlayerApi_0, which ZeroVelocity now no longer needs.
		private static Component LocalPlayerFromApi()
		{
			try
			{
				var api = LocalApi();
				if (api == null || !NativeGuard.Alive(api)) return null;
				var go = api.gameObject;
				if (go == null || !NativeGuard.Alive(go)) return null;
				return go.transform;
			}
			catch { return null; }
		}

		public static Transform LocalTransform()
		{
			var p = LocalPlayer();
			try { return p != null ? p.transform : null; }
			catch { return null; }
		}

		// ---------------------------------------------------------------- VRCPlayerApi

		private static VRC.SDKBase.VRCPlayerApi _api;

		// The local VRCPlayerApi — the SDK-stable handle that exposes TeleportTo/GetRotation.
		// Reached through VRCPlayer's static self pointer, with the AllPlayers scan as backup.
		//
		// Held across frames like the Component above, and for the same reason: SpeedModule asks
		// for this every single frame, and the profiler put it at 174 ms per second of wall clock
		// even after the reflection metadata was cached. VRCPlayerApi is not a UnityEngine.Object,
		// so it has no cheap null check of its own — its liveness is taken from the il2cpp object
		// behind it, which is the same test used for the player.
		private static float _apiCheckedAt;

		public static VRC.SDKBase.VRCPlayerApi LocalApi()
		{
			if (!VaPlayers.Ready) return null;   // AllPlayers accessor is mis-bound on this build: do not read it
			try
			{
				var all = VRChatArchiveMod.Core.VaPlayers.All();
				if (all == null || all.Count == 0) return null;
			}
			catch { return null; }

			if (_api != null)
			{
				float now = VaClock.Now;
				if (now - _apiCheckedAt < RevalidateSec) return _api;
				_apiCheckedAt = now;
				if (NativeGuard.Alive(_api)) return _api;
			}
			_api = ResolveApi();
			_apiCheckedAt = VaClock.Now;
			return _api;
		}

		private static VRC.SDKBase.VRCPlayerApi ResolveApi()
		{
			if (!VaPlayers.Ready) return null;   // AllPlayers accessor is mis-bound on this build: do not read it
			try
			{
				var all = VRChatArchiveMod.Core.VaPlayers.All();
				if (all == null || all.Count == 0) return null;

				for (int i = 0; i < all.Count; i++)
				{
					var a = all[i];
					if (a != null && NativeGuard.Alive(a) && a.isLocal)
					{
						return a;
					}
				}
			}
			catch { }

			Resolve();
			try
			{
				// VRCPlayer is obfuscated and absent under its 1886 name on this build: through the
				// placeholder its static self-pointer getter is an access violation. Skip straight to
				// the SDK's AllPlayers when the class is not really there.
				if (VrcPlayerClassIsReal() && _propVrcPlayerSelf != null && _propApiOnSelf != null)
				{
					object self = null;
					try { self = _propVrcPlayerSelf.GetValue(null); } catch { }
					if (self != null && NativeGuard.Alive(self))
					{
						try
						{
							if (_propApiOnSelf.GetValue(self) is VRC.SDKBase.VRCPlayerApi api && api != null && NativeGuard.Alive(api))
								return api;
						}
						catch { }
					}
				}
			}
			catch { }

			return null;
		}

		private static float _nextScan;

		// ---------------------------------------------------------------- velocity

		private static PropertyInfo _propApiOnComponent;
		private static MethodInfo _setVelocity;
		private static Type _velocityFor;

		// Zero the local player's networked velocity (keeps fly from drifting/falling).
		//
		// Called every frame while flying, so the two lookups it needs are resolved once per player
		// type rather than once per call — the same reason the rest of this file caches.
		public static void ZeroVelocity(Component player)
		{
			try
			{
				if (player == null) return;
				Type t = player.GetType();
				if (_velocityFor != t)
				{
					_velocityFor = t;
					_propApiOnComponent = t.GetProperty("field_Private_VRCPlayerApi_0");
					_setVelocity = null;
				}

				object api = _propApiOnComponent?.GetValue(player);
				// A Transform handed out by LocalPlayerFromApi has no such field: the SDK handle IS
				// the api, and it is exactly as good for SetVelocity.
				if (api == null) api = LocalApi();
				if (api == null) return;

				if (_setVelocity == null)
					_setVelocity = api.GetType().GetMethod("SetVelocity", new[] { typeof(Vector3) });

				_setVelocity?.Invoke(api, VelocityArgs);
			}
			catch { }
		}

		// Reused so a per-frame call does not allocate an object[] and a box every frame.
		private static readonly object[] VelocityArgs = { Vector3.zero };
	}
}
