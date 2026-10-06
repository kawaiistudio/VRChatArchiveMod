using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// INFINITE PORTAL — the portals YOU drop stop closing, and the cooldown between drops is gone.
	//
	// WHAT THIS IS AND IS NOT
	//
	// It is not a portal spawner. VRChat already has one — the native "Create Portal" flow, where the
	// player picks the destination world. This only changes two things about the portals that flow
	// creates: the lifetime countdown that closes them, and the client-side wait before the next drop.
	// Everything a portal does is unchanged; it just stays, and you can lay several down (to different
	// worlds) for RP set-dressing without them expiring under you. It is what a player could already do
	// by hand, minus the timer — the same line ObjectOrbit's synced mode draws.
	//
	// OTHERS SEE THESE. A dropped portal is a networked object; leaving it open leaves it open for
	// everyone in the instance. That is the point (you are inviting people), but it is stated plainly and
	// the toggle is OFF by default.
	//
	// WHY IT IS WRITTEN TO IDENTIFY ITS OWN TARGETS
	//
	// On build 1903 the two levers are fields whose names are obfuscated, and the interop is only a stub
	// — the countdown and the cooldown live in native code we cannot read. So nothing here is hardcoded
	// to an offset. The lifetime is found for what it DOES: among a portal's float fields, the one that
	// counts DOWN toward zero over time is the timer, and that is the only one clamped. The clamp is a
	// plain field write on a live object, guarded like every other read in this mod, and it can never
	// call an obfuscated method — so it cannot crash the way a hook would. If the portal type cannot be
	// resolved, the feature reports it and does nothing.
	//
	// REVERSIBILITY. Turning it off stops the top-up; a portal already pinned open keeps its huge
	// remaining time (it will not close during the session, which is what "infinite" means and what the
	// owner asked for). Nothing about other players, the network or the account is touched.
	public class PortalInfiniteModule : IModule
	{
		public override string Name => "PortalInfinite";

		public static bool Active { get; private set; }
		public static string Status = "off";

		// A portal's remaining time, pushed this high, will not reach zero in any session (years).
		private const float BigLife = 1e8f;
		// A field counting down from more than this is not a "closing in N seconds" timer; leave it.
		private const float LifeCap = 3600f;
		// One pass a second. FindObjectsOfType walks the whole scene and it was costing ~100 ms/s at twice
		// this rate, for a timer that only matters once every thirty seconds.
		private const float PassInterval = 1.0f;

		private static float _nextPass;

		// Per (portal instance id, offset) -> last value seen, to spot a monotonic countdown. The
		// portal's timer is NOT a discoverable Single field on this build ("aucun float" in the diag),
		// so the object's raw bytes are scanned instead and each candidate slot remembered as a double.
		private static readonly Dictionary<long, double> _prev = new Dictionary<long, double>();
		// Portal instance id -> its identified timer slot, packed as offset | (kind << 40).
		// kind 0 = float(4), 1 = double(8), 2 = int32(4). Forced every pass once found.
		private static readonly Dictionary<int, long> _lifeSlot = new Dictionary<int, long>();
		private const int KIND_FLOAT = 0, KIND_DOUBLE = 1, KIND_INT = 2;
		// How many diagnostic dumps each portal has had (2 samples so a decreasing counter can be told
		// from a constant end-timestamp), and a session cap so the log never floods.
		private static readonly Dictionary<int, int> _dumpPasses = new Dictionary<int, int>();
		private static int _dumpsLogged;
		private static bool _loggedLife, _loggedCooldown, _loggedNoType;

		// Portals seen alive last pass -> when first seen, to report the SECOND close mechanism: dropping
		// a new portal shuts your previous one. If the old id VANISHES from the scan, VRChat destroyed it
		// (nothing to clamp — needs the destroy prevented). If it stays but a field flips, we clamp that.
		private static readonly Dictionary<int, float> _seenPortals = new Dictionary<int, float>();
		private static int _maxCoexist;

		// Portal component type, resolved by CLEAN name (PortalInternal is not obfuscated) exactly as
		// HighlightEspModule resolves it. Getting the managed Type does not trigger the cctor crash;
		// naming the generated proxy store would, so it is never named.
		private static Type _portalType;
		private static Il2CppSystem.Type _portalIl2;
		private static bool _portalResolved;

		// PortalManager holds the static cooldown state. Its class pointer, for walking static fields.
		private static IntPtr _managerClass;
		private static bool _managerResolved;

		private const int FIELD_ATTRIBUTE_STATIC = 0x0010;

		// MEASURED 2026-09-19: a portal with its displayed timer pinned still vanishes at ~30 s, and dropping
		// a new one removes the previous one. Both closes are a DESTROY, not a field — so the destroy calls
		// are intercepted: logged with their native caller, and refused for portals while this is on.
		private const int MaxPortals = 10;
		private static readonly List<KeyValuePair<System.Reflection.MethodBase, System.Reflection.MethodInfo>> _destroyPatches
			= new List<KeyValuePair<System.Reflection.MethodBase, System.Reflection.MethodInfo>>();
		private static readonly HashSet<string> _destroyLogged = new HashSet<string>(StringComparer.Ordinal);
		private static readonly HashSet<int> _destroySeen = new HashSet<int>();
		// Portal object pointer -> its instance id, refreshed by the scan. The destroy prefix may look at
		// NOTHING else: every object it is handed may already be dying.
		private static readonly Dictionary<IntPtr, int> _portalPtrs = new Dictionary<IntPtr, int>();
		// Portals whose destroy we refused -> how many passes we have since put their insides back on.
		// VRChat's close sequence DISABLES the portal's canvases and graphics and THEN destroys the root:
		// blocking only the destroy leaves a black, empty shell (measured 2026-09-19). Re-enabling the
		// children is what makes a kept portal look and read like a portal again.
		private static readonly Dictionary<int, int> _blockedIds = new Dictionary<int, int>();
		// How each child of a healthy portal stood the FIRST time we saw it. Some are meant to be off (the
		// invalid-destination and proximity graphics), so "switch everything on" would draw a portal that
		// lies; the close sequence is undone by restoring this snapshot instead.
		private static readonly Dictionary<int, List<KeyValuePair<Transform, bool>>> _childSnap
			= new Dictionary<int, List<KeyValuePair<Transform, bool>>>();
		private static bool _revivedLogged, _stopLogged;
		private static readonly HashSet<int> _closeStopped = new HashSet<int>();

		public static void Toggle() { Set(!Active); }

		public static void Set(bool on)
		{
			if (on == Active) { Sync(); return; }
			Active = on;
			try { if (ModConfig.PortalInfiniteEnabled != null) ModConfig.PortalInfiniteEnabled.Value = on; } catch { }
			if (!on)
			{
				_prev.Clear();
				_lifeSlot.Clear();
				_dumpPasses.Clear();
				RemoveDestroyHooks();
			}
			Sync();
			VRChatArchiveModPlugin.Logger.LogInfo("[InfinitePortal] " + Status);
			Toast.Show(on ? "Infinite Portal ON — portals stay open, no cooldown"
			              : "Infinite Portal OFF — new portals close normally");
		}

		private static void Sync()
		{
			Status = Active ? "on — dropped portals will not expire" : "off";
		}

		public override void OnInitialize()
		{
			try { if (ModConfig.PortalInfiniteEnabled != null) Active = ModConfig.PortalInfiniteEnabled.Value; } catch { }
			Sync();
		}

		// ---- destroy: the real close, for both the 30 s expiry and the one-portal-per-player swap ----

		// ONLY WHILE A PORTAL IS ALIVE. A prefix on Object.Destroy makes the detour build a managed proxy
		// for EVERY object the game destroys, and the first version also ASKED each one whether it was a
		// portal (GetComponent) — on a world unload, where thousands die at once, that was an access
		// violation no try/catch survives, and it took the owner's game down. Two rules now: the prefix
		// touches nothing but the pointer (compared against what the scan already proved is a portal), and
		// the hooks exist only between the first portal appearing and the last one going away, so a world
		// change with no portal down never meets them at all.
		private static void InstallDestroyHooks()
		{
			if (_destroyPatches.Count > 0) return;
			Type uo = typeof(UnityEngine.Object);
			const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
			HookDestroy(uo.GetMethod("Destroy", S, null, new[] { uo, typeof(float) }, null), nameof(DestroyPre2));
			HookDestroy(uo.GetMethod("Destroy", S, null, new[] { uo }, null), nameof(DestroyPre1));
			HookDestroy(uo.GetMethod("DestroyImmediate", S, null, new[] { uo, typeof(bool) }, null), nameof(DestroyImmPre2));
			HookDestroy(uo.GetMethod("DestroyImmediate", S, null, new[] { uo }, null), nameof(DestroyImmPre1));
			VRChatArchiveModPlugin.Logger.LogInfo("[InfinitePortal] " + _destroyPatches.Count + "/4 hooks de destruction poses.");
		}

		private static void HookDestroy(System.Reflection.MethodBase target, string prefix)
		{
			if (target == null) return;
			try
			{
				var pm = typeof(PortalInfiniteModule).GetMethod(prefix,
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyLib.HarmonyMethod(pm));
				_destroyPatches.Add(new KeyValuePair<System.Reflection.MethodBase, System.Reflection.MethodInfo>(target, pm));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[InfinitePortal] hook " + target.Name + " : " + e.Message); }
		}

		private static void RemoveDestroyHooks()
		{
			foreach (var kv in _destroyPatches)
				try { VRChatArchiveModPlugin.HarmonyInstance.Unpatch(kv.Key, kv.Value); } catch { }
			_destroyPatches.Clear();
			_portalPtrs.Clear();
		}

		private static bool DestroyPre2(UnityEngine.Object __0, float __1) => OnDestroyCall(__0, "Destroy(obj, " + __1.ToString("0.##") + "s)");
		private static bool DestroyPre1(UnityEngine.Object __0) => OnDestroyCall(__0, "Destroy(obj)");
		private static bool DestroyImmPre2(UnityEngine.Object __0, bool __1) => OnDestroyCall(__0, "DestroyImmediate(obj, " + __1 + ")");
		private static bool DestroyImmPre1(UnityEngine.Object __0) => OnDestroyCall(__0, "DestroyImmediate(obj)");

		// true = let the destroy run.
		private static bool OnDestroyCall(UnityEngine.Object obj, string what)
		{
			try
			{
				if (!Active || obj == null) return true;
				if (!IsPortal(obj, out int id)) return true;

				_destroySeen.Add(id);
				bool block = _seenPortals.Count <= MaxPortals;
				if (block) _blockedIds[id] = 0;
				string age = _seenPortals.TryGetValue(id, out float first)
					? (VaClock.Now - first).ToString("0.0") + "s" : "a la creation";
				string key = id + "|" + what;
				if (_destroyLogged.Add(key) && _destroyLogged.Count < 60)
					VRChatArchiveModPlugin.Logger.LogWarning("[InfinitePortal/destroy] " + what + " sur le portail " + id
						+ " (age " + age + ", " + _seenPortals.Count + " vivant(s)) — "
						+ (block ? "BLOQUE" : "laisse passer (plafond " + MaxPortals + ")")
						+ "  <- " + NativeStack.Capture());
				return !block;
			}
			catch { return true; }
		}

		// Switch every child of a kept portal back on. Walked by transform, depth- and count-capped, and
		// every step guarded: these objects are live (the scan just found them) but VRChat has been in the
		// middle of closing this one.
		private static void Revive(int id)
		{
			if (!_childSnap.TryGetValue(id, out var snap) || snap == null) return;
			int back = 0;
			foreach (var kv in snap)
			{
				try
				{
					Transform t = kv.Key;
					if (t == null || !NativeGuard.Alive(t)) continue;
					var go = t.gameObject;
					if (go == null) continue;
					if (go.activeSelf != kv.Value) { go.SetActive(kv.Value); back++; }
				}
				catch { }
			}
			if (back > 0 && !_revivedLogged)
			{
				_revivedLogged = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[InfinitePortal] portail garde : " + back
					+ " enfant(s) remis dans leur etat d'origine — VRChat les eteint avant de detruire la racine.");
			}
		}

		// The object surviving is not the same as the portal WORKING: closing also switches off the portal
		// behaviour itself and its trigger, so what is left is scenery you walk through. Both are ordinary
		// Unity properties on types VRChat does not obfuscate, so they can be set without calling anything
		// of the game's own.
		private static void Reopen(UnityEngine.Object portalComponent)
		{
			try
			{
				var beh = portalComponent.TryCast<Behaviour>();
				if (beh != null && !beh.enabled) beh.enabled = true;
				var comp = portalComponent.TryCast<Component>();
				var go = comp != null ? comp.gameObject : null;
				if (go == null) return;
				var box = go.GetComponent<BoxCollider>();
				if (box != null && !box.enabled) box.enabled = true;
			}
			catch { }
		}

		// WHAT CHANGES INSIDE THE PORTAL WHEN IT CLOSES.
		//
		// Cancelling every scheduled call on the portal AND its 32 child scripts did not stop the close, so
		// the order comes from outside the object (a manager holding the list). Rather than guess which one,
		// the object's own bytes are photographed while it is healthy and compared once the close has been
		// refused: whatever flipped is the state that says "closed", and that is what has to be held for the
		// portal to keep working. Read-only, one line, once per portal.
		private static readonly Dictionary<int, byte[]> _byteSnap = new Dictionary<int, byte[]>();
		private static readonly HashSet<int> _diffed = new HashSet<int>();
		private static readonly Dictionary<int, int> _riseOff = new Dictionary<int, int>();
		private const int SnapSize = 0x180;

		// MEASURED 2026-09-20, and it is one byte. Between a working portal and a closed one, the only
		// thing that changes in the object — besides the elapsed counter and our own pinned cap — is a
		// single byte flipping 0 -> 1 (0x120 on this build). That flag is what blanks the world image and
		// refuses the teleport, whatever the collider says. So the healthy bytes are written back, every
		// pass, skipping the counter and the cap (those two are SUPPOSED to move).
		private static bool _holdLogged;

		private static void HoldState(IntPtr ptr, int id)
		{
			if (!_byteSnap.TryGetValue(id, out byte[] was)) return;
			if (!NativeGuard.IsReadable(ptr, SnapSize)) return;
			int capOff = -1;
			if (_lifeSlot.TryGetValue(id, out long packed)) capOff = (int)(packed & 0xFFFFFFFF);
			_riseOff.TryGetValue(id, out int riseOff);

			var held = _holdLogged ? null : new System.Text.StringBuilder();
			for (int off = 0x10; off < SnapSize; off++)
			{
				if (capOff >= 0 && off >= capOff && off < capOff + 4) continue;
				if (riseOff > 0 && off >= riseOff && off < riseOff + 4) continue;
				byte cur;
				try { cur = Marshal.ReadByte(ptr, off); } catch { continue; }
				if (cur == was[off]) continue;
				try { Marshal.WriteByte(ptr, off, was[off]); } catch { continue; }
				held?.Append(" 0x").Append(off.ToString("X")).Append(':').Append(cur).Append("->").Append(was[off]);
			}
			if (held != null && held.Length > 0)
			{
				_holdLogged = true;
				VRChatArchiveModPlugin.Logger.LogInfo("[InfinitePortal] etat 'ouvert' reecrit dans le portail " + id + " :" + held);
			}
		}

		private static void SnapBytes(IntPtr ptr, int id)
		{
			if (_byteSnap.ContainsKey(id)) return;
			if (!NativeGuard.IsReadable(ptr, SnapSize)) return;
			var b = new byte[SnapSize];
			try { Marshal.Copy(ptr, b, 0, SnapSize); } catch { return; }
			_byteSnap[id] = b;
		}

		// (SnapSiblings / HoldSiblings lived here and were REMOVED in 3.9.244 — see the comment at the
		// call site. They wrote into components by an address remembered from an earlier pass, and a
		// remembered address is a promise the allocator does not keep.)

		private static void DiffBytes(IntPtr ptr, int id)
		{
			if (_diffed.Contains(id) || !_byteSnap.TryGetValue(id, out byte[] was)) return;
			if (!NativeGuard.IsReadable(ptr, SnapSize)) return;
			var now = new byte[SnapSize];
			try { Marshal.Copy(ptr, now, 0, SnapSize); } catch { return; }
			_diffed.Add(id);

			var sb = new System.Text.StringBuilder();
			for (int off = 0x10; off + 4 <= SnapSize; off += 4)
			{
				if (was[off] == now[off] && was[off + 1] == now[off + 1]
					&& was[off + 2] == now[off + 2] && was[off + 3] == now[off + 3]) continue;
				int iw = BitConverter.ToInt32(was, off), inw = BitConverter.ToInt32(now, off);
				float fw = BitConverter.ToSingle(was, off), fnw = BitConverter.ToSingle(now, off);
				if (sb.Length > 700) { sb.Append(" ..."); break; }
				sb.Append(" 0x").Append(off.ToString("X")).Append(':').Append(iw).Append("->").Append(inw);
				if (Math.Abs(fw) < 1e6 && Math.Abs(fnw) < 1e6)
					sb.Append(" (f ").Append(fw.ToString("0.##")).Append("->").Append(fnw.ToString("0.##")).Append(')');
			}
			VRChatArchiveModPlugin.Logger.LogWarning("[InfinitePortal/etat] portail " + id
				+ " — ce qui a change dans l'objet entre 'ouvert' et 'ferme' :"
				+ (sb.Length == 0 ? " RIEN (l'etat ferme n'est pas dans cet objet)" : sb.ToString()));
		}

		// The snapshot, taken once per portal while it is still healthy.
		private static void SnapChildren(UnityEngine.Object portalComponent, int id)
		{
			if (_childSnap.ContainsKey(id)) return;
			var list = new List<KeyValuePair<Transform, bool>>();
			try
			{
				var comp = portalComponent.TryCast<Component>();
				var root = comp != null ? comp.transform : null;
				if (root == null) return;
				var stack = new Stack<Transform>();
				stack.Push(root);
				while (stack.Count > 0 && list.Count < 200)
				{
					Transform t = stack.Pop();
					int n;
					try { n = t.childCount; } catch { continue; }
					for (int i = 0; i < n && list.Count < 200; i++)
					{
						Transform c;
						try { c = t.GetChild(i); } catch { continue; }
						if (c == null) continue;
						bool act;
						try { act = c.gameObject.activeSelf; } catch { continue; }
						list.Add(new KeyValuePair<Transform, bool>(c, act));
						stack.Push(c);
					}
				}
			}
			catch { }
			_childSnap[id] = list;
		}

		private static bool IsPortal(UnityEngine.Object obj, out int compId)
		{
			compId = 0;
			if (_portalPtrs.Count == 0) return false;
			IntPtr p = IL2CPP.Il2CppObjectBaseToPtr(obj);
			if (p == IntPtr.Zero || !NativeGuard.IsLiveObject(p)) return false;
			// ONLY pointers the 0.5 s scan has proved to be portals. GetComponent was called here once and
			// it is what crashed the game during a world unload: the object handed to Destroy is already
			// half gone, and asking it anything is an access violation.
			if (!_portalPtrs.TryGetValue(p, out compId)) return false;
			return true;
		}

		public override void OnUpdate()
		{
			if (!Active) return;
			float now;
			try { now = VaClock.Now; } catch { return; }
			if (now < _nextPass) return;
			_nextPass = now + PassInterval;

			try { KeepPortalsOpen(now); } catch { }
			try { KillCooldown(); } catch { }
		}

		// ---- lifetime: find every live portal, clamp the field that is counting down --------------

		private unsafe void KeepPortalsOpen(float now)
		{
			Type t = ResolvePortalType();
			if (t == null || _portalIl2 == null)
			{
				if (!_loggedNoType)
				{
					_loggedNoType = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[InfinitePortal] PortalInternal introuvable sur ce build — rien a garder ouvert.");
				}
				return;
			}

			var live = new HashSet<int>();
			var found = VRChatArchiveMod.Core.Live.AllOfType(_portalIl2);
			if (found == null) return;

			double nowTime; try { nowTime = VaClock.NowDouble; } catch { nowTime = now; }

			for (int i = 0; i < found.Length; i++)
			{
				var o = found[i];
				if (o == null || !NativeGuard.Alive(o)) continue;

				IntPtr ptr;
				try { ptr = IL2CPP.Il2CppObjectBaseToPtr(o); } catch { continue; }
				if (ptr == IntPtr.Zero || !NativeGuard.IsLiveObject(ptr)) continue;

				int id;
				try { id = o.GetInstanceID(); } catch { continue; }
				live.Add(id);
					_portalPtrs[ptr] = id;
				// AND THE GAMEOBJECT THAT CARRIES IT. Destroy is called on the object, not on the component,
				// so matching only the component pointer made the prefix miss every close and report
				// "aucun Object.Destroy vu" — a false negative, measured 2026-09-19.
				try
				{
					var comp = o.TryCast<Component>();
					var goo = comp != null ? comp.gameObject : null;
					if (goo != null)
					{
						IntPtr gp = IL2CPP.Il2CppObjectBaseToPtr(goo);
						if (gp != IntPtr.Zero) _portalPtrs[gp] = id;
					}
				}
				catch { }

				// A portal we kept alive: put its insides back on. Only for a few passes — the close
				// sequence runs once, so re-enabling forever would just be work every half second.
				SnapChildren(o, id);

				// STOP THE CLOSE BEFORE IT STARTS, rather than fighting its consequences.
				// Blocking the Destroy keeps the object but not the portal: by then VRChat has already
				// switched its insides off, dropped the world preview and disarmed the trigger, so what
				// survives is scenery. The close is not driven by any field we can clamp (the pinned cap
				// proved that) — it is scheduled when the portal is created. Unity can cancel exactly that,
				// with no call into any obfuscated code: CancelInvoke kills a delayed call, and
				// StopAllCoroutines kills a WaitForSeconds. Done once, a few seconds in, so the portal's
				// own setup (its world image loads in a coroutine) has already finished.
				if (!_closeStopped.Contains(id) && _seenPortals.TryGetValue(id, out float first0) && now - first0 > 3f)
				{
					_closeStopped.Add(id);
					// EVERY script of the portal, not just the one the scan found. Cancelling on that single
					// component left the close untouched (measured: the destroy still came at 29.9 s) — the
					// root carries a second script besides it, and the cosmetics child has its own.
					int stopped = 0;
					try
					{
						var comp0 = o.TryCast<Component>();
						var root0 = comp0 != null ? comp0.gameObject : null;
						if (root0 != null)
						{
							var all = root0.GetComponentsInChildren<MonoBehaviour>(true);
							if (all != null)
								for (int k = 0; k < all.Length && k < 64; k++)
								{
									var mb = all[k];
									if (mb == null) continue;
									try { mb.CancelInvoke(); mb.StopAllCoroutines(); stopped++; } catch { }
								}
						}
					}
					catch { }
					if (!_stopLogged)
					{
						_stopLogged = true;
						VRChatArchiveModPlugin.Logger.LogInfo("[InfinitePortal] fermeture programmee annulee sur le portail "
							+ id + " : " + stopped + " script(s) de sa hierarchie (CancelInvoke + StopAllCoroutines).");
					}
				}
				// Every pass, not a few: VRChat's close routine keeps switching things off after the
				// destroy it was denied, so putting them back once is not enough.
				// WRITING INTO THE PORTAL'S OTHER SCRIPTS IS GONE (3.9.244, after a crash).
				//
				// HoldSiblings wrote flag bytes into components found on an EARLIER pass, by their stored
				// address. An address is only valid while its object lives: VRChat destroys these the
				// moment the portal closes, the allocator hands the same address to something else, and
				// "is this address a live il2cpp object" then says YES about a DIFFERENT object — so the
				// write lands in an innocent one. The owner's game died with an access violation in a
				// session where the portal feature was the busiest module.
				//
				// What remains only ever touches `ptr`, which THIS pass just took from the live scan, and
				// Unity's own SetActive / enabled on objects proved alive the same frame. It did not make
				// a kept portal usable anyway — the state that matters is not in this object (see the
				// [InfinitePortal/etat] measurement), so the risk was being taken for nothing.
				if (_blockedIds.ContainsKey(id)) { DiffBytes(ptr, id); HoldState(ptr, id); Revive(id); Reopen(o); }
				else SnapBytes(ptr, id);

				// Already identified this portal's timer slot: keep topping it up, in its own type.
				if (_lifeSlot.TryGetValue(id, out long packed))
				{
					PinSlot(ptr, packed, now, nowTime);
					continue;
				}

				IntPtr cls;
				try { cls = IL2CPP.il2cpp_object_get_class(ptr); } catch { continue; }
				int size = 0;
				if (cls != IntPtr.Zero)
					try { size = (int)Il2CppInterop.Runtime.Runtime.UnityVersionHandler
						.Wrap((Il2CppInterop.Runtime.Runtime.Il2CppClass*)cls).InstanceSize; }
					catch { size = 0; }
				if (size < 0x20 || size > 0x4000) size = 0x400;   // no size? scan a safe window
				if (!NativeGuard.IsReadable(ptr, size)) continue;

				int dp; _dumpPasses.TryGetValue(id, out dp);
				bool dumpThis = dp < 3 && _dumpsLogged < 9;
				System.Text.StringBuilder dump = dumpThis ? new System.Text.StringBuilder() : null;

				// RAW SCAN — the timer is not a discoverable Single field on this build, so read the
				// object's own bytes and interpret each slot. MEASURED 2026-09-19: a dropped portal keeps
				// its lifetime as a CONSTANT float cap (f0x20 = 30.0) and counts an ELAPSED float UP toward
				// it (f0x11C rising); it closes when elapsed >= cap, and the shown seconds = cap - elapsed.
				// So nothing counts DOWN. Two shapes are handled: a decreasing stored counter (other
				// builds/worlds), and this count-UP model — a constant cap paired with a rising sibling.
				bool sawRising = false;
				int capOff = -1;
				for (int off = 0x10; off + 8 <= size; off += 4)
				{
					float fv = ReadFloat(ptr, off);
					int iv; try { iv = Marshal.ReadInt32(ptr, off); } catch { iv = int.MinValue; }
					double dv = ReadDouble(ptr, off);

					// Existing single-slot shapes: a stored "seconds left" that shrinks, or a close-time.
					Consider(id, off, KIND_FLOAT, fv, now, nowTime, ptr, dump);
					if (iv > 0 && iv < 3600) Consider(id, off, KIND_INT, iv, now, nowTime, ptr, dump);
					Consider(id, off, KIND_DOUBLE, dv, now, nowTime, ptr, dump);
					if (_lifeSlot.ContainsKey(id)) break;   // a decreasing/close-time slot won outright

					// Count-UP model: track this float's move since last pass.
					if (!float.IsNaN(fv) && !float.IsInfinity(fv))
					{
						long rk = ((long)id << 24) | ((long)9 << 22) | (uint)off;   // kind 9 = "rise track"
						bool had = _prev.TryGetValue(rk, out double pw);
						_prev[rk] = fv;
						if (had)
						{
							double d = fv - pw;
							// rose by roughly the elapsed time and stayed in the lifetime range → elapsed timer
							if (d > PassInterval * 0.3 && d < PassInterval * 4.0 && fv >= 0f && fv < 600f)
								{ sawRising = true; _riseOff[id] = off; }
						}
						// a steady value in a plausible portal-lifetime window → candidate cap
						if (capOff < 0 && Math.Abs(fv - Math.Round(fv)) < 0.01f && fv >= 8f && fv <= 120f)
							capOff = off;
					}
				}

				// The cap is only trusted when the object also shows a rising elapsed counter — that pairing
				// is what makes it a timer and not just some constant. Push the cap out of reach.
				if (!_lifeSlot.ContainsKey(id) && sawRising && capOff >= 0)
				{
					float wasCap = ReadFloat(ptr, capOff);
					_lifeSlot[id] = ((long)KIND_FLOAT << 40) | (uint)capOff;
					WriteFloat(ptr, capOff, BigLife);
					if (!_loggedLife)
					{
						_loggedLife = true;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[InfinitePortal] minuterie (modele compteur-montant) : plafond a l'offset 0x"
							+ capOff.ToString("X") + " (etait " + wasCap.ToString("0.0")
							+ ") pousse a l'infini — portail epingle.");
					}
				}

				if (dumpThis)
				{
					_dumpPasses[id] = dp + 1;
					_dumpsLogged++;
					VRChatArchiveModPlugin.Logger.LogWarning("[InfinitePortal/diag] portail " + id
						+ " taille 0x" + size.ToString("X")
						+ (dump.Length == 0 ? " (rien en [1,300])" : " candidats:" + dump)
						+ "  | now rt=" + now.ToString("0.0") + " t=" + nowTime.ToString("0.0"));
				}
			}

			// SECOND CLOSE MECHANISM — dropping a new portal shuts the previous one. Report it: a portal
			// that was alive last pass and is gone now was CLOSED. If it had a pinned cap and still
			// vanished, VRChat destroyed the object (the close is not a field we can clamp — it would need
			// the destroy itself prevented). This is exactly what the two-portal capture is meant to catch.
			foreach (var kv in _seenPortals)
			{
				if (live.Contains(kv.Key)) continue;
				bool wasPinned = _lifeSlot.ContainsKey(kv.Key);
				// GONE FROM THE SCAN IS NOT THE SAME AS DESTROYED. FindObjectsOfType skips INACTIVE objects,
				// so a portal merely switched off leaves the scan exactly like a destroyed one does. If its
				// memory is still a live il2cpp object, it was deactivated — and that is a very different
				// thing to prevent.
				bool stillInMemory = false;
				foreach (var pp in _portalPtrs)
					if (pp.Value == kv.Key && NativeGuard.IsLiveObject(pp.Key)) { stillInMemory = true; break; }
				VRChatArchiveModPlugin.Logger.LogWarning("[InfinitePortal/diag] portail " + kv.Key
					+ " DISPARU apres " + (now - kv.Value).ToString("0.0") + "s"
					+ (wasPinned ? " — alors qu'il etait EPINGLE (plafond force) : VRChat a DETRUIT l'objet, "
					              + "pas juste expire. La 2e fermeture n'est pas un champ a clamper."
					            : " (ferme par le jeu).") + " Portails vivants: " + live.Count
					+ (_destroyPatches.Count == 0 ? ""
					   : _destroySeen.Contains(kv.Key) ? " | un Destroy a ete vu (et bloque si sous le plafond) : il est passe quand meme par un autre chemin."
					   : " | AUCUN Object.Destroy vu pour lui : fermeture par un autre chemin (SetActive/pool/natif).")
				+ (stillInMemory ? " | SON OBJET EST ENCORE VIVANT EN MEMOIRE : il a ete DESACTIVE, pas detruit."
				                 : " | son objet n'est plus en memoire : vraiment detruit."));
			}
			// Note when several portals coexist — the goal state for "drop another, keep the first".
			if (live.Count > _maxCoexist)
			{
				_maxCoexist = live.Count;
				if (live.Count >= 2)
					VRChatArchiveModPlugin.Logger.LogWarning("[InfinitePortal/diag] " + live.Count
						+ " portails vivants EN MEME TEMPS.");
			}
			// Update the seen-set: drop the gone, keep first-seen times, add the new.
			var goneIds = new List<int>();
			foreach (var kv in _seenPortals) if (!live.Contains(kv.Key)) goneIds.Add(kv.Key);
			foreach (int g in goneIds) _seenPortals.Remove(g);
			foreach (int id2 in live) if (!_seenPortals.ContainsKey(id2)) _seenPortals[id2] = now;

			// The hooks live exactly as long as the portals do.
			if (live.Count > 0) InstallDestroyHooks(); else if (_destroyPatches.Count > 0) RemoveDestroyHooks();

			if (_portalPtrs.Count > 0)
			{
				List<IntPtr> goneP = null;
				foreach (var kv in _portalPtrs) if (!live.Contains(kv.Value)) (goneP ??= new List<IntPtr>()).Add(kv.Key);
				if (goneP != null) foreach (var g in goneP) _portalPtrs.Remove(g);
			}

			// Forget portals that are gone, so the maps do not grow across a session.
			if (_lifeSlot.Count > 0)
			{
				List<int> dead = null;
				foreach (var kv in _lifeSlot) if (!live.Contains(kv.Key)) (dead ??= new List<int>()).Add(kv.Key);
				if (dead != null) foreach (int d in dead) { _lifeSlot.Remove(d); _blockedIds.Remove(d); _childSnap.Remove(d); _closeStopped.Remove(d); _byteSnap.Remove(d); _diffed.Remove(d); _riseOff.Remove(d); }
			}
		}


		// Look at one slot read one way: log it if it is in the "seconds" range, and if across two
		// samples it dropped by roughly the elapsed time (a live countdown or a close-time being
		// approached), pin it. Kept in a double so float/int/double share one path.
		private void Consider(int id, int off, int kind, double v, float now, double nowTime,
			IntPtr ptr, System.Text.StringBuilder dump)
		{
			if (double.IsNaN(v) || double.IsInfinity(v)) return;

			// A raw display counter reads ~[1,300]; an absolute close-time reads ~now+[1,300] on either
			// clock. Everything else in the object (positions, colours, huge timestamps) is ignored.
			double remRt = v - now;
			double remT = v - nowTime;
			bool small = v > 0.5 && v < 300.0;
			bool nearRt = remRt > 1.0 && remRt < 300.0;
			bool nearT = remT > 1.0 && remT < 300.0;
			if (!small && !nearRt && !nearT) return;

			if (dump != null && dump.Length < 900)
				dump.Append(' ').Append(K(kind)).Append("0x").Append(off.ToString("X")).Append('=')
					.Append(v.ToString("0.0"));

			long key = ((long)id << 24) | ((long)kind << 22) | (uint)off;
			bool had = _prev.TryGetValue(key, out double was);
			_prev[key] = v;
			if (!had) return;

			double drop = was - v;
			float elapsed = PassInterval;
			// It behaves like a clock: it fell by about the time that passed (counter → toward 0;
			// close-time → toward now, so the remaining shrank). Tolerant, but enough to reject noise.
			bool ticks = drop > elapsed * 0.3 && drop < elapsed * 4.0;
			bool inWindow = small || nearRt || nearT;
			if (!ticks || !inWindow) return;

			_lifeSlot[id] = ((long)kind << 40) | (uint)off;
			PinSlot(ptr, _lifeSlot[id], now, nowTime);
			if (!_loggedLife)
			{
				_loggedLife = true;
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[InfinitePortal] minuterie identifiee : " + K(kind) + "offset 0x" + off.ToString("X")
					+ " (tombait " + was.ToString("0.0") + "->" + v.ToString("0.0")
					+ ", rt" + remRt.ToString("+0.0;-0.0") + " t" + remT.ToString("+0.0;-0.0")
					+ ") — portail epingle.");
			}
		}

		private static string K(int kind) => kind == KIND_FLOAT ? "f" : kind == KIND_DOUBLE ? "d" : "i";

		// Push the identified slot far out: a counter to a huge value, an absolute close-time far into
		// the future on whichever clock it tracks (guessed as the nearer of the two, else realtime).
		private void PinSlot(IntPtr ptr, long packed, float now, double nowTime)
		{
			int kind = (int)(packed >> 40);
			int off = (int)(packed & 0xFFFFFFFF);
			try
			{
				if (kind == KIND_INT) { Marshal.WriteInt32(ptr, off, 1000000000); return; }
				if (kind == KIND_FLOAT)
				{
					float cur = ReadFloat(ptr, off);
					// small value = "seconds left" counter; large = an absolute time to push past.
					WriteFloat(ptr, off, cur < 1000f ? BigLife : now + BigLife);
					return;
				}
				double dcur = ReadDouble(ptr, off);
				WriteDouble(ptr, off, dcur < 1000.0 ? 1e8 : nowTime + 1e8);
			}
			catch { }
		}

		private static double ReadDouble(IntPtr obj, int off)
		{
			try { return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(obj, off)); }
			catch { return double.NaN; }
		}

		private static void WriteDouble(IntPtr obj, int off, double v)
		{
			try { Marshal.WriteInt64(obj, off, BitConverter.DoubleToInt64Bits(v)); }
			catch { }
		}

		// ---- cooldown: zero PortalManager's static timers so the next drop is allowed at once -----

		private void KillCooldown()
		{
			IntPtr cls = ResolveManagerClass();
			if (cls == IntPtr.Zero) return;

			foreach (IntPtr f in MemberAlign.LiveFields(cls))
			{
				int flags;
				try { flags = IL2CPP.il2cpp_field_get_flags(f); } catch { continue; }
				if ((flags & FIELD_ATTRIBUTE_STATIC) == 0) continue;
				if (!IsSingle(f)) continue;

				float cur = ReadStaticFloat(f);
				if (float.IsNaN(cur)) continue;

				if (!_loggedCooldown)
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[InfinitePortal] cooldown static PortalManager = " + cur.ToString("0.00") + " -> 0.");

				if (cur != 0f) WriteStaticFloat(f, 0f);
			}
			_loggedCooldown = true;
		}

		// ---- resolution ---------------------------------------------------------------------------

		private static Type ResolvePortalType()
		{
			if (_portalResolved) return _portalType;
			_portalResolved = true;
			string[] names = { "PortalInternal", "VRC.PortalInternal", "PortalInternalDynamic" };
			try
			{
				foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (a.GetName().Name != "Assembly-CSharp") continue;
					foreach (string n in names)
					{
						Type t = a.GetType(n, false);
						if (t != null) { _portalType = t; break; }
					}
					break;
				}
				if (_portalType == null)
					foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
						foreach (var t in SafeTypes(a))
							if (t != null && t.Name == "PortalInternal") { _portalType = t; break; }

				if (_portalType != null)
				{
					try { _portalIl2 = Il2CppType.From(_portalType); }
					catch { _portalType = null; _portalIl2 = null; }
				}
			}
			catch { }
			return _portalType;
		}

		private static IntPtr ResolveManagerClass()
		{
			if (_managerResolved) return _managerClass;
			_managerResolved = true;
			try
			{
				Type mgr = null;
				foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (a.GetName().Name != "Assembly-CSharp") continue;
					mgr = a.GetType("PortalManager", false);
					break;
				}
				if (mgr != null)
				{
					var il2 = Il2CppType.From(mgr);
					if (il2 != null)
						_managerClass = IL2CPP.il2cpp_class_from_system_type(IL2CPP.Il2CppObjectBaseToPtr(il2));
				}
			}
			catch { _managerClass = IntPtr.Zero; }
			return _managerClass;
		}

		private static Type[] SafeTypes(System.Reflection.Assembly a)
		{
			try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
		}

		// ---- field primitives (pointers only, guarded) --------------------------------------------

		private static bool IsInstanceSingle(IntPtr field)
		{
			try
			{
				int flags = IL2CPP.il2cpp_field_get_flags(field);
				if ((flags & FIELD_ATTRIBUTE_STATIC) != 0) return false;
			}
			catch { return false; }
			return IsSingle(field);
		}

		private static bool IsSingle(IntPtr field)
		{
			try
			{
				IntPtr tp = MemberAlign.FieldTypePtr(field);
				if (tp == IntPtr.Zero) return false;
				IntPtr fk = IL2CPP.il2cpp_class_from_type(tp);
				if (fk == IntPtr.Zero) return false;
				IntPtr np = IL2CPP.il2cpp_class_get_name(fk);
				return np != IntPtr.Zero && Marshal.PtrToStringAnsi(np) == "Single";
			}
			catch { return false; }
		}

		private static float ReadFloat(IntPtr obj, int off)
		{
			try { return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(obj, (int)off)); }
			catch { return float.NaN; }
		}

		private static void WriteFloat(IntPtr obj, int off, float v)
		{
			try { Marshal.WriteInt32(obj, (int)off, BitConverter.SingleToInt32Bits(v)); }
			catch { }
		}

		private static float ReadStaticFloat(IntPtr field)
		{
			try
			{
				float v = 0f;
				unsafe { IL2CPP.il2cpp_field_static_get_value(field, &v); }
				return v;
			}
			catch { return float.NaN; }
		}

		private static void WriteStaticFloat(IntPtr field, float v)
		{
			try { unsafe { IL2CPP.il2cpp_field_static_set_value(field, &v); } }
			catch { }
		}

		public override void OnShutdown()
		{
			_prev.Clear();
			_lifeSlot.Clear();
		}
	}
}
