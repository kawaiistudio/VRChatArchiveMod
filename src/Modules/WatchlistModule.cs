using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// Watchlist: flags specific VRChat user ids. When a watched user is in your instance they
	// get an animated super-RGB ESP box (rainbow outline + soft glow) so they stand out, and
	// the moment one JOINS a notification banner pops on screen for a few seconds.
	//
	// Purely a local visualization/alert built on the same position data your client already
	// receives (VRChatArchiveMod.Core.VaPlayers.All()) and the same APIUser access as the ESP module — it
	// never targets, follows, or acts on anyone, and touches nothing networked.
	public class WatchlistModule : IModule
	{
		public override string Name => "Watchlist";

		private HashSet<string> _watch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private string _watchRaw;
		private readonly HashSet<string> _presentWatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private bool _primed;
		private int _settle;
		private const int SettlePolls = 10;   // ~3s at 20-frame cadence before announcing

		// A new world means every "who was already here" judgement is stale: without this the
		// settle window never ran again and each world change announced everyone as a join.
		// ONE SHARED, LOCAL SOUND. Its own AudioSource rather than the soundboard's: that one is driven
		// by remote requests and carries the soundboard's volume, while this is a private alert nobody
		// else hears. spatialBlend 0 so it plays flat in both ears instead of somewhere in the world.
		private static AudioSource _alertSrc;
		private static AudioClip _alertClip;
		private static bool _alertTried;
		private static float _alertNext;

		private static void Alert()
		{
			// Watchlist join sound disabled
			return;
		}

		public override void OnSceneLoaded(int buildIndex)
		{
			_settle = 0;
			_primed = false;
			_memberPrimed = false;
			_presentWatched.Clear();
			_presentMembers.Clear();
		}

		// active on-screen notification
		private string _notifyName;
		private float _notifyUntil;

		// VRChat Archive members present in the instance, so we can announce arrivals.
		private readonly HashSet<string> _presentMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private bool _memberPrimed;
		private static string _memberName;
		private static float _memberUntil;
		private static float _memberSince;
		private Texture2D _memberGrad;
		private Texture2D _rgbTex;
		private Color32[] _rgbBuf;

		// Fire the member-join banner on demand (menu "Test" button), no real member needed.
		public static void TestMemberNotification(string name)
		{
			_memberName = string.IsNullOrEmpty(name) ? "Test Member" : name;
			_memberSince = VaClock.Now;
			_memberUntil = _memberSince + 7f;
		}

		private int _frame;

		// reflection (mirrors EspModule)
		private Type _playerType;
		private PropertyInfo _mApiUser;
		private MethodInfo _tryCastPlayer;
		private bool _resolved;

		private GUIStyle _banner, _tag, _mBanner, _mSub;

		public override void OnInitialize()
		{
			RebuildWatch();
			VRChatArchiveModPlugin.Logger.LogInfo($"[Watchlist] armed — {_watch.Count} watched user(s).");
		}

		public override void OnUpdate()
		{
			try
			{
				if (!ModConfig.WatchlistEnabled.Value) return;

				if (_watchRaw != ModConfig.WatchlistUserIds.Value) RebuildWatch();
				if (_watch.Count == 0) return;

				if (++_frame < 20) return;
				_frame = 0;
				PollPresence();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[Watchlist] update threw: {e}"); }
		}

		private void RebuildWatch()
		{
			_watchRaw = ModConfig.WatchlistUserIds.Value ?? "";
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string s in _watchRaw.Split(','))
			{
				string id = s.Trim();
				if (id.StartsWith("usr_", StringComparison.OrdinalIgnoreCase)) set.Add(id);
			}
			_watch = set;
			_primed = false;
			_presentWatched.Clear();
		}

		// Detect watched users present, and fire a notification on a fresh join.
		private void PollPresence()
		{
			ResolveReflection();
			string localUid = VaTagsModule.LocalUserId();
			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return;

			var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var nameByUid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int count;
			try { count = players.Count; } catch { return; }
			for (int i = 0; i < count; i++)
			{
				try
				{
					VRCPlayerApi api = players[i];
					if (api == null || api.isLocal) continue;
					string uid = UidOf(api);
					if (uid == null || !_watch.Contains(uid)) continue;
					if (SameUid(uid, localUid)) continue;                 // never yourself
					now.Add(uid);
					nameByUid[uid] = SafeName(api);
				}
				catch { }
			}

			// Settle first, so users already in the instance when you arrive are recorded
			// as present rather than falsely announced as fresh joins.
			bool settled = _settle >= SettlePolls;
			if (settled && _primed)
			{
				foreach (string uid in now)
					if (!_presentWatched.Contains(uid))
					{
						_notifyName = nameByUid.TryGetValue(uid, out string n) ? n : uid;
						_notifyUntil = VaClock.Now + 6f;
						VRChatArchiveModPlugin.Logger.LogInfo($"[Watchlist] watched user joined: {_notifyName} ({uid}).");
						// The banner is easy to miss when you are not looking at that corner of the
						// screen — which is most of the time, and the whole point of watching someone.
						Alert();
					}
			}
			if (settled) _primed = true; else _settle++;
			_presentWatched.Clear();
			foreach (string uid in now) _presentWatched.Add(uid);

			PollMembers(localUid, settled);
		}

		// Announce VRChat Archive members arriving. A watched user who is also a member gets
		// the watch banner only, so the two banners never stack for the same person.
		private void PollMembers(string localUid, bool settled)
		{
			var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			List<string> newcomers = null;
			foreach (var e in VaTagsModule.Roster)
			{
				if (e.IsLocal || string.IsNullOrEmpty(e.UserId)) continue;
				if (SameUid(e.UserId, localUid)) continue;                // never yourself
				if (_watch.Contains(e.UserId)) continue;                  // watch banner owns this one
				if (!VaTagsModule.IsMember(e.UserId)) continue;
				now.Add(e.UserId);
				if (settled && _memberPrimed && !_presentMembers.Contains(e.UserId))
				{
					(newcomers ??= new List<string>()).Add(e.Name);
					VRChatArchiveModPlugin.Logger.LogInfo($"[Watchlist] VRChat Archive member joined: {e.Name}.");
				}
			}
			// Several members can arrive in the same poll; naming only the last one silently
			// swallowed the others and marked them present forever.
			if (newcomers != null)
			{
				_memberName = newcomers.Count == 1
					? newcomers[0]
					: newcomers[0] + $" +{newcomers.Count - 1} more";
				_memberSince = VaClock.Now;
				_memberUntil = _memberSince + 7f;
			}
			if (settled) _memberPrimed = true;
			_presentMembers.Clear();
			foreach (string uid in now) _presentMembers.Add(uid);
		}

		private static bool SameUid(string a, string b)
			=> !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

		public override void OnGui()
		{
			try
			{
				if (Event.current.type != EventType.Repaint) return;
				EnsureStyles();

				// Watch boxes + watched-user banner depend on the watchlist having entries.
				if (ModConfig.WatchlistEnabled.Value && _watch.Count > 0)
				{
					DrawBoxes();
					DrawNotification();
				}
				// The VRChat Archive member-join banner is INDEPENDENT of the watchlist, so it
				// always draws (and the Test button can fire it even with an empty watchlist).
				DrawMemberNotification();
				GUI.color = Color.white;
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogError($"[Watchlist] draw threw: {e}"); }
		}

		private void DrawBoxes()
		{
			if (_watch.Count == 0) return;      // nothing watched -> resolve nobody
			var cam = Camera.main;
			if (cam == null) return;
			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return;

			// rainbow colour cycles over time (super-RGB)
			float t = VaClock.Now;
			Color rgb = Color.HSVToRGB(Mathf.Repeat(t * 0.4f, 1f), 0.9f, 1f);
			Color rgb2 = Color.HSVToRGB(Mathf.Repeat(t * 0.4f + 0.5f, 1f), 0.9f, 1f);

			int count;
			try { count = players.Count; } catch { return; }
			for (int i = 0; i < count; i++)
			{
				try
				{
					VRCPlayerApi api = players[i];
					if (api == null || api.isLocal) continue;
					string uid = UidOf(api);
					if (uid == null || !_watch.Contains(uid)) continue;

					Vector3 feet = api.GetPosition();
					Vector3 head;
					try { head = api.GetBonePosition(HumanBodyBones.Head); } catch { head = Vector3.zero; }
					if (head == Vector3.zero) head = feet + Vector3.up * 1.7f;

					Vector3 fs = cam.WorldToScreenPoint(feet);
					Vector3 hs = cam.WorldToScreenPoint(head);
					// EITHER point behind the camera mirrors the projection into a full-screen
					// box; the old `&&` only skipped when BOTH were behind.
					if (fs.z <= 0f || hs.z <= 0f) continue;

					float feetY = Screen.height - fs.y;
					float headY = Screen.height - hs.y;
					float top = Mathf.Min(feetY, headY);
					float h = Mathf.Max(20f, Mathf.Abs(feetY - headY));
					float w = h * 0.5f;
					float cx = (fs.x + hs.x) * 0.5f;
					var box = new Rect(cx - w * 0.5f, top, w, h);

					// glow (outer, translucent second colour) + crisp rainbow outline
					DrawBoxOutline(new Rect(box.x - 3f, box.y - 3f, box.width + 6f, box.height + 6f), new Color(rgb2.r, rgb2.g, rgb2.b, 0.35f), 3f);
					DrawBoxOutline(box, rgb, 2.5f);

					_tag.normal.textColor = rgb;
					GUI.Label(new Rect(box.x - 50f, box.y - 20f, box.width + 100f, 18f), "★ " + SafeName(api), _tag);
				}
				catch { }
			}
		}

		// Member arrival banner: a clean pink→violet gradient PILL with a soft glow, a glassy top
		// sheen and a gentle fade in/out. Deliberately calm and premium, not a rainbow box.
		private void DrawMemberNotification()
		{
			if (VaClock.Now > _memberUntil || string.IsNullOrEmpty(_memberName)) return;
			float now = VaClock.Now;

			// fade in over the first 0.3s, out over the last 0.7s, with a soft ease-out entry pop.
			float fin = Mathf.Clamp01((now - _memberSince) / 0.3f);
			float fout = Mathf.Clamp01((_memberUntil - now) / 0.7f);
			float alpha = Mathf.Min(fin, fout);
			float pop = 1f - Mathf.Pow(1f - fin, 3f);

			float w = 520f, h = 66f;
			float sc = Mathf.Lerp(0.93f, 1f, pop);
			float pw = w * sc, ph = h * sc;
			float cx = Screen.width * 0.5f, cy = Screen.height * 0.075f + h * 0.5f;
			var r = new Rect(cx - pw * 0.5f, cy - ph * 0.5f, pw, ph);
			float rad = ph * 0.5f;                       // radius = half-height -> perfect pill/oval ends

			bool rgb = ModConfig.MemberNotifyRgb.Value;

			// soft halo, breathing gently — violet for the clean pill, hue-cycling for RGB
			float breathe = 0.5f + 0.5f * Mathf.Sin(now * 2.6f);
			Color glowC = rgb ? Color.HSVToRGB(Mathf.Repeat(now * 0.15f, 1f), 0.7f, 1f) : new Color(0.71f, 0.42f, 1f);
			GuiKit.SoftGlow(r, new Color(glowC.r, glowC.g, glowC.b, (0.45f + 0.25f * breathe) * alpha), rad, 0.55f, 7, 3f);

			// anti-aliased rounded-corner GRADIENT fill (one draw, borderRadius = rad).
			// Clean = fixed pink→violet; RGB = an animated rainbow sweep (test / flashier look).
			var radV = new Vector4(rad, rad, rad, rad);
			Texture2D grad = rgb ? RainbowGradient(now) : MemberGradient();
			GUI.DrawTexture(r, grad, ScaleMode.StretchToFill, true, 0f, new Color(1f, 1f, 1f, alpha), Vector4.zero, radV);

			// glassy top sheen
			var sheen = new Rect(r.x + rad * 0.5f, r.y + ph * 0.11f, pw - rad, ph * 0.32f);
			GuiKit.RoundedFill(sheen, new Color(1f, 1f, 1f, 0.13f * alpha), sheen.height * 0.5f);

			// crisp thin inner ring
			GUI.DrawTexture(r, GuiKit.Pixel, ScaleMode.StretchToFill, true, 0f, new Color(1f, 1f, 1f, 0.24f * alpha), new Vector4(1.4f, 1.4f, 1.4f, 1.4f), radV);

			// text — white with a soft shadow so it reads cleanly over the gradient
			ShadowLabel(new Rect(r.x, r.y + 8f, pw, 24f), "◆   VRCHAT ARCHIVE MEMBER", _mBanner, new Color(1f, 1f, 1f, alpha), alpha);
			ShadowLabel(new Rect(r.x, r.y + ph - 29f, pw, 22f), Trunc(_memberName, 30) + "  joined your instance", _mSub, new Color(1f, 0.92f, 0.99f, alpha), alpha);
			GUI.color = Color.white;
		}

		// One-time horizontal pink→violet gradient texture, drawn with rounded corners above.
		private Texture2D MemberGradient()
		{
			if (_memberGrad != null) return _memberGrad;
			const int N = 256;
			var tex = new Texture2D(N, 1, TextureFormat.RGBA32, false)
			{ wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
			Color a = new Color(1f, 0.416f, 0.835f);      // #FF6AD5 member pink
			Color b = new Color(0.506f, 0.263f, 0.902f);  // #8143E6 member violet
			for (int x = 0; x < N; x++) tex.SetPixel(x, 0, Color.Lerp(a, b, (float)x / (N - 1)));
			tex.Apply(false, false);
			_memberGrad = tex;
			return tex;
		}

		// Animated rainbow gradient rebuilt each frame (RGB test mode). 128px is cheap to upload.
		private Texture2D RainbowGradient(float t)
		{
			const int N = 128;
			if (_rgbTex == null)
			{
				_rgbTex = new Texture2D(N, 1, TextureFormat.RGBA32, false)
				{ wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
				_rgbBuf = new Color32[N];
			}
			for (int x = 0; x < N; x++)
			{
				float h = Mathf.Repeat((float)x / N * 1.3f + t * 0.14f, 1f);
				Color c = Color.HSVToRGB(h, 0.85f, 1f);
				_rgbBuf[x] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), 255);
			}
			_rgbTex.SetPixels32(_rgbBuf);
			_rgbTex.Apply(false, false);
			return _rgbTex;
		}

		private static void ShadowLabel(Rect r, string text, GUIStyle st, Color col, float alpha)
		{
			st.normal.textColor = new Color(0f, 0f, 0f, 0.5f * alpha);
			GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, st);
			st.normal.textColor = col;
			GUI.Label(r, text, st);
		}

		private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

		private void DrawNotification()
		{
			if (VaClock.Now > _notifyUntil || string.IsNullOrEmpty(_notifyName)) return;
			float t = VaClock.Now;
			Color rgb = Color.HSVToRGB(Mathf.Repeat(t * 0.6f, 1f), 0.9f, 1f);

			float w = 460f, h = 54f;
			var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.14f, w, h);
			GuiKit.Fill(r, new Color(0.03f, 0.03f, 0.05f, 0.92f));
			DrawBoxOutline(r, rgb, 2.5f);
			_banner.normal.textColor = rgb;
			GUI.Label(r, "★  WATCHED USER JOINED  ★\n" + _notifyName, _banner);
		}

		// ---- APIUser / uid (mirrors EspModule) ----

		private void ResolveReflection()
		{
			if (_resolved) return;
			_resolved = true;
			try
			{
				_playerType = Assembly.Load("Assembly-CSharp").GetType("VRC.Player");
				if (_playerType != null)
				{
					_mApiUser = _playerType.GetProperties().FirstOrDefault(p => p.PropertyType == typeof(APIUser));
					_tryCastPlayer = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
						.GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)
						?.MakeGenericMethod(_playerType);
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning($"[Watchlist] reflection unavailable: {e.Message}"); }
		}

		// Goes through the shared cached resolver: this used to rebuild an Il2CppType, search the
		// hierarchy up to three times and invoke two reflection calls FOR EVERY PLAYER ON EVERY
		// REPAINT — and IMGUI repaints more than once a frame.
		private string UidOf(VRCPlayerApi api)
		{
			try { var u = Core.ApiUsers.Get(api); return u != null ? u.id : null; }
			catch { return null; }
		}

		private static void DrawBoxOutline(Rect r, Color c, float t)
		{
			GUI.color = c;
			GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), GuiKit.Pixel);
			GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), GuiKit.Pixel);
			GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), GuiKit.Pixel);
			GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), GuiKit.Pixel);
			GUI.color = Color.white;
		}

		private void EnsureStyles()
		{
			if (_banner != null) return;
			_banner = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.MiddleCenter };
			_tag = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, richText = true, alignment = TextAnchor.LowerCenter };
			_mBanner = new GUIStyle { fontSize = 17, fontStyle = FontStyle.Bold, richText = false, alignment = TextAnchor.MiddleCenter };
			_mSub    = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, richText = false, alignment = TextAnchor.MiddleCenter };
		}

		private static string SafeName(VRCPlayerApi api)
		{
			try { return api.displayName ?? "?"; } catch { return "?"; }
		}
	}
}
