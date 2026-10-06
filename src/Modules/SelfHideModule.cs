using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// SELF HIDE — your own avatar is not drawn on YOUR screen.
	//
	// Not "do not load the bundle": refusing your own avatar's bundle makes VRChat fall back to the
	// error robot and complain every time, and what everyone else sees is unaffected either way (they
	// load your avatar themselves). So the avatar loads normally and its renderers are kept off.
	//
	// KEPT OFF EVERY FRAME, IN LATEUPDATE. A once-a-second pass with forceRenderingOff (v2) logged
	// "hiding 823 renderer(s)" and the avatar stayed visible: VRChat writes those flags itself on the
	// local avatar (first-person head hiding, mirror handling) and undid us between passes. Camera
	// callbacks would be the clean answer but ConvertDelegate crashes this build (Core/Il2CppDelegates),
	// so the next best is the last word before rendering: every LateUpdate, every tracked renderer gets
	// enabled = false AND forceRenderingOff = true, written only when a getter says it is needed.
	// Renderers are (re)collected once a second under the local avatar root and under any mirror clone
	// VRChat makes of it. OFF restores enabled to what it was and clears forceRenderingOff.
	public class SelfHideModule : IModule
	{
		public override string Name => "SelfHide";

		public static string Status = "";
		public static int Hidden => _hidden.Count;

		private sealed class Held { public Renderer R; public bool WasEnabled; public int Id; }

		/// <summary>Drops entry i from BOTH the ledger and the id set. Removing it from _hidden alone
		/// left its instance id in _ids — the "already taken" guard — so the renderer could never be
		/// re-adopted and never be restored: invisible for the rest of the session.</summary>
		private static void Forget(int i)
		{
			try { var h = _hidden[i]; if (h != null) _ids.Remove(h.Id); } catch { }
			_hidden.RemoveAt(i);
		}
		private static readonly List<Held> _hidden = new List<Held>();
		private static readonly HashSet<int> _ids = new HashSet<int>();
		private static int _rootId;
		private static string _rootName = "";
		private static float _nextScan, _nextLog;
		private static bool _on;

		public override void OnUpdate()
		{
			try
			{
				bool on = false;
				try { on = ModConfig.SelfHide.Value; } catch { }
				if (!on)
				{
					if (_on || _hidden.Count > 0) { RestoreAll(); Status = "self hide off"; VRChatArchiveModPlugin.Logger.LogInfo("[SelfHide] off — avatar drawn again."); }
					_on = false;
					return;
				}
				if (!_on) { _on = true; _nextScan = 0f; }
				float now = VaClock.Now;
				if (now < _nextScan) return;
				_nextScan = now + 1f;
				Scan(now);
			}
			catch (Exception e) { Status = "self hide: " + e.Message; }
		}

		// The last word before the frame renders: whatever VRChat or the avatar's animator did this
		// frame, the renderers we hold are off when the cameras look.
		public override void OnLateUpdate()
		{
			if (!_on || _hidden.Count == 0) return;
			for (int i = _hidden.Count - 1; i >= 0; i--)
			{
				var h = _hidden[i];
				try
				{
					var r = h.R;
					if (r == null || !NativeGuard.Alive(r)) { Forget(i); continue; }
					if (r.enabled) r.enabled = false;
					if (!r.forceRenderingOff) r.forceRenderingOff = true;
				}
				catch
				{
					// A THROW IS NOT A DEATH (2026-09-13). Dropping the entry on a transient write
					// failure left a LIVE renderer at enabled = false with forceRenderingOff = true,
					// outside _hidden — the only ledger RestoreAll() walks — and with its id still in
					// _ids, so the scan could never re-adopt it either: a permanently invisible piece
					// of your own avatar that switching self-hide off could not bring back. Put it
					// back visible if it really is alive, then forget it properly (id included).
					try { if (h.R != null && NativeGuard.Alive(h.R)) { h.R.forceRenderingOff = false; h.R.enabled = h.WasEnabled; } } catch { }
					Forget(i);
				}
			}
		}

		// The local avatar root: the object under the local player that carries the VRCAvatarDescriptor,
		// or failing that the one named like VRChat names avatar instances ("prefab-id-v1_avtr_…" / "Avatar").
		private static GameObject FindRoot(Transform local)
		{
			try
			{
				var desc = local.GetComponentInChildren<VRCAvatarDescriptor>(true);
				if (desc != null && NativeGuard.Alive(desc)) return desc.gameObject;
			}
			catch { }
			try
			{
				var all = local.GetComponentsInChildren<Transform>(true);
				if (all != null)
					for (int i = 0; i < all.Length; i++)
					{
						var t = all[i];
						if (t == null) continue;
						string n; try { n = t.name ?? ""; } catch { continue; }
						if (n.StartsWith("prefab-id-v1_avtr", StringComparison.OrdinalIgnoreCase) || n.IndexOf("avtr_", StringComparison.OrdinalIgnoreCase) >= 0 || n == "Avatar")
							return t.gameObject;
					}
			}
			catch { }
			return null;
		}

		private static void Scan(float now)
		{
			// RESTORE, DON'T JUST DROP (2026-09-13). Both of these are TRANSIENT states — the local
			// player or the avatar root being momentarily unresolvable around a respawn or an avatar
			// change — and Drop() is `_hidden.Clear(); _ids.Clear();` with no restore at all. Every
			// renderer we had already written to enabled = false / forceRenderingOff = true left the
			// ledger still hidden, so a later OFF ran RestoreAll() over an empty list and parts of
			// the avatar stayed invisible for good. RestoreAll() puts them back first and then calls
			// Drop() itself, which is the same reset with the undo actually performed.
			var lt = PlayerRef.LocalTransform();
			if (lt == null || !NativeGuard.Alive(lt)) { RestoreAll(); Status = "self hide: no local player yet"; return; }
			var root = FindRoot(lt);
			if (root == null)
			{
				RestoreAll();
				Status = "self hide: waiting for your avatar";
				if (now >= _nextLog)
				{
					_nextLog = now + 10f;
					var sb = new System.Text.StringBuilder();
					try { int k = 0; for (int i = 0; i < lt.childCount && k < 12; i++, k++) { if (k > 0) sb.Append(", "); sb.Append(lt.GetChild(i).name); } } catch { }
					VRChatArchiveModPlugin.Logger.LogWarning("[SelfHide] no avatar root under '" + lt.name + "' (children: " + sb + ").");
				}
				return;
			}
			int id; try { id = root.GetInstanceID(); } catch { Drop(); return; }
			if (id != _rootId)
			{
				RestoreAll(); _rootId = id;
				try { _rootName = root.name; } catch { _rootName = "?"; }
			}

			int added = 0;
			added += Take(root);
			// VRChat's mirror copy of the local avatar lives beside it under the player root
			try
			{
				var all = lt.GetComponentsInChildren<Transform>(true);
				if (all != null)
					for (int i = 0; i < all.Length; i++)
					{
						var t = all[i];
						if (t == null) continue;
						string n; try { n = t.name ?? ""; } catch { continue; }
						if (n.IndexOf("MirrorClone", StringComparison.OrdinalIgnoreCase) >= 0) added += Take(t.gameObject);
					}
			}
			catch { }
			Status = "self hide on — " + _hidden.Count + " renderer(s) of your avatar hidden";
			if (added > 0) VRChatArchiveModPlugin.Logger.LogInfo("[SelfHide] hiding " + _hidden.Count + " renderer(s) under '" + _rootName + "' (+" + added + "), kept off every frame.");
		}

		private static int Take(GameObject root)
		{
			Renderer[] rs = null;
			try { rs = root.GetComponentsInChildren<Renderer>(true); } catch { }
			if (rs == null) return 0;
			int added = 0;
			for (int i = 0; i < rs.Length; i++)
			{
				var r = rs[i];
				try
				{
					if (r == null || !NativeGuard.Alive(r)) continue;
					int rid = r.GetInstanceID();
					if (!_ids.Add(rid)) continue;
					// Id recorded while the renderer is provably alive: Forget() needs it to clear
					// _ids, and reading GetInstanceID() off a destroyed renderer would be an
					// uncatchable access violation.
					var h = new Held { R = r, WasEnabled = r.enabled, Id = rid };
					r.enabled = false;
					r.forceRenderingOff = true;
					_hidden.Add(h); added++;
				}
				catch { }
			}
			return added;
		}

		private static void Drop() { _hidden.Clear(); _ids.Clear(); _rootId = 0; _rootName = ""; }

		private static void RestoreAll()
		{
			for (int i = 0; i < _hidden.Count; i++)
			{
				var h = _hidden[i];
				try { if (h.R != null && NativeGuard.Alive(h.R)) { h.R.forceRenderingOff = false; h.R.enabled = h.WasEnabled; } } catch { }
			}
			Drop();
		}

		public override void OnSceneLoaded(int buildIndex) => Drop();   // the avatar left with the world
		public override void OnShutdown() => RestoreAll();
	}
}
