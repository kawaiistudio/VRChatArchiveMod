using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FORCE PICKUP — make a world's LOCKED pickups grabbable again, so you pick them up with your
	// own hands the way the world intended.
	//
	// This is not the old Force Grab, and the difference is the whole point. Force Grab teleported an
	// object in front of the camera and drove its transform every frame: the object moved, but you
	// were never HOLDING it, so its script never ran its pickup logic and a gun never fired, a tool
	// never worked, a door key never opened anything.
	//
	// A pickup is locked in one of three ordinary ways, all of them plain fields on VRC_Pickup:
	//
	//   * pickupable = false      the world switched grabbing off outright
	//   * proximity  = tiny       you have to be unrealistically close for VRChat to offer the grab
	//   * DisallowTheft = true    it cannot be taken while somebody else holds it
	//
	// So this flips those fields instead of moving anything. After that VRChat's own pickup system
	// does the work: you look at the object, you press grab, you are holding it for real — with the
	// world's Udon running exactly as it does for a normal player. Everything is REMEMBERED and put
	// back on toggle-off, so a world is never left permanently altered.
	//
	// LOCAL: these fields live on your client's copy of the object. Unlocking one changes what YOU
	// are allowed to pick up; it does not unlock it for anyone else, and it never takes an object
	// out of another player's hands (DisallowTheft is only relaxed on request, and taking a held
	// object still goes through VRChat's own ownership rules).
	public class ForcePickupModule : IModule
	{
		public override string Name => "ForcePickup";

		public static bool Active { get; private set; }
		public static string Status = "";
		public static int Unlocked;

		// What a pickup looked like before we touched it, so toggle-off restores exactly that.
		private sealed class Original
		{
			public VRC.SDKBase.VRC_Pickup P;
			public bool Pickupable;
			public float Proximity;
			public bool DisallowTheft;
		}

		private static readonly List<Original> Touched = new List<Original>();
		private static float _nextSweep;
		private static bool _loggedNoPickups;

		// Re-swept on a slow timer, not per frame: a world spawns pickups as you play (a shop hands
		// you an item, a game respawns props), and a one-shot unlock would only ever cover what
		// happened to exist the moment you pressed the button. Two seconds is far below noticing.
		private const float SweepSeconds = 2f;

		public override void OnUpdate()
		{
			try
			{
				if (Input.GetKey(KeyCode.RightShift) && Input.GetKeyDown(KeyCode.G)) Toggle();

				if (!Active) return;
				float now = VaClock.Now;
				if (now < _nextSweep) return;
				_nextSweep = now + SweepSeconds;
				Sweep();
			}
			catch { }
		}

		public static void Toggle()
		{
			if (Active) Stop("force pickup off — every pickup put back the way the world had it");
			else Start();
		}

		public static void Start()
		{
			Active = true;
			_nextSweep = 0f;
			Unlocked = 0;
			Sweep();
			Status = Unlocked > 0
				? "force pickup ON — " + Unlocked + " locked pickup(s) unlocked, grab them normally"
				: "force pickup ON — nothing here was locked";
			VRChatArchiveModPlugin.Logger.LogInfo("[ForcePickup] on — " + Unlocked + " unlocked.");
		}

		public static void Stop(string why)
		{
			// RESTORE FIRST, then forget: a half-restored world is worse than one we never touched.
			for (int i = 0; i < Touched.Count; i++)
			{
				var o = Touched[i];
				try
				{
					if (o == null || o.P == null || !Core.NativeGuard.Alive(o.P)) continue;
					o.P.pickupable = o.Pickupable;
					o.P.proximity = o.Proximity;
					o.P.DisallowTheft = o.DisallowTheft;
				}
				catch { }
			}
			Touched.Clear();
			Active = false;
			Unlocked = 0;
			if (!string.IsNullOrEmpty(why)) Status = why;
			VRChatArchiveModPlugin.Logger.LogInfo("[ForcePickup] off — restored.");
		}

		// A world change usually destroys every pickup we recorded — but NOT ALWAYS, and that is the
		// bug this used to have (2026-09-13). OnSceneLoaded also fires for additive and UI scene
		// loads, where the world's pickups survive; clearing the ledger then left every unlocked
		// object with pickupable = true and proximity = 100000 for the rest of the session, with
		// nothing left to restore it.
		//
		// Restoring first is safe in BOTH cases: the loop proves liveness with NativeGuard.Alive and
		// simply skips anything the load did destroy, so this costs nothing on a real world change
		// and is the whole fix on a partial one.
		public override void OnSceneLoaded(int buildIndex)
		{
			for (int i = 0; i < Touched.Count; i++)
			{
				var o = Touched[i];
				try
				{
					if (o == null || o.P == null || !Core.NativeGuard.Alive(o.P)) continue;
					o.P.pickupable = o.Pickupable;
					o.P.proximity = o.Proximity;
					o.P.DisallowTheft = o.DisallowTheft;
				}
				catch { }
			}
			Touched.Clear();
			Unlocked = 0;
			_loggedNoPickups = false;
			if (Active) _nextSweep = 0f;   // re-unlock in the new world
		}

		public override void OnShutdown() => Stop(null);

		private static void Sweep()
		{
			try
			{
				// ROBUST ENUMERATION. The generic FindObjectsOfType<VRC_Pickup>() came back EMPTY on this
				// build — Il2CppInterop's generic type resolution does not always find the il2cpp class,
				// which is why the count was stuck at 0. The mod already works around this elsewhere
				// (Core/Menu.cs) with the NON-generic overload fed an explicit Il2CppType, so use that:
				// resolve the class once, enumerate, and TryCast each result back.
				var il2 = Il2CppInterop.Runtime.Il2CppType.Of<VRC.SDKBase.VRC_Pickup>();
				var found = VRChatArchiveMod.Core.Live.AllOfType(il2);

				float wantProximity = 100f;
				try { wantProximity = Mathf.Max(1f, ModConfig.ForcePickupRange.Value); } catch { }
				bool allowTheft = false;
				try { allowTheft = ModConfig.ForcePickupAllowTheft.Value; } catch { }

				int scanned = 0;
				if (found != null)
				{
					for (int fi = 0; fi < found.Length; fi++)
					{
						var p = found[fi] != null ? found[fi].TryCast<VRC.SDKBase.VRC_Pickup>() : null;
						// A dead wrapper's fields are an access violation, not an exception.
						if (p == null || !Core.NativeGuard.Alive(p)) continue;
						scanned++;
						try
						{
							bool already = false;
							for (int i = 0; i < Touched.Count; i++)
								if (Touched[i] != null && ReferenceEquals(Touched[i].P, p)) { already = true; break; }
							if (already) continue;

							// PEUT IMPORTE — make EVERY pickup grabbable, not only the ones we judged
							// "locked". The owner wants to grab any pickup the world has, full stop; a
							// pickup that was already grabbable is still recorded (a no-op change) so
							// toggle-off restores the world's exact values and the count is "everything".
							Touched.Add(new Original
							{
								P = p,
								Pickupable = p.pickupable,
								Proximity = p.proximity,
								DisallowTheft = p.DisallowTheft,
							});

							// ILLIMITÉ — force a huge grab range so any pickup can be taken from anywhere, not
							// only within a world's tiny proximity (or the 100 m default). pickupable=true
							// is what makes you actually HOLD it, so its Udon runs and the item works. The
							// world's own values are remembered above and restored exactly on toggle-off.
							float target = Mathf.Max(wantProximity, 100000f);
							p.pickupable = true;
							if (p.proximity < target) p.proximity = target;
							if (allowTheft && p.DisallowTheft) p.DisallowTheft = false;
						}
						catch { }
					}
				}

				// How many pickups we now hold grabbable — everything the sweep has touched in this
				// world, so a later sweep never lowers it.
				Unlocked = Touched.Count;
				if (scanned == 0)
				{
					if (!_loggedNoPickups)
					{
						_loggedNoPickups = true;
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[ForcePickup] no VRC_Pickup found in this world — it likely uses a custom/Udon "
							+ "pickup system, which Force Pickup cannot unlock (use FORCE GRAB for raw objects).");
					}
					// Back off sweep when this world has no VRC_Pickups at all so we don't spam FindObjectsOfType
					_nextSweep = VaClock.Now + 15f;
				}
			}
			catch (Exception e) { Status = "sweep failed: " + e.Message; }
		}
	}
}
