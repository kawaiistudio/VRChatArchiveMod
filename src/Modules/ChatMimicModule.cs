using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MIMIC CHATBOX — repeat what a chosen player types, into your own chatbox.
	//
	// WHY IT CANNOT BE DONE OVER OSC, which is where the owner started. VRChat's OSC is a LOCAL
	// loopback: /chatbox/input sends YOUR text and there is no endpoint that receives anybody else's.
	// The text of a remote player exists only inside the game process — which is exactly why this
	// half lives in the mod, and the sending half stays in the desktop client where OscChatbox
	// already is.
	//
	// WHERE THE TEXT IS. A player's chat bubble is a ChatBubbleDisplay (: BaseChatBubbleDisplay) and
	// the base carries _chatText, a TextMeshProUGUI. Verified in libs/interop rather than assumed: the
	// class also holds _typingIndicator, _canvasGroup, _heightFitter, _chatBubblePositioner and
	// field_Protected_VRCPlayer_0.
	//
	// AND IT IS NOT UNDER THE PLAYER'S GAMEOBJECT — this comment said it was, for two versions, and
	// that single wrong sentence is the whole reason the feature shipped in v302 and never captured a
	// line. Each player owns a NameplateContainer GameObject exposed as a FIELD on VRCPlayer, and the
	// container is parented under the global NameplateManager, not under the player. See ResolveBubble.
	//
	// TWO RULES TAKEN FROM THAT REVIEW, AND FROM TODAY'S CRASH:
	//
	//  1. RESOLVE BY COMPONENT TYPE, NEVER BY MEMBER NAME. Every type here carries [ObfuscatedName],
	//     and the readable names are an artifact of how the interop assemblies were generated — the
	//     sibling member _heightFitter is typed ChatBubbleRectHeightFitter in libs/interop.bak-aug18
	//     and MonoBehaviourPublicSiInSiRe_rSi_hReTe_tUnique in the current one. So the bubble is
	//     found with GetComponentInChildren on the resolved TYPE, which drops two field reads and
	//     survives the rename. The one string that remains is the type name for the initial lookup:
	//     it is resolved once, logged once, and fails to null rather than to the wrong member.
	//
	//  2. IT REFUSES TO ARM WITHOUT FieldOffsetFix.Verified. Reading _chatText compiles to
	//     *(IntPtr*)(obj + il2cpp_field_get_offset(...)), and Il2CppInterop reads that offset from
	//     the slot that holds the metadata token on this build (Core/FieldOffsetFix). Unrepaired, the
	//     read lands on unmapped memory and NativeGuard cannot help, because the bad dereference
	//     happens inside the getter before anything is returned. This is the same gate
	//     Core/Il2CppDelegates uses, and the lesson of the custom-username crash a few hours ago.
	//
	// READ ONLY. Nothing is written to anyone's bubble and nothing is sent to the instance: the text
	// is handed to the desktop client, which puts it in YOUR chatbox over your own OSC.
	public class ChatMimicModule : IModule
	{
		public override string Name => "ChatMimic";

		public static string Status = "";
		public static string TargetUid { get; private set; } = "";
		public static string TargetName { get; private set; } = "";
		public static bool Active => TargetUid.Length > 0;

		/// <summary>False when FieldOffsetFix could not verify the offset slot: the feature is off for
		/// the session rather than reading a wild address.</summary>
		public static bool Armed { get; private set; }

		// A SMALL QUEUE, NOT A SINGLE SLOT. The client polls roughly once a second and somebody can
		// type two messages inside that window; a one-value latch would silently drop the first.
		// Bounded so a stuck client cannot grow it without limit.
		private const int MaxQueued = 8;
		private static readonly Queue<string> _pending = new Queue<string>();
		private static readonly object Gate = new object();

		private static bool _armChecked;
		private static float _nextPoll;
		private static string _lastSeen = "";
		private static Type _bubbleType;
		private static Il2CppSystem.Type _bubbleIl2;
		private static bool _typeLogged;
		private static Component _bubble;
		private static int _bubbleId;

		/// <summary>Text the mod has seen and the client has not taken yet. Drained by ModControl.</summary>
		public static List<string> Drain()
		{
			lock (Gate)
			{
				if (_pending.Count == 0) return null;
				var outp = new List<string>(_pending);
				_pending.Clear();
				return outp;
			}
		}

		public static void Toggle(VaTagsModule.PlayerEntry e)
		{
			if (e == null) { Status = "no such player"; return; }
			if (Active && string.Equals(e.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) { Stop("stopped"); return; }
			Start(e);
		}

		public static void Start(VaTagsModule.PlayerEntry e)
		{
			if (e == null || e.IsLocal) { Status = "pick somebody else"; return; }
			TargetUid = e.UserId ?? "";
			TargetName = e.Name ?? "";
			// A new target means a new search: clear the backoff and the once-only warning, or picking
			// somebody else would wait a second for nothing and stay quiet about a second failure.
			_lastSeen = ""; _bubble = null; _bubbleId = 0; _nextPoll = 0f; _nextSearch = 0f; _noneLogged = false; _viaContainerLogged = false;
			lock (Gate) _pending.Clear();
			Status = "mimicking " + TargetName + "'s chatbox";
			VRChatArchiveModPlugin.Logger.LogInfo("[ChatMimic] watching " + TargetName + " (" + TargetUid + ")");
		}

		public static void Stop(string why)
		{
			if (!Active) return;
			VRChatArchiveModPlugin.Logger.LogInfo("[ChatMimic] " + why + " (was " + TargetName + ")");
			TargetUid = ""; TargetName = "";
			_lastSeen = ""; _bubble = null; _bubbleId = 0;
			lock (Gate) _pending.Clear();
			Status = why;
		}

		public override void OnUpdate()
		{
			try
			{
				if (!_armChecked)
				{
					_armChecked = true;
					try { Armed = FieldOffsetFix.Verified; } catch { Armed = false; }
					if (!Armed)
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[ChatMimic] NOT ARMED — FieldOffsetFix could not verify the offset slot, so reading the "
							+ "chat bubble's text field would land outside the object. Chatbox mimic is off this session.");
				}
				if (!Armed || !Active) return;

				float now = VaClock.Now;
				if (now < _nextPoll) return;
				// Four times a second: fast enough that a message is picked up while its bubble is
				// still on screen, cheap enough to be one component lookup and one string read.
				_nextPoll = now + 0.25f;
				Poll();
			}
			catch (Exception e) { Status = "chat mimic: " + e.Message; }
		}

		private static void Poll()
		{
			VaTagsModule.PlayerEntry target = null;
			var roster = VaTagsModule.Roster;
			try
			{
				lock (roster)
					foreach (var p in roster)
						if (p != null && string.Equals(p.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) { target = p; break; }
			}
			catch { }
			if (target == null) { Stop("they left"); return; }

			Component bubble = ResolveBubble(target);
			if (bubble == null) { Status = "waiting for " + TargetName + "'s chat bubble"; return; }

			string text = ReadText(bubble);
			if (string.IsNullOrEmpty(text)) return;
			if (string.Equals(text, _lastSeen, StringComparison.Ordinal)) return;   // still the same bubble

			_lastSeen = text;
			lock (Gate)
			{
				if (_pending.Count >= MaxQueued) _pending.Dequeue();
				_pending.Enqueue(text);
			}
			Status = "mimicking " + TargetName + " — last: " + (text.Length > 40 ? text.Substring(0, 40) + "…" : text);
			VRChatArchiveModPlugin.Logger.LogInfo("[ChatMimic] " + TargetName + ": " + text);
		}

		// THE BUBBLE IS NOT UNDER THE PLAYER. This used to do
		//     playerTransform.GetComponentInChildren(typeof(ChatBubbleDisplay), true)
		// which assumed a chat bubble hangs off its own player's object. It does not, and a UI capture
		// off the owner's own machine (captures/audio_2026-08-26_19-14-55.txt) says so outright —
		// every live bubble sits at
		//     _Application <guid>/NameplateManager/NameplateContainer/ChatBubble/…
		// under ONE global manager, interleaved with the VRCPlayer[Remote] objects rather than inside
		// them. So the search could never match, ResolveBubble returned null forever, and Poll() took
		// its silent "waiting for their chat bubble" branch — which writes to Status and NOT to the
		// log. The feature shipped in v302 looking armed and never captured a line.
		//
		// ASK THE OBJECT, DO NOT MEASURE THE TREE. BaseChatBubbleDisplay carries
		// field_Protected_VRCPlayer_0 — the bubble states which player it belongs to. That is the same
		// lesson as the Among Us per-player presets earlier today: identity beats hierarchy, and it
		// survives VRChat moving things around.
		//
		// The base type, not ChatBubbleDisplay: the VRCPlayer member is declared on
		// BaseChatBubbleDisplay, so matching there covers every derived variant this build ships.
		private static Component ResolveBubble(VaTagsModule.PlayerEntry e)
		{
			try
			{
				if (_bubbleIl2 == null)
				{
					if (_bubbleType == null)
					{
						foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
						{
							try { _bubbleType = asm.GetType("BaseChatBubbleDisplay", false) ?? asm.GetType("ChatBubbleDisplay", false); }
							catch { continue; }
							if (_bubbleType != null) break;
						}
					}
					if (_bubbleType == null)
					{
						if (!_typeLogged) { _typeLogged = true; VRChatArchiveModPlugin.Logger.LogWarning("[ChatMimic] BaseChatBubbleDisplay is not present on this build — chatbox mimic unavailable."); }
						return null;
					}
					try { _bubbleIl2 = Il2CppType.From(_bubbleType); } catch { }
					if (_bubbleIl2 == null) return null;
					if (!_typeLogged) { _typeLogged = true; VRChatArchiveModPlugin.Logger.LogInfo("[ChatMimic] resolved " + _bubbleType.Name + " by type."); }
				}

				Transform want = e.Transform;
				if (want == null || !NativeGuard.Alive(want)) return null;
				Transform wantRoot = RootOf(want);

				// Cached until their bubble is rebuilt (avatar change, respawn, they leave).
				if (_bubble != null && NativeGuard.Alive(_bubble))
				{
					try { if (_bubble.GetInstanceID() == _bubbleId) return _bubble; }
					catch { }
				}

				// THE PLAYER'S OWN NAMEPLATE CONTAINER — the right way in, and the mod already knew it.
				//
				// The scene-wide search below found all 25 bubbles and matched none of them, which
				// ruled out "they have no bubble" and left the matching itself wrong. The answer was
				// already in FewTagsModule, which the nameplate tags have used in production for
				// weeks: VRCPlayer exposes a GameObject FIELD called "NameplateContainer", one PER
				// PLAYER, and it is parented under the global NameplateManager rather than under the
				// player. That is exactly the shape the owner's capture showed —
				//     _Application/<guid>/NameplateManager/NameplateContainer/ChatBubble/…
				// — a per-player container living somewhere else entirely. So the bubble is not found
				// by walking the player's transform, and not by scanning the scene: it is found by
				// asking the player for its container and looking inside THAT.
				//
				// It also costs nothing. The scene scan below was measured at 53 ms/s of the mod's
				// budget on its own; this is one field read and one subtree search.
				try
				{
					GameObject cont = FewTagsModule.FindNameplateContainer(e.Player);
					if (cont != null && NativeGuard.Alive(cont))
					{
						Component c = null;
						try { c = cont.transform.GetComponentInChildrenSafe(_bubbleIl2, true); } catch { }
						if (c != null && NativeGuard.Alive(c))
						{
							_bubble = c;
							try { _bubbleId = c.GetInstanceID(); } catch { _bubbleId = 0; }
							if (!_viaContainerLogged)
							{
								_viaContainerLogged = true;
								VRChatArchiveModPlugin.Logger.LogInfo("[ChatMimic] found the bubble through the player's own NameplateContainer.");
							}
							return c;
						}
					}
				}
				catch { }

				// AND NOT FOUR TIMES A SECOND. Poll runs at 4 Hz, but the enumeration below walks every
				// loaded object in the scene — cheap once, ruinous as a heartbeat, and it would run on
				// EVERY poll for as long as the target has no bubble yet (which is most of the time:
				// bubbles are made on demand). The cache above covers the found case; this covers the
				// not-found one, at once per second.
				float nowR = VaClock.Now;
				if (nowR < _nextSearch) return null;
				_nextSearch = nowR + 3f;   // the container path above is the normal one; this is the net

				// EVERY bubble in the scene, inactive ones included — a bubble is disabled while nobody
				// is talking, so FindObjectsOfTypeAll is the only enumeration that sees it. The
				// NON-GENERIC overload with an Il2CppSystem.Type: the generic one comes back empty for
				// VRChat's own classes (see Core/Il2CppSeq and the mod's notes on that trap).
				Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<UnityEngine.Object> all = null;
				try { all = Resources.FindObjectsOfTypeAll(_bubbleIl2); }
                catch (Exception ex) { Status = "chat mimic: could not list bubbles (" + ex.Message + ")"; return null; }
				if (all == null) return null;

				int seen = 0;
				for (int i = 0; i < all.Count; i++)
				{
					var c = all[i] != null ? all[i].TryCast<Component>() : null;
					if (c == null || !NativeGuard.Alive(c)) continue;
					seen++;
					if (OwnerRootOf(c) != wantRoot) continue;
					_bubble = c;
					try { _bubbleId = c.GetInstanceID(); } catch { _bubbleId = 0; }
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[ChatMimic] matched " + TargetName + "'s bubble (" + seen + " of " + all.Count + " in the scene).");
					return c;
				}

				// NOT SILENT ANY MORE. The old code returned null here and said nothing, which is how a
				// feature that never worked looked identical to one nobody had typed into yet.
				if (!_noneLogged)
				{
					_noneLogged = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[ChatMimic] " + TargetName + " has no NameplateContainer bubble, and none of the " + all.Count
						+ " bubble(s) in the scene name them either. Either they have not been given one yet, or "
						+ "VRChat has moved both the NameplateContainer field and the VRCPlayer member.");
				}
				return null;
			}
			catch (Exception ex) { Status = "chat mimic: " + ex.Message; return null; }
		}

		private static bool _noneLogged;
		private static bool _viaContainerLogged;
		private static float _nextSearch;

		private static Transform RootOf(Transform t)
		{
			try
			{
				int guard = 0;
				while (t != null && t.parent != null && guard++ < 32) t = t.parent;
				return t;
			}
			catch { return t; }
		}

		// Which player rig this bubble says it belongs to, as a transform root we can compare against.
		// Resolved BY MEMBER TYPE ("VRCPlayer"), not by the generated member name, because those names
		// change on every VRChat update; the generated ones are only hints for the lookup.
		private static Transform OwnerRootOf(Component bubble)
		{
			try
			{
				object vp = FewTagsModule.GetMemberByTypeName(bubble, "VRCPlayer", "field_Protected_VRCPlayer_0", "field_Private_VRCPlayer_0");
				var comp = (vp as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Component>();
				if (comp == null || !NativeGuard.Alive(comp)) return null;
				return RootOf(comp.transform);
			}
			catch { return null; }
		}

		// _chatText is a raw field read (see the header), so this is the line the FieldOffsetFix gate
		// exists for. TMP_Text.text itself goes through il2cpp_runtime_invoke, which is safe.
		private static PropertyInfo _chatTextProp;
		private static MethodInfo _toBubble;
		private static bool _fallbackLogged;

		private static string ReadText(Component bubble)
		{
			try
			{
				// CAST IT BACK TO THE REAL TYPE FIRST. GetComponentInChildren(Il2CppSystem.Type, bool)
				// is the NON-generic overload, so what it hands back is typed UnityEngine.Component —
				// and bubble.GetType() is therefore Component, not BaseChatBubbleDisplay. Looking for
				// "_chatText" on Component finds nothing, so this quietly fell through to the
				// longest-string fallback below every single time. That fallback happens to pick the
				// right label, which is exactly the kind of accident that looks like a working feature
				// until the day a second text field appears.
				object typed = bubble;
				try
				{
					if (_toBubble == null && _bubbleType != null)
						_toBubble = typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(_bubbleType);
					if (_toBubble != null) typed = _toBubble.Invoke(bubble, null) ?? bubble;
				}
				catch { typed = bubble; }

				if (_chatTextProp == null)
				{
					var mt = typed.GetType();
					_chatTextProp = mt.GetProperty("_chatText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (_chatTextProp == null)
					{
						// The member really is absent on this build: fall back to the TMP text under the
						// bubble. Logged once, because a silent fallback is how this module hid a bug for
						// two versions.
						if (!_fallbackLogged)
						{
							_fallbackLogged = true;
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[ChatMimic] no _chatText member on " + mt.Name + " — reading the bubble's TMP text instead.");
						}
						return ReadTextByChild(bubble);
					}
				}
				object tmp;
				try { tmp = _chatTextProp.GetValue(typed); }
				catch { return ReadTextByChild(bubble); }
				if (tmp == null) return "";
				var ob = tmp as Il2CppObjectBase;
				if (ob != null && !NativeGuard.Alive(ob)) return "";
				var textProp = tmp.GetType().GetProperty("text");
				return textProp?.GetValue(tmp) as string ?? "";
			}
			catch { return ""; }
		}

		private static string ReadTextByChild(Component bubble)
		{
			try
			{
				var go = bubble.gameObject;
				if (go == null || !NativeGuard.Alive(go)) return "";
				var tmps = go.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
				if (tmps == null) return "";
				// Longest non-empty wins: the typing indicator is the other TextMeshProUGUI on this
				// hierarchy and it carries dots, never a sentence.
				string best = "";
				for (int i = 0; i < tmps.Length; i++)
				{
					string s;
					try { s = tmps[i]?.text ?? ""; } catch { continue; }
					if (s.Length > best.Length) best = s;
				}
				return best;
			}
			catch { return ""; }
		}

		public override void OnSceneLoaded(int buildIndex) { _bubble = null; _bubbleId = 0; _lastSeen = ""; }
		public override void OnShutdown() { Stop("shutdown"); }
	}
}
