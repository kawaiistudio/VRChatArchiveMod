using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FLOAT OBJECTS — a TOGGLE that removes gravity from every pickup in the world.
	//
	// Per object, the way the owner showed it done elsewhere: Rigidbody.useGravity = false on each
	// VRC_Pickup's body — NOT Physics.gravity = 0, which also stops lifts, doors and physics puzzles for
	// you. Objects you own float for everyone (their positions are synced), the rest only on your
	// screen. Re-swept every 2 s so pickups that appear later float too; OFF puts every body's own
	// useGravity back and lets them fall.
	public class ObjectGravityModule : IModule
	{
		public override string Name => "ObjectGravity";

		public static bool Active { get; private set; }
		public static int Count => _bodies.Count;
		public static string Status = "";

		// Id kept with the body: dropping a body from the list has to drop its id too, or a pickup that
		// only LOOKED dead for one step (a proxy the runtime recycled, a body re-enabled by the world)
		// is barred from _ids for ever and silently stops floating with no way back but a re-toggle.
		// CheckedAt: when this body's liveness was last PROVEN. NativeGuard.Alive is two VirtualQuery
		// syscalls (Core/NativeGuard.cs), and in VRChat's address space a syscall that walks the
		// kernel's region tree is not the 1-3 us of the textbook — measured here it was the entire
		// cost of the module. So liveness is cached per body, exactly the way PlayerRef caches the
		// local player behind RevalidateSec, and re-proven once a second instead of every visit.
		private sealed class Held { public Rigidbody Body; public bool Was; public int Id; public float CheckedAt; }
		private static readonly List<Held> _bodies = new List<Held>();
		private static readonly HashSet<int> _ids = new HashSet<int>();
		private static float _nextSweep;
		private static float _nextReassert;       // time gate for the re-assert; see OnFixedUpdate
		private static Il2CppSystem.Type _pickupIl2;

		// The pending object-table snapshot, walked a slice at a time — see BeginScan/StepScan.
		private const int ScanBudget = 12;        // pickups examined per frame
		private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> _scan;
		private static int _scanAt, _scanAdded, _scanSkipped;
		private static double _queryMs, _scanMs;

		// WHY THIS LOOP EXISTS AT ALL. VRC_Pickup turns gravity back ON at the moment you DROP an
		// object — it restores the body's original useGravity as part of releasing it. When the
		// re-assert lived on the 2 s discovery timer, an object you threw kept its gravity for up to
		// two seconds: it fell, hit the floor, and only then started floating. The visible bug was
		// "it only floats after it lands".
		//
		// TEN TIMES A SECOND, NOT EVERY PHYSICS STEP (2026-09-08, the measurement that settled it).
		//
		// Spreading the walk was not enough, because the cost was never the NUMBER of bodies. With
		// 25 pickups this module was still billed 772 ms of every second while its discovery sweep
		// accounted for 26 — the rest was this loop, at roughly a millisecond PER BODY VISITED.
		// A property read does not cost that. NativeGuard.Alive does: two VirtualQuery syscalls, and
		// in a process with VRChat's enormous fragmented address space the kernel's region walk is
		// hundreds of microseconds, not the 1-3 us it costs in a small process. Multiply by the
		// physics step, which Unity runs in BURSTS when the frame rate collapses (up to
		// maximumDeltaTime / fixedDeltaTime steps per frame, ~30 on this build), and the loop was
		// paying for thousands of syscalls a second to re-prove the same 25 objects.
		//
		// So: a time gate, not a step gate. Every 100 ms the WHOLE list is re-asserted — better
		// coverage than the slice it replaces — and each body's liveness is re-proven at most once a
		// second (Held.CheckedAt). An object you drop floats again within 100 ms, which was already
		// the accepted trade and is imperceptible next to the two seconds this loop was built to fix.
		//
		// Still FixedUpdate rather than Update: useGravity is read by the physics step and
		// FixedUpdate runs immediately before it, so the next step honours it and a throw keeps its
		// velocity.
		public override void OnFixedUpdate()
		{
			if (!Active) return;
			try
			{
				float now = VaClock.Now;
				if (now < _nextReassert) return;
				_nextReassert = now + 0.1f;
				ReassertAll(now);
			}
			catch (Exception e) { Status = "float objects: " + e.Message; }
		}

		public override void OnUpdate()
		{
			try
			{
				if (!Active) return;

				// A snapshot in progress is finished first, a slice per frame, before another is
				// taken — the whole point is that no single frame pays for all of it.
				if (_scan != null) { StepScan(); return; }

				float now = VaClock.Now;
				if (now < _nextSweep) return;
				// Only DISCOVERY is rate-limited: FindObjectsOfType walks the whole object table.
				_nextSweep = now + 2f;
				ReassertAll(now);
				BeginScan();
			}
			catch (Exception e) { Status = "float objects: " + e.Message; }
		}

		public static void Toggle()
		{
			if (Active) { RestoreAll(); _scan = null; Active = false; Status = "object gravity back"; }
			else
			{
				// Only START the scan here. Finishing it inside the toggle would put the whole
				// stall back on the frame the button was pressed; OnUpdate walks it out over the
				// next few frames instead, so the first pickups float almost immediately and the
				// rest follow without a hitch.
				Active = true; _nextSweep = 0f; _nextReassert = 0f; _scan = null;
				ReassertAll(VaClock.Now); BeginScan();
				Status = "object gravity removed — scanning…";
			}
			VaTagsModule.LastStatus = Status;
			VRChatArchiveModPlugin.Logger.LogInfo("[ObjectGravity] " + Status);
		}

		// EVERY ENTRY, WITH THE LIVENESS PROOF CACHED. Called ten times a second by OnFixedUpdate,
		// and once per discovery sweep.
		//
		// The guard is the expensive line, not the list. NativeGuard.Alive is two VirtualQuery
		// syscalls; re-proving 62 bodies on every visit is what cost 664 ms of every second. A body
		// that was alive 100 ms ago is alive now in every case that matters, and the ones that are not
		// are caught within a second — so it is re-proven at most once per second per body, and the
		// visits in between read useGravity and nothing else.
		//
		// A body whose proxy died between checks is still safe: reading useGravity on it throws a
		// managed exception from the interop wrapper (the property goes through il2cpp_runtime_invoke,
		// not a raw field dereference), and the catch drops it. The AV that NativeGuard exists to stop
		// comes from raw field reads, which this loop does not do.
		private static void ReassertAll(float now)
		{
			for (int i = _bodies.Count - 1; i >= 0; i--)
			{
				var h = _bodies[i];
				try
				{
					if (h.Body == null) { Forget(i); continue; }
					if (now - h.CheckedAt >= 1f)
					{
						if (!NativeGuard.Alive(h.Body)) { Forget(i); continue; }
						h.CheckedAt = now;
					}
					if (h.Body.useGravity) h.Body.useGravity = false;
				}
				catch
				{
					// RESTORE BEFORE FORGETTING (2026-09-13). Discovery already wrote useGravity =
					// false, so an entry dropped here on a merely TRANSIENT throw left that pickup
					// floating for the rest of the session: _bodies is the only ledger RestoreAll()
					// walks, and _ids kept the object's id so discovery could not re-adopt it either.
					// Try to hand gravity back first — if the body really is gone this throws again
					// and is swallowed, which is the same outcome as before.
					try { if (h.Body != null && NativeGuard.Alive(h.Body)) h.Body.useGravity = h.Was; } catch { }
					Forget(i);
				}
			}
		}

		// Removes entry i from both the list and the id set, so the next discovery may take it again.
		private static void Forget(int i)
		{
			try { _ids.Remove(_bodies[i].Id); } catch { }
			_bodies.RemoveAt(i);
		}

		// DISCOVERY IS A STALL, SO IT IS SPREAD OVER FRAMES (2026-09-07).
		//
		// The old Discover() did the whole thing in one call, and the timer added to it measured
		// 85-318 ms EVERY TWO SECONDS on the main thread of a world with 183 pickups. That is not a
		// background cost, it is a third of a second where the game stops dead, over and over --
		// "float object makes me lag" exactly. The [Perf] report agreed while pointing the wrong
		// way: it only prints on a second whose fps collapsed, so it fired precisely on the seconds
		// the sweep landed in and billed the whole second to this module.
		//
		// It is now two halves. BeginScan takes the object-table snapshot (one FindObjectsOfType,
		// the part that cannot be split) and StepScan walks a slice of that snapshot per frame.
		// Both are timed separately, because "the query is slow" and "there are too many pickups"
		// need different answers and the old single number could not tell them apart.
		private static void BeginScan()
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				// NON-GENERIC: FindObjectsOfType<VRC_Pickup>() returns nothing on this build.
				if (_pickupIl2 == null) _pickupIl2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.SDKBase.VRC_Pickup>();
				_scan = VRChatArchiveMod.Core.Live.AllOfType(_pickupIl2);
				_scanAt = 0; _scanAdded = 0; _scanSkipped = 0; _scanMs = 0.0;
			}
			catch (Exception e)
			{
				_scan = null;
				VRChatArchiveModPlugin.Logger.LogWarning("[ObjectGravity] query: " + e.Message);
			}
			sw.Stop();
			_queryMs = sw.Elapsed.TotalMilliseconds;
		}

		// One slice of the pending snapshot. Every pickup here costs a TryCast, a NativeGuard probe,
		// a component lookup and -- when the synced filter is on -- a walk of its component list, so
		// a small budget is the point.
		private static void StepScan()
		{
			if (_scan == null) return;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			bool syncedOnly = true;
			try { syncedOnly = ModConfig.FloatSyncedOnly.Value; } catch { }

			int end = _scanAt + ScanBudget;
			int n = 0;
			try { n = _scan.Length; } catch { _scan = null; return; }
			if (end > n) end = n;

			for (; _scanAt < end; _scanAt++)
			{
				try
				{
					var o = _scan[_scanAt];
					var p = o != null ? o.TryCast<VRC.SDKBase.VRC_Pickup>() : null;
					if (p == null || !NativeGuard.Alive(p)) continue;

					// A VRC_Pickup on its own floats only on YOUR screen: nothing carries its
					// position to the instance. VRCObjectSync (or a world script holding one) is
					// what makes the float visible to everyone, so that is the set worth paying
					// for -- and skipping the rest is most of the saving in a prop-heavy world.
					if (syncedOnly && !HasObjectSync(p)) { _scanSkipped++; continue; }

					Rigidbody rb = null;
					try
					{
						rb = p.GetComponent<Rigidbody>();
						// NOT ??: a destroyed UnityEngine.Object is non-null BY REFERENCE, so ??
						// would keep the dead proxy and skip the fallback. The == operator is the
						// one that asks the engine.
						if (rb == null) rb = p.GetComponentInChildren<Rigidbody>();
					}
					catch { }
					if (rb == null || !NativeGuard.Alive(rb)) continue;

					int id; try { id = rb.GetInstanceID(); } catch { continue; }
					if (!_ids.Add(id)) continue;
					var h = new Held { Body = rb, Was = rb.useGravity, Id = id };
					if (rb.useGravity) rb.useGravity = false;
					_bodies.Add(h); _scanAdded++;
				}
				catch { }
			}

			sw.Stop();
			_scanMs += sw.Elapsed.TotalMilliseconds;

			if (_scanAt < n) return;   // more slices to come

			// Finished this snapshot.
			_scan = null;
			if (_scanAdded > 0)
				Status = "object gravity removed — " + _bodies.Count + " pickup(s) floating";
			if (_queryMs + _scanMs > 40.0 || _scanAdded > 0)
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[ObjectGravity] sweep: " + n + " pickup(s) seen, +" + _scanAdded + " taken, "
					+ _scanSkipped + " skipped (no ObjectSync), " + _bodies.Count + " floating — query "
					+ _queryMs.ToString("0.#") + " ms, walk " + _scanMs.ToString("0.#") + " ms spread over "
					+ ((n + ScanBudget - 1) / ScanBudget) + " frame(s).");
		}

		// Does this pickup's position actually reach the other clients? Same test MarkModule uses to
		// answer "can anyone else see these move": a component whose type name carries ObjectSync.
		// By NAME because the concrete type differs between VRC_ObjectSync and VRCObjectSync builds.
		private static bool HasObjectSync(Component p)
		{
			try
			{
				var go = p.gameObject;
				if (go == null || !NativeGuard.Alive(go)) return false;
				var comps = go.GetComponents<Component>();
				if (comps == null) return false;
				for (int i = 0; i < comps.Length; i++)
				{
					var c = comps[i];
					if (c == null) continue;
					string nm;
					try { nm = Core.MenuCard.Il2CppNameOf(c) ?? ""; } catch { continue; }
					if (nm.Length == 0 || nm == "?") { try { nm = c.GetIl2CppType().Name ?? ""; } catch { } }
					if (nm.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
		}

		private static void RestoreAll()
		{
			for (int i = 0; i < _bodies.Count; i++)
			{
				var h = _bodies[i];
				try { if (h.Body != null && NativeGuard.Alive(h.Body)) h.Body.useGravity = h.Was; } catch { }
			}
			_bodies.Clear(); _ids.Clear();
		}

		public override void OnSceneLoaded(int buildIndex) { _bodies.Clear(); _ids.Clear(); _scan = null; Active = false; }   // the objects left with the world
		public override void OnShutdown() { RestoreAll(); Active = false; }
	}
}
