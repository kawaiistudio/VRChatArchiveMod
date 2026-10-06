using System;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// CUSTOM USERNAME — the name the world's Udon scripts read for you, on this client.
	//
	// THE MECHANISM, PROVEN RATHER THAN ASSUMED (2026-09-08). The first cut of this shipped on a
	// grep: "get_displayName and set_displayName appear as strings inside VRCSDKBase.dll" says
	// nothing about which type owns them, and it was withdrawn after a crash it probably did not
	// cause. Decompiling the actual type settles it — VRC.SDKBase.VRCPlayerApi.displayName is a
	// writable instance field, and Il2CppInterop's generated setter is well formed:
	//
	//     set {
	//       IntPtr num = IL2CPP.Il2CppObjectBaseToPtrNotNull(this);
	//       IL2CPP.FieldWriteWbarrierStub(num,
	//           (IntPtr)((nint)num + (int)IL2CPP.il2cpp_field_get_offset(NativeFieldInfoPtr_displayName)),
	//           IL2CPP.ManagedStringToIl2Cpp(value));
	//     }
	//
	// Base pointer plus offset, once, through a GC write barrier. Nothing to be clever about.
	//
	// WHICH MAKES THE FIELD OFFSET THE ONLY REAL HAZARD, and it is a big one. That setter calls
	// il2cpp_field_get_offset, which Il2CppInterop reads from FieldInfo+0x08 — the slot that holds
	// the METADATA TOKEN on this build, not the offset (Core/FieldOffsetFix.cs). Unrepaired, the
	// write lands at base + 0x04000nnn: far outside the object, corrupting whatever is there, with
	// the access violation arriving later and somewhere else. So this module REFUSES TO ARM unless
	// FieldOffsetFix.Verified, exactly as Core/Il2CppDelegates gates the delegate bridge.
	//
	// WHAT IT REACHES. Every client builds its own VRCPlayerApi objects from the network, so this
	// writes the one in OUR process: the Udon scripts that see the new name are the ones running
	// HERE. Your nameplate is drawn by VRChat from the API record, not by Udon, so it keeps your
	// real name — this is a custom name inside worlds, not a change of pseudonym. It does reach the
	// others when a world copies what it read into a synced variable, which is the usual pattern for
	// leaderboards and name signs: the world read it here and sent it itself.
	//
	// IT IS VERIFIED BY READ-BACK. The write is followed by a read, and the log says whether the two
	// agree. That is the difference between "the mod says it applied it" and knowing it landed — and
	// it is what the first version lacked when its own PLAYERS panel appeared to disagree with it
	// (that panel reads EspModule's cache, which is a different thing entirely).
	public class SpoofModule : IModule
	{
		public override string Name => "Spoof";

		public static string Status = "";

		/// <summary>Your true display name, captured before anything was written over it.</summary>
		public static string RealName { get; private set; } = "";

		/// <summary>The name last successfully written, or "" when off.</summary>
		public static string Applied { get; private set; } = "";

		/// <summary>False when FieldOffsetFix could not repair the offset slot: the feature is then
		/// off for the session rather than writing to a wild address.</summary>
		public static bool Armed { get; private set; }

		private static bool _armChecked;
		private static float _nextCheck;
		private static string _lastLogged = "";
		private static IntPtr _lastApi = IntPtr.Zero;

		// DRIFT INSTRUMENTATION (2026-09-08). "It is not reliable enough" could not be confirmed or
		// denied from the log, because the log only spoke when the WANTED name changed: every silent
		// re-write after VRChat put the real name back looked exactly like nothing happening. If the
		// game rewrites this field, then between two of our passes it holds the real name, and any
		// Udon script reading in that window sees it — which is precisely what unreliable feels like.
		// So the passes are counted: how many found the value still ours, and how many found it
		// reverted. A number tells us whether we are fighting the game, and how fast it fights back.
		private static int _held, _reverted;
		private static float _nextStatLog;

		public override void OnUpdate()
		{
			try
			{
				float now = VaClock.Now;
				if (now < _nextCheck) return;
				// TEN TIMES A SECOND, not twice. A read of displayName is one il2cpp field access —
				// far cheaper than the NativeGuard syscalls that made ObjectGravity expensive — so
				// the window in which a world could read the real name back is 100 ms instead of
				// 500. Rejoining a world rebuilds the local VRCPlayerApi and restores the real name,
				// which is why this has to keep watching at all rather than write once.
				_nextCheck = now + 0.1f;

				if (!_armChecked)
				{
					_armChecked = true;
					try { Armed = FieldOffsetFix.Verified; } catch { Armed = false; }
					if (!Armed)
					{
						Status = "custom username unavailable: field offsets unrepaired on this build";
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[Spoof] NOT ARMED — FieldOffsetFix could not verify the offset slot, so a field "
							+ "write would land outside the object. Custom username is off for this session.");
					}
				}
				if (!Armed) return;

				// FEATURE NOT IN USE: NOTHING TO DO, SO DO NOTHING (2026-09-13).
				//
				// Everything below — LocalApi(), NativeGuard.Alive() (a VirtualQuery syscall), the
				// pointer read, ReadName() — ran ten times a second for every user whether or not a
				// custom name was set: 68 ms/s in the profiler for a feature most sessions never
				// touch. With no name wanted AND nothing applied there is nothing to write and
				// nothing to restore, so the native work is skipped outright.
				string wantedEarly = "";
				try { wantedEarly = (ModConfig.UdonNameSpoof.Value ?? "").Trim(); } catch { }
				if (wantedEarly.Contains("\n") || wantedEarly.Contains("\r"))
				{
					var lines = wantedEarly.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
					wantedEarly = lines.Length > 0 ? lines[0].Trim() : "";
				}
				if (wantedEarly.Length > 32)
				{
					wantedEarly = wantedEarly.Substring(0, 32).Trim();
					try { ModConfig.UdonNameSpoof.Value = wantedEarly; } catch { }
				}

				// If no custom username is wanted: restore real name immediately if needed and go idle
				if (wantedEarly.Length == 0)
				{
					if (Applied.Length > 0 && RealName.Length > 0)
					{
						var localApi = PlayerRef.LocalApi();
						if (localApi != null && NativeGuard.Alive(localApi))
						{
							try { localApi.displayName = RealName; } catch { }
							VRChatArchiveModPlugin.Logger.LogInfo("[Spoof] restored to " + RealName);
						}
					}
					Applied = "";
					_lastLogged = "";
					Status = "custom username: off";
					return;
				}

				var api = PlayerRef.LocalApi();
				if (api == null || !NativeGuard.Alive(api)) return;

				// A NEW VRCPlayerApi MEANS A NEW WORLD. Comparing the native pointer is what notices
				// it: the field on the new object still holds the real name, so Applied has to be
				// cleared or the "already correct" test below would skip the re-write.
				IntPtr ptr;
				try { ptr = api.Pointer; } catch { return; }
				if (ptr != _lastApi)
				{
					_lastApi = ptr;
					if (Applied.Length > 0)
					{
						Applied = "";
						VRChatArchiveModPlugin.Logger.LogInfo("[Spoof] new local VRCPlayerApi (world change) — re-applying.");
					}
				}

				string current;
				if (!ReadName(ptr, out current, now)) return;

				string wanted = wantedEarly;

				// Learn the real name only from a value we did not write, or the restore is a no-op.
				if (RealName.Length == 0 && current.Length > 0
					&& !string.Equals(current, Applied, StringComparison.Ordinal))
				{
					RealName = current;
					VRChatArchiveModPlugin.Logger.LogInfo("[Spoof] real display name noted: " + RealName);
				}

				// STILL OURS: count it and leave.
				if (string.Equals(current, wanted, StringComparison.Ordinal))
				{
					Applied = wanted;
					_held++;
					StatLog(now, wanted);
					return;
				}

				// NOT OURS ANY MORE. Either we have never written it, or something put the old value
				// back — and which of the two it is only shows up in the ratio below.
				if (Applied.Length > 0) _reverted++;

				try { api.displayName = wanted; }
				catch (Exception e) { Status = "custom username: " + e.Message; return; }
				RootWritten(ptr);

				// READ IT BACK. The whole point: this line is the difference between believing and
				// knowing, and it costs one interop read twice a second.
				string after;
				if (!ReadName(ptr, out after, now)) after = "<unreadable>";
				bool ok = string.Equals(after, wanted, StringComparison.Ordinal);
				Applied = ok ? wanted : "";
				Status = ok ? ("custom username: " + wanted) : "custom username: the write did not stick";

				if (!string.Equals(wanted, _lastLogged, StringComparison.Ordinal))
				{
					_lastLogged = wanted;
					if (ok)
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[Spoof] udon name = \"" + wanted + "\" — VERIFIED by read-back (real: "
							+ (RealName.Length > 0 ? RealName : "?") + "). Worlds on this client read the new "
							+ "one; your nameplate still shows the real one.");
					else
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[Spoof] wrote \"" + wanted + "\" but read back \"" + after
							+ "\" — the write did not stick. Not retrying in a loop.");
				}
			}
			catch (Exception e) { Status = "spoof: " + e.Message; }
		}

		// WHY THIS DOES NOT USE api.displayName, WHICH IS THE OBVIOUS THING TO WRITE.
		//
		// Because it killed VRChat. From BepInEx/ErrorLog.log, 2026-09-08:
		//
		//     Fatal error. Internal CLR error. (0x80131506)
		//        at System.Buffer.__Memmove(Byte*, Byte*, UIntPtr)
		//        at System.String.Ctor(Char*, Int32, Int32)
		//        at Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(IntPtr)
		//        at VRC.SDKBase.VRCPlayerApi.get_displayName()
		//        at VRChatArchiveMod.Modules.SpoofModule.OnUpdate()
		//
		// The generated getter reads the field and hands the pointer straight to
		// Il2CppStringToManaged, which validates nothing: it takes the length out of the string
		// header and memmoves. When that pointer is stale the fault happens INSIDE memmove — not an
		// exception, not something the try/catch two lines up could ever see, but a CLR fatal that
		// ends the process. Which is why every catch in this file looked like it was covering the
		// read, and none of them was.
		//
		// AND THE GUARD ABOVE COULD NOT HAVE HELPED. NativeGuard.Alive(api) had already passed: the
		// VRCPlayerApi object itself was fine. What was rotten was the pointer INSIDE its field —
		// a string collected out from under us, or a field belonging to an object whose memory had
		// been recycled after a world change. Alive() validates the object it is given and says
		// nothing about what its fields point at, so the crash walked straight past it.
		//
		// Core/Il2CppStr does the read the careful way and simply refuses when the pointer will not
		// stand up. A refused read returns false and this pass does nothing, which is exactly right:
		// the next pass is 100 ms away.
		private static IntPtr _nameField = IntPtr.Zero;
		private static bool _nameFieldTried;
		private static int _lastRejected;

		// AND THE OTHER HALF OF THE CRASH: KEEPING THE STRING WE WROTE ALIVE.
		//
		// Reading defensively stops the MOD from dying on a dangling pointer. It does nothing for the
		// GAME, which reads this same field constantly — and a Udon script reading it is the entire
		// point of the feature, so a dangling pointer there is a crash inside VRChat's own code where
		// no guard of ours can reach.
		//
		// This module is the only one in the mod that WRITES an il2cpp string into a VRChat field.
		// api.displayName = wanted compiles to ManagedStringToIl2Cpp, which allocates a fresh string
		// on the il2cpp heap, and the only thing referring to it afterwards is that one field. Every
		// OTHER displayName in this process was allocated and rooted by VRChat itself, which is why
		// the crash trace names this module and not the six others that read the same property.
		//
		// So the written string gets a real il2cpp GC root: Il2CppObjectBase takes an
		// il2cpp_gchandle_new in its constructor, so simply HOLDING the wrapper is the root. One
		// live handle at a time, replaced whenever the name changes — the previous wrapper is dropped
		// and its finalizer releases the old handle.
		private static Il2CppSystem.String _rooted;

		private static void RootWritten(IntPtr apiPtr)
		{
			try
			{
				if (_nameField == IntPtr.Zero) return;
				IntPtr strPtr;
				if (!Il2CppStr.TryFieldPtr(apiPtr, _nameField, out strPtr)) return;
				if (strPtr == IntPtr.Zero || !NativeGuard.IsLiveObject(strPtr)) return;
				_rooted = new Il2CppSystem.String(strPtr);
			}
			catch { /* a root we could not take is a name that may drift back, never a crash */ }
		}

		private static float _nextRejectLog;

		private static bool ReadName(IntPtr apiPtr, out string name, float now = 0f)
		{
			name = "";
			if (!_nameFieldTried)
			{
				_nameFieldTried = true;
				_nameField = Il2CppStr.FindField(apiPtr, "displayName");
				if (_nameField == IntPtr.Zero)
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[Spoof] VRCPlayerApi has no field called displayName on this build — custom username "
						+ "cannot verify itself, so it stays off rather than writing blind.");
			}
			if (_nameField == IntPtr.Zero) { Armed = false; return false; }

			IntPtr strPtr;
			bool ok = Il2CppStr.TryFieldPtr(apiPtr, _nameField, out strPtr)
				&& Il2CppStr.TryRead(strPtr, out name);

			// Throttle reject warning to avoid console spamming and CPU spikes
			if (Il2CppStr.Rejected != _lastRejected)
			{
				_lastRejected = Il2CppStr.Rejected;
				if (now >= _nextRejectLog)
				{
					_nextRejectLog = now + 5f;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[Spoof] unsafe string read rejected (" + _lastRejected + " total across mod). Guard active.");
				}
			}
			return ok;
		}

		// THE ANSWER TO "IS IT HOLDING?", once every ten seconds and only while a name is set.
		//
		// held = passes that found our value still in place. reverted = passes that found the real
		// name back after we had written ours. A clean run reads "held 100, reverted 0" and the
		// feature is solid; anything else is the game writing over us, and the ratio says how often
		// — which is the difference between "it works" and "a world reading at the wrong moment sees
		// your real name", with no guessing in between.
		private static void StatLog(float now, string wanted)
		{
			if (now < _nextStatLog) { if (_nextStatLog == 0f) _nextStatLog = now + 10f; return; }
			_nextStatLog = now + 10f;
			int total = _held + _reverted;
			if (total == 0) return;
			string line = "[Spoof] \"" + wanted + "\" holding: " + _held + " ok, " + _reverted
				+ " reverted out of " + total + " checks in the last 10 s";
			if (_reverted == 0) VRChatArchiveModPlugin.Logger.LogInfo(line + " — stable.");
			else VRChatArchiveModPlugin.Logger.LogWarning(
				line + " — the game is putting the real name back; a world reading between two of our "
				+ "passes would see it. Re-asserted each time.");
			_held = 0; _reverted = 0;
		}

		// A world change rebuilds the local player; force the next pass to re-apply rather than wait
		// for the pointer comparison to notice.
		public override void OnSceneLoaded(int buildIndex)
		{
			Applied = ""; _lastApi = IntPtr.Zero; _nextCheck = 0f;
			_held = 0; _reverted = 0; _nextStatLog = 0f;
		}

		public override void OnShutdown()
		{
			try
			{
				if (!Armed || Applied.Length == 0 || RealName.Length == 0) return;
				var api = PlayerRef.LocalApi();
				if (api != null && NativeGuard.Alive(api)) api.displayName = RealName;
			}
			catch { }
		}
	}
}
