using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PUT A URL IN THE WORLD'S VIDEO PLAYER.
	//
	// This drives the same mechanism the world's own URL box drives: a video player is an
	// UdonBehaviour holding a VRCUrl variable, and playing something means writing that variable
	// and firing the player's "load this" event. Nothing here is a hole in VRChat — the field is
	// meant to be written, and Udon's own whitelist (which stops a SCRIPT from fabricating a
	// VRCUrl) does not apply to us because we construct it natively.
	//
	// IT IS SYNCED, SO IT IS EVERYONE'S BUSINESS. Ownership is taken and the URL is written into
	// the script's SYNCED variable, then ONE play event is fired locally — exactly what pressing
	// the world's button does; the script's own sync carries it to the whole instance. (Nothing is
	// broadcast by us: a SendCustomNetworkEvent on top made every client re-trigger its load.) That
	// reach is what the feature is for, but it also means many worlds gate it to the instance owner
	// and many communities read it as trolling. The mod does not soften that; it just does what it
	// was asked, and says who it reached.
	public class VideoUrlModule : IModule
	{
		public override string Name => "VideoUrl";

		public static string LastStatus = "";
		public static string LastUrl = "";

		// Every video-player system names its "start playing" entry point differently, and a world
		// can ship any of them. They are tried in order and the ones that do not exist are simply
		// ignored — an UdonBehaviour rejects an event it has no entry point for.
		private static readonly string[] PlayEvents =
		{
			"ForceSyncVideo",      // USharpVideo: the OWNER reloads from _syncedURL (it has no plain Play; found in the 2026-09-02 dump)
			"SyncVideo",           // USharpVideo, softer variant
			"_ChangeMedia",        // ProTV
			"OnURLChanged",        // USharpVideo
			"_TriggerPlay",        // VideoTXL / USharpVideo
			"_UrlChanged",
			"PlayVideo", "_Play", "Play", "_PlayVideo",
			"OnURLInput", "_OnURLInput",
		};

		private static Type _udonType;
		private static Type _urlType;
		private static ConstructorInfo _urlCtor;

		public override void OnInitialize()
		{
			VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] armed — URL injection into the world's video player.");
		}

		private static Type FindType(string full)
		{
			try
			{
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					Type t;
					try { t = asm.GetType(full, false); }
					catch { continue; }
					if (t != null) return t;
				}
			}
			catch { }
			return null;
		}

		private static bool Resolve()
		{
			if (_udonType == null)
				_udonType = Type.GetType("VRC.Udon.UdonBehaviour, VRC.Udon", false)
					?? FindType("VRC.Udon.UdonBehaviour");

			if (_urlType == null)
				_urlType = FindType("VRC.SDKBase.VRCUrl") ?? FindType("VRCUrl");

			if (_urlCtor == null && _urlType != null)
			{
				try { _urlCtor = _urlType.GetConstructor(new[] { typeof(string) }); }
				catch { }
			}
			return _udonType != null && _urlType != null && _urlCtor != null;
		}

		/// <summary>
		/// Writes <paramref name="url"/> into every video player found and asks them to play it.
		/// Returns the number of players reached.
		/// </summary>
		public static int Inject(string url)
		{
			// Before anything: did the LAST one come back? If not, say what it died on.
			ReportPreviousCrash();

			LastUrl = url ?? "";
			if (string.IsNullOrWhiteSpace(url))
			{
				LastStatus = "no URL to send";
				return 0;
			}
			// A bare word would be written into the player and fail there instead of here, with no
			// explanation anywhere the user can see.
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				&& !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
				&& !url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
			{
				LastStatus = "that is not a URL — it has to start with http:// or https://";
				return 0;
			}

			// THE TRAIL IS OPEN FROM HERE TO THE END OF THE INJECTION. Everything past this point
			// touches world scripts through native il2cpp calls, which is where the game dies without
			// an exception; from here on, every Probe() is on disk before its call runs.
			Core.CrashTrail.Begin(TrailName, "url=" + url + "\r\nworld=" + WorldTag() + "\r\nmod=" + PluginInfo.Version);

			if (!Resolve())
			{
				LastStatus = "this build exposes no Udon/VRCUrl types — cannot reach the player";
				Core.CrashTrail.End();
				return 0;
			}

			object vrcUrl;
			Probe("building the VRCUrl object");
			try { vrcUrl = _urlCtor.Invoke(new object[] { url }); }
			catch (Exception e)
			{
				LastStatus = "could not build a VRCUrl: " + Short(e.Message);
				Core.CrashTrail.End();
				return 0;
			}

			// FIRST: THE WORLD'S OWN SCRIPT. A video player is an UdonBehaviour holding a VRCUrl
			// variable; writing that variable and firing its play event is exactly what the world's
			// URL box does, so the script loads the video ITSELF, syncs it to the instance, and its
			// retry/queue logic stays consistent with what it thinks is playing.
			//
			// THE TWO PATHS MUST NOT BOTH RUN. They used to: the direct LoadURL below started the
			// player, then the script path handed the same URL to the script, which called LoadURL
			// again on a player already loading — an error the script's retry loop answered by
			// re-issuing the load, over and over (the "resolved googlevideo stream appears, then
			// repeats ~80 s later, never simply plays" the owner reported). Now the direct component
			// path is the FALLBACK, taken only when no script exposed a VRCUrl symbol at all.
			// SCAN ONLY VIDEO-PLAYER SCRIPTS, never every UdonBehaviour in the world. Reading the
			// symbol table and variable types of ~145 arbitrary world scripts is what crashed the game
			// (a native call inside one script's program AV'd). A video player's script sits ON or
			// ABOVE a BaseVRCVideoPlayer, so we gather only the behaviours on the player object and its
			// ancestors (bounded, ~a handful) and scan those.
			int scripted = 0;
			try
			{
				Probe("collecting video-adjacent behaviours");
				var swCollect = System.Diagnostics.Stopwatch.StartNew();
				var candidates = CollectVideoScripts();
				swCollect.Stop();
				Probe("scanning " + candidates.Count + " video-adjacent behaviour(s) for a VRCUrl symbol");
				// TIMED IN TWO HALVES, because a one-second freeze needs an address and not a guess.
				// Reading a script's symbol table is a reflection call per symbol; writing is a
				// handful of calls total. Only the log can say which of the two the second went to.
				_scanCollectMs = swCollect.Elapsed.TotalMilliseconds;
				_scanReadMs = 0.0; _scanWriteMs = 0.0; _scanSymbols = 0;
				double readMs = 0.0, writeMs = 0.0;
				int symbolsSeen = 0;
				int idx = 0;
				foreach (var ub in candidates)
				{
					if (ub == null || !NativeGuard.Alive(ub)) continue;
					// EVERY candidate, and named. The cap was "if (idx < 6)", which is fine for reading
					// a log afterwards and useless for a crash: die on candidate #9 and the trail's last
					// line is about #5. The GameObject's name is the whole point — the crash belongs to
					// ONE script in ONE world, and this is what identifies it.
					string who = SafeName(ub.TryCast<Component>()?.gameObject);
					Probe("candidate #" + idx + " '" + who + "' — reading symbols");
					var swOne = System.Diagnostics.Stopwatch.StartNew();
					string symbol = FindUrlSymbol(ub);
					swOne.Stop(); readMs += swOne.Elapsed.TotalMilliseconds; _scanReadMs = readMs;
					symbolsSeen += _lastSymbolCount; _scanSymbols = symbolsSeen;
					Probe("candidate #" + idx + " '" + who + "' — symbols via " + _lastSymbolPath
						+ (symbol == null ? ", no VRCUrl" : ", VRCUrl '" + symbol + "'"));
					idx++;
					if (symbol == null) continue;
					Probe("candidate #" + idx + " '" + who + "' — WRITING '" + symbol + "'");
					swOne.Restart();
					if (TrySet(ub, symbol, vrcUrl)) scripted++;
					swOne.Stop(); writeMs += swOne.Elapsed.TotalMilliseconds; _scanWriteMs = writeMs;
					Probe("candidate #" + idx + " '" + who + "' — write returned");
				}
			}
			catch { }

			// FALLBACK: DRIVE THE VIDEO COMPONENT DIRECTLY. Every world player ultimately hands its
			// URL to a BaseVRCVideoPlayer (the SDK's VRCUnityVideoPlayer / AVPro player), whose
			// LoadURL(VRCUrl) plays on that player with no dependency on the script's variable
			// names — but it is local only, and the script does not know it happened.
			int direct = 0;
			if (scripted == 0) direct = DriveVideoComponents(url);

			if (scripted == 0 && direct == 0)
			{
				DumpVideoTargetsOnce();
				LastStatus = "no video player found in this world (subtree dumped for tuning)";
			}
			else if (scripted > 0)
				LastStatus = scripted + " player script(s) given the URL and told to play";
			else
				LastStatus = direct + " player(s) started directly (no script exposed a URL; local only)";
			VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] " + LastStatus + " :: " + url);
			// Got here alive: the trail is marked COMPLETED, so the next run knows this attempt did
			// not crash. Only an injection that never reaches this line leaves an unfinished trail.
			Probe("done — " + scripted + " via script, " + direct + " direct");
			if (_scanReadMs + _scanWriteMs > 60.0)
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[VideoUrl] injection cost: collect " + _scanCollectMs.ToString("0.#") + " ms, symbols "
					+ _scanReadMs.ToString("0.#") + " ms over " + _scanSymbols + " symbol(s), write "
					+ _scanWriteMs.ToString("0.#") + " ms — all on one frame.");
			Core.CrashTrail.End();
			return scripted + direct;
		}

		/// <summary>World name and id for the trail header — the crash is almost certainly a property
		/// of ONE world's script, so the file has to say which world it was.</summary>
		private static string WorldTag()
		{
			try
			{
				string n = "";
				try { n = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
				return string.IsNullOrEmpty(n) ? "?" : n;
			}
			catch { return "?"; }
		}

		private static Type _basePlayerType;
		private static System.Reflection.MethodInfo _loadUrl;
		private static System.Reflection.ConstructorInfo _playerCtor;
		private static System.Reflection.ConstructorInfo _urlParamCtor;
		private static bool _videoDumped;

		// Finds every BaseVRCVideoPlayer in the world and calls LoadURL(vrcUrl) on it.
		private static int DriveVideoComponents(object urlString)
		{
			int n = 0;
			try
			{
				if (_basePlayerType == null)
					_basePlayerType = FindType("VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer")
						?? FindType("BaseVRCVideoPlayer");
				if (_basePlayerType == null) return 0;

				if (_loadUrl == null)
				{
					foreach (var m in _basePlayerType.GetMethods())
					{
						if (m.Name != "LoadURL") continue;
						if (m.GetParameters().Length == 1) { _loadUrl = m; break; }
					}
				}
				if (_loadUrl == null) return 0;

				// BUILD THE VRCUrl FROM LoadURL'S OWN PARAMETER TYPE.
				//
				// "Object does not match target type" was two mismatches at once: the argument was a
				// VRCUrl of a type resolved elsewhere, not necessarily the exact type this method's
				// signature names. Constructing it from LoadURL's own parameter type guarantees the
				// argument matches.
				if (_urlParamCtor == null)
				{
					Type pt = _loadUrl.GetParameters()[0].ParameterType;
					_urlParamCtor = pt.GetConstructor(new[] { typeof(string) });
				}
				if (_urlParamCtor == null) return 0;
				object vrcUrl;
				try { vrcUrl = _urlParamCtor.Invoke(new object[] { (string)urlString }); }
				catch { return 0; }

				if (_playerCtor == null)
					_playerCtor = _basePlayerType.GetConstructor(new[] { typeof(IntPtr) });
				if (_playerCtor == null) return 0;

				Probe("direct: FindObjectsOfType(BaseVRCVideoPlayer)");
				var il2 = Il2CppType.From(_basePlayerType);
				var players = VRChatArchiveMod.Core.Live.AllOfType(il2);
				if (players == null) return 0;
				Probe("direct: " + players.Length + " player component(s) found");

				for (int i = 0; i < players.Length; i++)
				{
					var p = players[i];
					if (p == null || !NativeGuard.Alive(p)) continue;
					// Named and unthrottled, for the same reason as the script loop: LoadURL runs the
					// world's own player code, and if that is what kills the game the trail has to say
					// on WHICH player it happened.
					Probe("direct: LoadURL on player #" + i + " '" + SafeName(p.TryCast<Component>()?.gameObject) + "'");
					try
					{
						// RE-WRAP AT THE EXACT TYPE. FindObjectsOfType hands back UnityEngine.Object
						// wrappers; a reflected Invoke needs the instance to be the method's declaring
						// type. Reconstruct the proxy of _basePlayerType over the same il2cpp pointer,
						// exactly the fix used elsewhere in the mod for the same trap.
						IntPtr ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)p).Pointer;
						if (ptr == IntPtr.Zero) continue;
						object typed = _playerCtor.Invoke(new object[] { ptr });
						_loadUrl.Invoke(typed, new object[] { vrcUrl });
						n++;
					}
					catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] LoadURL threw: " + Short(e.Message)); }
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] component path threw: " + Short(e.Message)); }
			return n;
		}

		// One-time diagnostic when nothing matched: name every BaseVRCVideoPlayer and every
		// UdonBehaviour with its variable symbols, so the real target can be read off the log.
		private static void DumpVideoTargetsOnce()
		{
			if (_videoDumped) return;
			_videoDumped = true;
			try
			{
				if (_basePlayerType != null)
				{
					var players = VRChatArchiveMod.Core.Live.AllOfType(Il2CppType.From(_basePlayerType));
					VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] BaseVRCVideoPlayer count: " + (players?.Length ?? 0));
				}
				else VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] BaseVRCVideoPlayer TYPE not found in interop.");

				var uAll = VRChatArchiveMod.Core.Live.AllOfType(Il2CppType.From(_udonType));
				VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] UdonBehaviour count: " + (uAll?.Length ?? 0));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] dump threw: " + Short(e.Message)); }
		}

		// THE FIELD IS FOUND BY TYPE, NOT BY NAME. USharpVideo, ProTV and VideoTXL each call their
		// URL variable something different, and a world can ship a custom player that calls it
		// anything at all. What they cannot vary is the TYPE: a video URL is a VRCUrl. So every
		// public variable is read and the first VRCUrl-typed one wins.
		// Behaviours to scan for a URL: those on each BaseVRCVideoPlayer's object and its ancestors
		// (USharpVideo/ProTV keep the script on the player object or a parent). Ancestor-only walk is
		// bounded (<=8 levels) and never touches the hundreds of unrelated world scripts, which is
		// both the crash fix and the correct target set. Capped at 50.
		private static System.Collections.Generic.List<VRC.Udon.UdonBehaviour> CollectVideoScripts()
		{
			var outp = new System.Collections.Generic.List<VRC.Udon.UdonBehaviour>();
			var seen = new System.Collections.Generic.HashSet<int>();
			try
			{
				if (_basePlayerType == null)
					_basePlayerType = FindType("VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer") ?? FindType("BaseVRCVideoPlayer");
				if (_basePlayerType == null) return outp;
				var players = VRChatArchiveMod.Core.Live.AllOfType(Il2CppType.From(_basePlayerType));
				if (players == null) return outp;
				var tops = new System.Collections.Generic.List<Transform>();
				for (int i = 0; i < players.Length && outp.Count < 50; i++)
				{
					var pl = players[i];
					if (pl == null || !NativeGuard.Alive(pl)) continue;
					Component comp = pl.TryCast<Component>(); if (comp == null) continue;
					Transform t = null; try { t = comp.transform; } catch { }
					int up = 0; Transform top = t;
					for (Transform cur = t; cur != null && up < 8 && outp.Count < 50; cur = cur.parent, up++) { AddUdon(cur, outp, seen); top = cur; }
					if (top != null) tops.Add(top);
				}
				// THEN THE PLAYER'S WHOLE PREFAB. The script that owns the URL is not always on or above
				// the video component: iwaSync3 / ProTV / VideoTXL keep it on a sibling ("Udon", "Core",
				// "TVManager") under the same prefab root. Ancestors gave nothing on 2026-09-04 (3 behaviours,
				// no VRCUrl), so the subtree of the highest ancestor reached is scanned too, still bounded —
				// never the whole world (reading ~145 arbitrary programs is what once crashed the game).
				for (int i = 0; i < tops.Count && outp.Count < 60; i++)
				{
					try
					{
						var ubs = tops[i].GetComponentsInChildren<VRC.Udon.UdonBehaviour>(true);
						if (ubs == null) continue;
						for (int k = 0; k < ubs.Length && outp.Count < 60; k++) { var u = ubs[k]; if (u != null && seen.Add(u.GetInstanceID())) outp.Add(u); }
					}
					catch { }
				}
			}
			catch { }
			return outp;
		}
		private static void AddUdon(Transform t, System.Collections.Generic.List<VRC.Udon.UdonBehaviour> outp, System.Collections.Generic.HashSet<int> seen)
		{
			try
			{
				if (t == null) return;
				var ubs = t.GetComponents<VRC.Udon.UdonBehaviour>();
				if (ubs == null) return;
				for (int i = 0; i < ubs.Length; i++) { var u = ubs[i]; if (u != null && seen.Add(u.GetInstanceID())) outp.Add(u); }
			}
			catch { }
		}

		private static string FindUrlSymbol(Il2CppObjectBase ub)
		{
			try
			{
				var vars = _udonType.GetProperty("publicVariables")?.GetValue(ub);
				if (vars == null) return null;

				// VariableSymbols is an IReadOnlyCollection<string> PROXY: not a managed IEnumerable
				// (the old `as` cast always gave null, so the VRCUrl symbol was never found and this
				// path silently reached nothing). The shared reader walks its native enumerator.
				// Exported symbols off the program (a string[]) before the variable table's KeyCollection
				// (CopyTo only). See Core.UdonSymbols / Core.Il2CppSeq for the 2026-09-02 crash.
				// ALL symbols, not the exported ones: the URL a world player is PLAYING lives in a
				// private [UdonSynced] field (USharpVideo _syncedURL, ProTV/VideoTXL alike), which
				// publicVariables never lists. On 2026-09-02 the public list found no VRCUrl on any of
				// 55 behaviours, the injection fell back to the raw player, and the script promptly
				// re-asserted its own synced URL: "it just restarts the original video".
				var symbols = UdonSymbols.All(ub);
				_lastSymbolPath = "UdonSymbols." + UdonSymbols.LastPath;
				_lastSymbolCount = symbols.Count;
				if (symbols.Count == 0)
				{
					var symbolsProp = vars.GetType().GetProperty("VariableSymbols");
					symbols = Il2CppSeq.Strings(symbolsProp?.GetValue(vars));
					_lastSymbolPath = "Il2CppSeq." + Il2CppSeq.LastPath + " (UdonSymbols gave nothing: " + UdonSymbols.LastPath + ")";
				}
				if (symbols.Count == 0) return null;

				// Match by the DECLARED TYPE, never by reading the live value. The old path called
				// GetProgramVariable(sym) then Il2CppNameOf(value) on EVERY symbol of EVERY
				// UdonBehaviour in the world -- and reading the type of a variable whose value wraps a
				// dead/uninitialised il2cpp object is an access violation no try/catch survives. That
				// is the crash this module caused. GetProgramVariableType returns type metadata only
				// (never a live object), so it is safe on every symbol.
				var getType = _udonType.GetMethod("GetProgramVariableType", new[] { typeof(string) });
				if (getType == null) return null;

				// Among the VRCUrl-typed symbols, the one that names itself a URL wins (_syncedURL,
				// _url, syncUrl...); playlist/default entries lose; arrays (VRCUrl[]) never qualify.
				// ONE ARGS ARRAY AND ONE PASS. GetProgramVariableType is a reflection Invoke into
				// il2cpp, paid once per symbol per candidate script — several hundred times for a
				// single injection in a ProTV world, which is where the 990 ms freeze came from. The
				// array is reused (Invoke copies the argument before it returns), and the type NAMES
				// are kept as they are computed, so the diagnostic block below reads them back
				// instead of invoking a second time for the same symbols.
				string best = null; int bestScore = -1; var seen = new List<string>();
				object[] symArg = new object[1];
				var typeNames = new List<string>(symbols.Count);
				foreach (string sym in symbols)
				{
					if (string.IsNullOrEmpty(sym)) { typeNames.Add(null); continue; }
					object t;
					symArg[0] = sym;
					try { t = getType.Invoke(ub, symArg); }
					catch { typeNames.Add(null); continue; }
					string tn = TypeNameOf(t);
					typeNames.Add(tn);
					if (!string.Equals(tn, "VRCUrl", StringComparison.Ordinal)) continue;
					seen.Add(sym);
					string lo = sym.ToLowerInvariant();
					int score = 1;
					if (lo.Contains("url")) score += 4;
					if (lo.Contains("sync")) score += 3;
					if (lo.Contains("current") || lo.Contains("playing") || lo.Contains("pending")) score += 2;
					if (lo.Contains("default") || lo.Contains("playlist") || lo.Contains("queue") || lo.Contains("fallback")) score -= 3;
					if (score > bestScore) { bestScore = score; best = sym; }
				}
				if (seen.Count > 0) Probe("VRCUrl symbols: " + string.Join(", ", seen) + " -> " + best);
				else if (_dumpedNoUrl < 6)
				{
					// DIAGNOSTIC when a candidate has no VRCUrl at all: its first symbols with their declared
					// types (metadata only), so a player whose URL lives under another type or name can be
					// recognised from the log instead of guessed at.
					_dumpedNoUrl++;
					var sb = new System.Text.StringBuilder();
					int n = 0;
					// Reads the names resolved by the pass above — this used to Invoke all over again
					// for up to 30 more symbols per candidate, purely to print them.
					for (int i = 0; i < symbols.Count && i < typeNames.Count; i++)
					{
						string sym = symbols[i];
						if (string.IsNullOrEmpty(sym) || typeNames[i] == null) continue;
						if (n++ > 0) sb.Append(", ");
						sb.Append(sym).Append(':').Append(typeNames[i]);
						if (n >= 30) { sb.Append(", …"); break; }
					}
					Probe("no VRCUrl on '" + SafeName(ub.TryCast<Component>()?.gameObject) + "' — " + symbols.Count + " symbol(s): " + sb);
				}
				return best;
			}
			catch { }
			return null;
		}

		// GetProgramVariableType hands back a managed Type on some builds and an il2cpp Type proxy on
		// others; both answer to Name. Reading Name is metadata only -- it never touches a live value.
		private static string TypeNameOf(object t)
		{
			if (t == null) return "?";
			if (t is Type mt) return mt.Name ?? "?";
			try
			{
				string s = t.GetType().GetProperty("Name")?.GetValue(t) as string;
				if (!string.IsNullOrEmpty(s)) return s;
			}
			catch { }
			return "?";
		}

		private static bool TrySet(Il2CppObjectBase ub, string symbol, object vrcUrl)
		{
			try
			{
				GameObject go = null;
				try { go = (_udonType.GetProperty("gameObject")?.GetValue(ub)) as GameObject; }
				catch { }

				// SYNCED: take the object first, or the write is overwritten by whoever owns it on
				// their next sync tick and nobody else ever hears it. Gated so we NEVER SetOwner on
				// an object with no network state (that takes the game down): a VRCObjectSync/Pickup,
				// OR — crucially for USharpVideo/ProTV — an UdonBehaviour that actually SYNCS (a
				// Manual/Continuous SyncMethod). Those players carry their URL on their OWN synced
				// UdonBehaviour, no ObjectSync, so without this the set never reaches other clients
				// and the video "won't load".
				bool synced = false;
				try { synced = go != null && (IsNetworked(go) || UdonHasSync(ub)); } catch { }
				Probe("ownership: " + SafeName(go) + " synced=" + synced);
				if (synced) TakeOwnership(go);

				// THE SETTER IS RESOLVED BY SHAPE, once. GetMethod(name, {string, object}) never
				// matches (the plain overload takes Il2CppSystem.Object, not System.Object) and the
				// bare GetMethod(name) throws AmbiguousMatchException because a generic
				// SetProgramVariable<T> sits beside it — so the old lookup failed on every call and
				// the catch below reported "not reached". The generic overload is preferred, closed
				// over the VRCUrl proxy type so the interop layer does the marshalling; the plain one
				// is accepted when the proxy is an instance of its parameter type.
				if (!_setResolved) ResolveSetter();
				// PLAIN FIRST. The VRCUrl we hold is already an il2cpp object (built by its own
				// constructor), so SetProgramVariable(string, Il2CppSystem.Object) takes it as is.
				// The generic SetProgramVariable<T> was tried first before 2026-09-02, and the game
				// died inside this method on every injection: closing a generic il2cpp method over
				// a proxy type goes through the interop's generic-instantiation machinery, which is
				// exactly the path a build with reshuffled runtime structs breaks. Generic stays as
				// the fallback for a build that exposes no plain overload.
				Probe("set " + symbol + " on " + SafeName(go) + " (plain=" + (_setPlain != null) + ", generic=" + (_setGeneric != null) + ")");
				if (_setPlain != null && _setPlainParam != null && _setPlainParam.IsInstanceOfType(vrcUrl))
					_setPlain.Invoke(ub, new object[] { symbol, vrcUrl });
				else if (_setGeneric != null)
					_setGeneric.MakeGenericMethod(vrcUrl.GetType()).Invoke(ub, new object[] { symbol, vrcUrl });
				else return false;
				Probe("set ok");

				// A synced field only leaves this client when the owner asks for a serialisation pass;
				// Manual-sync players (USharpVideo, ProTV) never send otherwise. We own the object by
				// now (TakeOwnership above), so this is exactly what the world's own URL box does.
				if (synced)
				{
					try
					{
						var rs = _udonType.GetMethod("RequestSerialization", Type.EmptyTypes);
						if (rs != null) { rs.Invoke(ub, null); Probe("RequestSerialization sent"); }
					}
					catch (Exception e) { Probe("RequestSerialization threw: " + Short(e.Message)); }
				}

				// ONE PLAY EVENT, LOCALLY. The synced variable written above (after TakeOwnership) is
				// what carries the URL to the rest of the instance — the script's own sync does the
				// rest, exactly as the world's URL box does. The old loop fired EVERY name in
				// PlayEvents, each one both locally and as a network broadcast: every other client
				// then re-triggered its own load, our player got the same URL loaded several times
				// over, and the script's retry logic kept re-issuing it — the "plays, then repeats
				// 80 s later, never just plays" the owner saw. So the event is the first one this
				// behaviour actually EXPORTS, sent once with SendCustomEvent. When the export list
				// cannot be read, the first PlayEvents name is sent once; an unknown event is simply
				// ignored by the behaviour.
				var send = _udonType.GetMethod("SendCustomEvent", new[] { typeof(string) });
				if (send == null) return false;

				string chosen = null;
				Probe("exported events");
				var exported = ExportedEvents(ub);
				Probe("exported: " + exported.Count + (exported.Count > 0 ? " [" + string.Join(", ", exported) + "]" : ""));
				if (exported.Count > 0)
				{
					foreach (string ev in PlayEvents)
						if (exported.Contains(ev)) { chosen = ev; break; }
					if (chosen == null)
					{
						VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] " + symbol + " set; the script exports none of the known play events (URL will apply on its next own load)");
						return true;   // the URL is in the synced variable; that alone is a reach
					}
				}
				else chosen = PlayEvents[0];

				Probe("send " + chosen);
				try { send.Invoke(ub, new object[] { chosen }); }
				catch (Exception e)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[VideoUrl] SendCustomEvent(" + chosen + ") threw: " + Short(e.Message));
					return false;
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] " + symbol + " set, sent " + chosen + " locally");
				return true;
			}
			catch { return false; }
		}

		private static bool _setResolved;
		// STEP PROBES. The 2026-09-01 crash left the log ending on "applied action videoUrl" and
		// nothing else: an access violation writes no exception, so the only trace of WHERE the
		// process died is the last line that made it to disk. Each step announces itself first.
		private static void Probe(string step)
		{
			// BOTH, and the trail is the one that matters. The logger is BUFFERED, so on an access
			// violation its last lines never reach disk — which is exactly how the 2026-09-01 crash
			// managed to say nothing at all. CrashTrail forces every line out to disk before the call
			// it describes runs, so the file's last line names what killed the game.
			try { VRChatArchiveModPlugin.Logger.LogInfo("[VideoUrl] probe: " + step); } catch { }
			try { Core.CrashTrail.Step(step); } catch { }
		}

		private const string TrailName = "videourl";
		private static bool _crashChecked;
		/// <summary>The step the previous injection died on; "" when it completed. Surfaced so the
		/// crash names itself instead of being retold from memory.</summary>
		public static string CrashedAt = "";

		/// <summary>Said once per session, at the first injection: what the PREVIOUS one died on.
		/// Reading it consumes it, so a crash is reported once rather than every session.</summary>
		private static void ReportPreviousCrash()
		{
			if (_crashChecked) return;
			_crashChecked = true;
			try
			{
				string last = Core.CrashTrail.Check(TrailName);
				if (string.IsNullOrEmpty(last)) return;
				CrashedAt = last;
				LastStatus = "the last video injection CRASHED the game at: " + last;
				VRChatArchiveModPlugin.Logger.LogWarning(
					"[VideoUrl] THE PREVIOUS VIDEO INJECTION CRASHED THE GAME. It got as far as: " + last);
			}
			catch { }
		}

		private static string SafeName(GameObject go)
		{
			try { return go == null ? "?" : go.name; } catch { return "?"; }
		}

		private static string _lastSymbolPath = "none";
		private static int _dumpedNoUrl;   // how many "no VRCUrl" candidates were dumped this session (capped)

		// What the last injection cost, in halves. Fields rather than locals because the summary is
		// printed after the try block that measures them, and a one-second freeze deserves an address
		// rather than a guess — the same instrumentation that found the float-objects cost in minutes
		// after two wrong diagnoses.
		private static int _lastSymbolCount;
		private static double _scanCollectMs, _scanReadMs, _scanWriteMs;
		private static int _scanSymbols;

		private static MethodInfo _setGeneric;    // SetProgramVariable<T>(string, T)
		private static MethodInfo _setPlain;      // SetProgramVariable(string, Il2CppSystem.Object)
		private static Type _setPlainParam;

		private static void ResolveSetter()
		{
			_setResolved = true;
			try
			{
				foreach (var m in _udonType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (m.Name != "SetProgramVariable") continue;
					var ps = m.GetParameters();
					if (ps.Length != 2 || ps[0].ParameterType != typeof(string)) continue;
					if (m.IsGenericMethodDefinition) { if (_setGeneric == null) _setGeneric = m; }
					else if (_setPlain == null) { _setPlain = m; _setPlainParam = ps[1].ParameterType; }
				}
			}
			catch { }
		}

		// The entry points a behaviour exports — GetPrograms() hands back an ImmutableArray<string>
		// proxy, read with the same shared reader the Udon page uses for its events. Empty when
		// unreadable; callers treat that as "unknown", not as "none".
		private static HashSet<string> ExportedEvents(Il2CppObjectBase ub)
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				var mi = _udonType.GetMethod("GetPrograms");
				object arr = mi?.Invoke(ub, null);
				if (arr == null) return set;
				foreach (string n in Il2CppSeq.Strings(arr))
					if (!string.IsNullOrEmpty(n)) set.Add(n);
			}
			catch { }
			return set;
		}

		// True only when this UdonBehaviour actually synchronises (Manual/Continuous SyncMethod),
		// which is what gives it the network state SetOwner needs. Anything unclear -> false, so we
		// never SetOwner on a non-networked behaviour (the crash). USharpVideo/ProTV sync this way.
		private static bool UdonHasSync(Il2CppObjectBase ub)
		{
			try
			{
				object sm = _udonType.GetProperty("SyncMethod")?.GetValue(ub);
				if (sm == null) return false;
				string n = sm.ToString();
				return n == "Manual" || n == "Continuous";
			}
			catch { return false; }
		}

		private static bool IsNetworked(GameObject go)
		{
			try
			{
				var comps = go.GetComponents<Component>();
				if (comps == null) return false;
				foreach (var c in comps)
				{
					if (c == null) continue;
					string n;
					try { n = MenuCard.Il2CppNameOf(c); }
					catch { continue; }
					if (string.IsNullOrEmpty(n)) continue;
					// ONLY a VRCObjectSync or a VRC Pickup gives the network state Networking.SetOwner
					// needs. An UdonBehaviour alone does NOT -- SetOwner on a non-networked object makes
					// VRChat dereference network state that was never allocated and the process dies
					// (this module's crash). Same gate ObjectOrbitModule uses.
					if (n.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.IndexOf("ObjectSync", StringComparison.OrdinalIgnoreCase) >= 0) return true;
				}
			}
			catch { }
			return false;
		}

		private static void TakeOwnership(GameObject go)
		{
			try
			{
				if (go == null || !NativeGuard.Alive(go)) return;
				var me = VRC.SDKBase.Networking.LocalPlayer;
				if (me == null || !NativeGuard.Alive(me)) return;
				if (VRC.SDKBase.Networking.IsOwner(me, go)) return;
				VRC.SDKBase.Networking.SetOwner(me, go);
			}
			catch { }
		}

		private static string Short(string s)
			=> string.IsNullOrEmpty(s) ? "" : (s.Length > 90 ? s.Substring(0, 90) + "…" : s);
	}
}
