using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// NSFW FILTER — hides, on YOUR screen, the parts of OTHER players' avatars whose object name says
	// what they are. Avatar authors name their meshes; a renderer whose GameObject (or a parent, up to
	// three levels) is called after a keyword from the list is switched off. Nothing is destroyed and
	// nothing is sent: the toggle going OFF (or the master anti-crash going OFF) turns them back on.
	// Re-asserted every 2 s because an avatar's own toggles can re-enable a renderer.
	//
	// Your own avatar is never touched (that is what SELF HIDE is for).
	public class NsfwFilterModule : IModule
	{
		public override string Name => "NsfwFilter";

		public static int Hidden => _hidden.Count;
		public static string Status = "";

		private sealed class Held { public Renderer R; public bool Was; public int Id; }
		private static readonly List<Held> _hidden = new List<Held>();
		private static readonly HashSet<int> _ids = new HashSet<int>();
		private static readonly HashSet<int> _scannedAvatars = new HashSet<int>();
		private static string[] _keywords = Array.Empty<string>();
		private static string _keywordsRaw;
		private static float _nextPass;
		private static bool _wasOn;

		private static void RefreshKeywords()
		{
			string raw = "";
			try { raw = ModConfig.NsfwKeywords.Value ?? ""; } catch { }
			if (raw == _keywordsRaw) return;
			_keywordsRaw = raw;
			var list = new List<string>();
			foreach (var part in raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string k = part.Trim().ToLowerInvariant();
				if (k.Length >= 3) list.Add(k);
			}
			_keywords = list.ToArray();
			_scannedAvatars.Clear();   // new words: look at everyone again
		}

		public override void OnUpdate()
		{
			try
			{
				bool on = false;
				try { on = ModConfig.NsfwFilter.Value && ModConfig.AntiCrashEnabled.Value; } catch { }
				if (!on)
				{
					if (_wasOn || _hidden.Count > 0) { RestoreAll(); Status = "nsfw filter off"; }
					_wasOn = false;
					return;
				}
				if (!_wasOn) { _wasOn = true; _nextPass = 0f; _scannedAvatars.Clear(); }
				float now = VaClock.Now;
				if (now < _nextPass) return;
				_nextPass = now + 2f;
				RefreshKeywords();
				Pass();
			}
			catch (Exception e) { Status = "nsfw filter: " + e.Message; }
		}

		private static void Pass()
		{
			// re-assert what we hold, drop the dead
			for (int i = _hidden.Count - 1; i >= 0; i--)
			{
				var h = _hidden[i];
				try
				{
					if (h.R == null || !NativeGuard.Alive(h.R)) { Forget(i); continue; }
					if (h.R.enabled) h.R.enabled = false;
				}
				catch
				{
					// A THROW IS NOT A DEATH (2026-09-13). This used to drop the entry outright, so a
					// renderer that merely threw once on the `enabled = false` write left _hidden — the
					// only restore ledger — while still forced off, and its id stayed in _ids so the
					// discovery pass could never re-adopt it either: a permanently invisible piece of
					// somebody's avatar that RestoreAll() can no longer reach. Put it back visible if
					// it is in fact alive, then forget it properly (id included).
					try { if (h.R != null && NativeGuard.Alive(h.R)) h.R.enabled = h.Was; } catch { }
					Forget(i);
				}
			}
			if (_keywords.Length == 0) return;

			// every REMOTE player's avatar, once per avatar instance (a new avatar = a new root id)
			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return;
			int newlyHidden = 0;
			for (int i = 0; i < players.Count; i++)
			{
				try
				{
					var api = players[i];
					if (api == null || api.isLocal) continue;
					var go = api.gameObject; if (go == null) continue;
					var desc = go.GetComponentInChildren<VRCAvatarDescriptor>(true);
					if (desc == null || !NativeGuard.Alive(desc)) continue;
					var root = desc.gameObject;
					int rid = root.GetInstanceID();
					if (!_scannedAvatars.Add(rid)) continue;
					newlyHidden += Scan(root);
				}
				catch { }
			}
			if (newlyHidden > 0) VRChatArchiveModPlugin.Logger.LogInfo("[NsfwFilter] hid " + newlyHidden + " renderer(s); " + _hidden.Count + " hidden in total.");
			Status = "nsfw filter on — " + _hidden.Count + " renderer(s) hidden";
			FeatureHealth.Ok("AntiCrash/NsfwFilter", _hidden.Count == 0 ? "on — nothing matched so far" : "on — " + _hidden.Count + " renderer(s) hidden");
		}

		private static int Scan(GameObject root)
		{
			Renderer[] rs = null;
			try { rs = root.GetComponentsInChildren<Renderer>(true); } catch { }
			if (rs == null) return 0;
			int n = 0;
			for (int i = 0; i < rs.Length; i++)
			{
				var r = rs[i];
				try
				{
					if (r == null || !NativeGuard.Alive(r)) continue;
					if (!Matches(r.transform)) continue;
					int id = r.GetInstanceID();
					if (!_ids.Add(id)) continue;
					// Id recorded here, while the object is provably alive: Forget() needs it to clear
					// _ids, and reading GetInstanceID() off an already-destroyed renderer would be an
					// uncatchable access violation.
					_hidden.Add(new Held { R = r, Was = r.enabled, Id = id });
					r.enabled = false; n++;
				}
				catch { }
			}
			return n;
		}

		// The renderer's object or up to three parents named after a keyword (case-insensitive).
		private static bool Matches(Transform t)
		{
			int up = 0;
			for (Transform p = t; p != null && up < 4; p = p.parent, up++)
			{
				string n; try { n = (p.name ?? "").ToLowerInvariant(); } catch { break; }
				if (n.Length == 0) continue;
				for (int k = 0; k < _keywords.Length; k++) if (n.Contains(_keywords[k])) return true;
			}
			return false;
		}

		/// <summary>Drops entry i from BOTH the ledger and the id set. Removing it from _hidden alone
		/// left the renderer's instance id in _ids, which is the "already taken" guard — so the
		/// renderer could never be re-adopted and never be restored: invisible forever.</summary>
		private static void Forget(int i)
		{
			try { var h = _hidden[i]; if (h != null) _ids.Remove(h.Id); } catch { }
			_hidden.RemoveAt(i);
		}

		private static void RestoreAll()
		{
			for (int i = 0; i < _hidden.Count; i++)
			{
				var h = _hidden[i];
				try { if (h.R != null && NativeGuard.Alive(h.R)) h.R.enabled = h.Was; } catch { }
			}
			_hidden.Clear(); _ids.Clear(); _scannedAvatars.Clear();
			FeatureHealth.Idle("AntiCrash/NsfwFilter", "off");
		}

		public override void OnSceneLoaded(int buildIndex) { _hidden.Clear(); _ids.Clear(); _scannedAvatars.Clear(); }
		public override void OnShutdown() => RestoreAll();
	}
}
