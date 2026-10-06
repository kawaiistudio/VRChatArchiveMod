using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using VRC.SDKBase;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FewTags integration: downloads the community tag database published by Fewdys
	// (github.com/Fewdys/FewTags) and renders each tagged user's tags above their
	// nameplate, exactly where the original FewTags mod puts them.
	//
	// Rendering follows the FewTags technique — clone the nameplate's own "quickStats"
	// popup (it carries a correctly-styled background + "Trust Text" TMP label), keep
	// only the label, and stack one clone per tag above the plate. Cloning a native
	// object means the plates inherit VRChat's fonts/materials and billboard for free.
	//
	// Every game-side lookup (PlayerManager → Player → VRCPlayer → PlayerNameplate)
	// goes through reflection with by-name *and* by-type fallbacks, so a member rename
	// in a future build degrades to "tags stop appearing", never a crash.
	public class FewTagsModule : IModule
	{
		public override string Name => "FewTags";

		// Plates start above the nameplate and step up. Base height + spacing are live-tunable
		// (ModConfig.FewTagsBaseY / FewTagsSpacing) so positions can be corrected in-game.
		private const float BigTextY = 344.75f;
		private const string CloneName = "ArchiveFewTagPlate";
		private static bool _loggedFirstBuild;

		private const string HeaderStr = "<b><color=#ff0000>-</color> <color=#ff7f00>F</color><color=#ffff00>e</color><color=#80ff00>w</color><color=#00ff00>T</color><color=#00ff80>a</color><color=#00ffff>g</color><color=#0000ff>s</color> <color=#8b00ff>-</color></b>";
		private const string MaliciousStr = "<b><color=#ff0000>Malicious User</color></b>";

		private sealed class TagRecord
		{
			public string Uid;
			public string[] Tags;
			public string BigText;
			public string Size;
			public bool Malicious;
			public bool BigTextActive;
		}

		private sealed class Applied
		{
			public int DbVersion;
			public int Lines;                                   // plate rows this user currently occupies
			public readonly List<GameObject> Clones = new List<GameObject>();
			// The animatable labels behind those clones. Same list lifetime as Clones: when the
			// set is rebuilt or destroyed these go with it, and a plate whose GameObject died
			// reads back Tmp == null, which VaTagsModule.Animate skips.
			public readonly List<VaTagsModule.Plate> Plates = new List<VaTagsModule.Plate>();
		}

		// FewTags encodes a tag's animation as a DOTTED PREFIX inside the tag string itself
		// (".SR.some text", ".SCROLL.some text") — there is no effect field in its JSON. Nothing
		// here ever parsed it, so the prefix was rendered as literal text and the tag never moved.
		// The effect ids are the same vocabulary VaTagsModule already implements, so the prefix is
		// simply lowercased and looked up there.
		//
		// ONLY a recognised id is stripped: a tag legitimately starting with ".Something." keeps
		// its text intact rather than losing a word to a prefix we do not understand.
		private static string SplitFxPrefix(string raw, out string fx)
		{
			fx = "none";
			if (string.IsNullOrEmpty(raw) || raw.Length < 3 || raw[0] != '.') return raw;
			int end = raw.IndexOf('.', 1);
			if (end <= 1) return raw;
			string id = raw.Substring(1, end - 1).ToLowerInvariant();
			if (!VaTagsModule.IsAnimated(id) && id != "grad") return raw;
			fx = id;
			return raw.Substring(end + 1);
		}

		// How many nameplate rows FewTags is currently using for this user. VaTags reads this so
		// its own plates stack ABOVE them — the two systems used to draw from separate fixed
		// baselines and landed on top of each other.
		public static int LinesFor(string uid)
		{
			try
			{
				if (Instance == null || string.IsNullOrEmpty(uid)) return 0;
				return Instance._applied.TryGetValue(uid, out Applied a) ? a.Lines : 0;
			}
			catch { return 0; }
		}

		private static FewTagsModule Instance;

		// --- database state ---
		private static readonly HttpClient Http = CreateClient();
		private Dictionary<string, TagRecord> _db = new Dictionary<string, TagRecord>(StringComparer.OrdinalIgnoreCase);
		private volatile Dictionary<string, TagRecord> _pendingDb;
		private int _dbVersion;
		private float _nextFetchAt;
		private bool _fetching;

		// --- per-player state ---
		private readonly Dictionary<string, Applied> _applied = new Dictionary<string, Applied>(StringComparer.OrdinalIgnoreCase);
		private int _frame;
		// Edge memory for FewTagsEnabled, so an OFF->ON flip can prime _frame and apply this frame
		// instead of waiting out the 60-frame throttle.
		private bool _wasOn;

		// --- live stats for the menu ---
		public static int RecordsLoaded { get; private set; }
		public static int TaggedHere { get; private set; }
		public static string LastFetchInfo { get; private set; } = "not fetched yet";

		public override void OnInitialize()
		{
			Instance = this;
			// ONE-TIME SANITY. Sliders that briefly had no ceiling left absurd values in some configs
			// (spacing 195, first plate 512 px up — "the tags are separated by huge gaps"). Values no
			// nameplate layout can want go back to TIGHT (0 = auto) / the default height, once, and say so.
			try
			{
				var fixes = new System.Text.StringBuilder();
				if (ModConfig.FewTagsSpacingExpanded.Value > 150f) { fixes.Append(" SpacingExpanded ").Append(ModConfig.FewTagsSpacingExpanded.Value).Append("->0(auto)"); ModConfig.FewTagsSpacingExpanded.Value = 0f; }
				if (ModConfig.FewTagsSpacing.Value > 60f) { fixes.Append(" Spacing ").Append(ModConfig.FewTagsSpacing.Value).Append("->0(auto)"); ModConfig.FewTagsSpacing.Value = 0f; }
				if (ModConfig.FewTagsBaseYExpanded.Value > 400f) { fixes.Append(" BaseYExpanded ").Append(ModConfig.FewTagsBaseYExpanded.Value).Append("->205"); ModConfig.FewTagsBaseYExpanded.Value = 205f; }
				if (ModConfig.FewTagsBaseY.Value > 250f) { fixes.Append(" BaseY ").Append(ModConfig.FewTagsBaseY.Value).Append("->119"); ModConfig.FewTagsBaseY.Value = 119.05f; }
				if (fixes.Length > 0) VRChatArchiveModPlugin.Logger.LogWarning("[FewTags] layout values reset to sane ones:" + fixes);
			}
			catch { }
			if (!ModConfig.FewTagsEnabled.Value)
			{
				VRChatArchiveModPlugin.Logger.LogInfo("[FewTags] disabled by config.");
				return;
			}
			VRChatArchiveModPlugin.Logger.LogInfo("[FewTags] armed — community tag DB by Fewdys, plates via nameplate quickStats clone.");
		}

		public override void OnUpdate()
		{
			if (!ModConfig.FewTagsEnabled.Value)
			{
				if (_applied.Count > 0) RemoveAllPlates();
				_wasOn = false;
				return;
			}

			// ON IS INSTANT (2026-09-13). The apply pass is throttled to one frame in 60, and the
			// counter is never touched on the OFF path, so switching FewTags back on left the plates
			// missing for up to a second — long enough to read as a switch that did nothing. Priming
			// the counter on the OFF->ON edge makes the first pass run on this very frame.
			if (!_wasOn) { _wasOn = true; _frame = 60; }

			// Adopt a freshly-parsed database (fetched on a worker thread).
			var pending = _pendingDb;
			if (pending != null)
			{
				_pendingDb = null;
				_db = pending;
				_dbVersion++;
				RecordsLoaded = _db.Count;
				VRChatArchiveModPlugin.Logger.LogInfo($"[FewTags] database applied: {_db.Count} active record(s).");
			}

			// Periodic refresh + the menu's "Update DB now" button.
			float now = VaClock.Now;
			if (Menu.ConsumeFewTagsRefreshRequest()) _nextFetchAt = 0f;
			if (now >= _nextFetchAt)
			{
				int minutes = Mathf.Max(1, ModConfig.FewTagsUpdateMinutes.Value);
				_nextFetchAt = now + minutes * 60f;
				StartFetch();
			}

			// Throttled apply pass.
			if (++_frame < 60) return;
			_frame = 0;
			long tApply = Core.PerfLog.Start();
			ApplyPass();
			Core.PerfLog.Slow("FewTags/apply", tApply, 8.0,
				TaggedHere + " tagged here, " + _applied.Count + " plate set(s), db " + _db.Count);
		}

		// FewTags plates used to be written once and never touched again — the module had no
		// animation tick at all, which is why every effect in its database rendered as frozen
		// text. Same 15 Hz budget and same engine as VaTagsModule.OnLateUpdate: the per-glyph
		// recomposition forces a TMP layout + mesh rebuild, so running it per frame costs
		// milliseconds a frame for motion nobody can see.
		public override void OnLateUpdate()
		{
			if (!ModConfig.FewTagsEnabled.Value) return;
			float t = VaClock.Now;
			if (t < _nextAnim) return;
			_nextAnim = t + 0.0667f;
			foreach (var a in _applied.Values)
			{
				var plates = a.Plates;
				for (int i = 0; i < plates.Count; i++)
				{
					var p = plates[i];
					if (p == null || p.Tmp == null) continue;
					// Static ids (and anything unrecognised) are finished once their text is
					// written; only the genuinely animated effects are re-composed.
					if (p.StaticBuilt && !VaTagsModule.IsAnimated(p.Fx)) continue;
					try { VaTagsModule.Animate(p, t); }
					catch (Exception ex)
					{
						if (_animFailLogged.Add(p.Fx ?? "?"))
							VRChatArchiveModPlugin.Logger.LogWarning(
								"[FewTags] effect '" + (p.Fx ?? "?") + "' threw, plate frozen: " + ex.Message);
					}
				}
			}
		}

		private float _nextAnim;
		private static readonly HashSet<string> _animFailLogged = new HashSet<string>(StringComparer.Ordinal);

		public override void OnShutdown()
		{
			RemoveAllPlates();
		}

		// ---------------------------------------------------------------- database

		private static HttpClient CreateClient()
		{
			var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			c.DefaultRequestHeaders.Add("User-Agent", "VRChatArchiveMod-FewTags/1.0");
			return c;
		}

		private void StartFetch()
		{
			if (_fetching) return;
			_fetching = true;
			string url = ModConfig.FewTagsDbUrl.Value;
			Task.Run(async () =>
			{
				try
				{
					string raw = await Http.GetStringAsync(url);
					var parsed = ParseDb(raw);
					_pendingDb = parsed; // adopted on the main thread
					LastFetchInfo = $"{parsed.Count} records @ {DateTime.Now:HH:mm:ss}";
				}
				catch (Exception e)
				{
					LastFetchInfo = "fetch failed: " + e.Message;
					VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] DB fetch failed: {e.Message}");
				}
				finally
				{
					_fetching = false;
				}
			});
		}

		// Accepts both shapes FewTags has used: a bare array, or {"records":[...]}.
		private static Dictionary<string, TagRecord> ParseDb(string raw)
		{
			var db = new Dictionary<string, TagRecord>(StringComparer.OrdinalIgnoreCase);
			using (JsonDocument doc = JsonDocument.Parse(raw))
			{
				JsonElement records;
				JsonElement root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Array) records = root;
				else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var r) && r.ValueKind == JsonValueKind.Array) records = r;
				else return db;

				foreach (JsonElement rec in records.EnumerateArray())
				{
					if (rec.ValueKind != JsonValueKind.Object) continue;
					if (!ReadBool(rec, "Active", true)) continue;
					string uid = ReadString(rec, "UserID");
					if (string.IsNullOrEmpty(uid)) continue;

					var tags = new List<string>();
					if (rec.TryGetProperty("Tag", out var tagArr) && tagArr.ValueKind == JsonValueKind.Array)
					{
						foreach (var t in tagArr.EnumerateArray())
						{
							if (t.ValueKind == JsonValueKind.String)
							{
								string s = t.GetString();
								if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
							}
						}
					}

					db[uid] = new TagRecord
					{
						Uid = uid,
						Tags = tags.ToArray(),
						BigText = ReadString(rec, "PlateBigText"),
						Size = ReadString(rec, "Size"),
						Malicious = ReadBool(rec, "Malicious", false),
						BigTextActive = ReadBool(rec, "BigTextActive", false),
					};
				}
			}
			return db;
		}

		private static string ReadString(JsonElement el, string name)
		{
			if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
			return "";
		}

		private static bool ReadBool(JsonElement el, string name, bool fallback)
		{
			if (!el.TryGetProperty(name, out var v)) return fallback;
			switch (v.ValueKind)
			{
				case JsonValueKind.True: return true;
				case JsonValueKind.False: return false;
				case JsonValueKind.String: return string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase);
				case JsonValueKind.Number: return v.TryGetInt32(out int n) && n != 0;
				default: return fallback;
			}
		}

		// ---------------------------------------------------------------- apply pass

		private void ApplyPass()
		{
			if (_db.Count == 0) { TaggedHere = 0; return; }

			int taggedHere = 0;
			try
			{
				// REUSE THE ROSTER VaTags ALREADY BUILT (2026-09-13).
				//
				// This walked all 40 players and called UserIdOf(player) on each — an APIUser->id
				// reflection read per player, every pass — only to find zero matches in a normal
				// instance (measured 79 ms for 0 tags). VaTags' roster already holds each player's
				// UserId and Player object, resolved once and cached, so iterating it turns the
				// per-player reflection into a dictionary lookup. Falls back to the direct
				// enumeration only when the roster is empty (VaTags idle), so FewTags never depends
				// on VaTags being switched on.
				var roster = VaTagsModule.Roster;
				if (roster != null && roster.Count > 0)
				{
					lock (roster)
					{
						for (int i = 0; i < roster.Count; i++)
						{
							var e = roster[i];
							if (e == null || e.Player == null) continue;
							string uid = e.UserId;
							if (string.IsNullOrEmpty(uid)) continue;
							if (!_db.TryGetValue(uid, out TagRecord rec)) continue;

							taggedHere++;
							if (_applied.TryGetValue(uid, out Applied a) && a.DbVersion == _dbVersion && CloneAlive(a)) continue;

							BuildPlates(e.Player, uid, rec);
						}
					}
				}
				else
				{
					foreach (object player in EnumeratePlayers())
					{
						if (player == null) continue;
						string uid = UserIdOf(player);
						if (string.IsNullOrEmpty(uid)) continue;
						if (!_db.TryGetValue(uid, out TagRecord rec)) continue;

						taggedHere++;
						if (_applied.TryGetValue(uid, out Applied a) && a.DbVersion == _dbVersion && CloneAlive(a)) continue;

						BuildPlates(player, uid, rec);
					}
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogError($"[FewTags] apply pass threw: {e}");
			}
			TaggedHere = taggedHere;
			PruneDead();
		}

		// A rebuilt nameplate (avatar change, instance travel) destroys our clones with it.
		//
		// EVERY clone, not just the first (2026-09-13). This used to test Clones[0] alone: when the
		// first plate died and the others did not — VRChat rebuilding part of a nameplate, a throw
		// part-way through a build — the entry was declared dead and dropped from _applied, which is
		// the ONLY teardown ledger, leaving the surviving plates parented to the nameplate forever
		// and unreachable by RemoveAllPlates(). A set counts as alive while ANY clone survives, and
		// the survivors are destroyed before the entry is forgotten.
		private static bool CloneAlive(Applied a)
		{
			if (a == null || a.Clones.Count == 0) return false;
			for (int i = 0; i < a.Clones.Count; i++)
			{
				try { if (a.Clones[i] != null) return true; } catch { }
			}
			return false;
		}

		/// <summary>Destroys whatever is left of a set, then forgets it. Safe on already-destroyed
		/// clones — this is the only way an entry may leave _applied.</summary>
		private static void DestroyEntry(Applied a)
		{
			if (a == null) return;
			for (int i = 0; i < a.Clones.Count; i++)
			{
				try { var go = a.Clones[i]; if (go != null) UnityEngine.Object.Destroy(go); } catch { }
			}
			a.Clones.Clear();
		}

		private void PruneDead()
		{
			List<string> dead = null;
			foreach (var kv in _applied)
			{
				if (CloneAlive(kv.Value)) continue;
				(dead ??= new List<string>()).Add(kv.Key);
			}
			if (dead == null) return;
			// Destroy first: a "dead" set can still hold live clones (see CloneAlive) and dropping the
			// key without destroying them is exactly how plates end up stranded on a nameplate.
			foreach (string uid in dead)
			{
				if (_applied.TryGetValue(uid, out var a)) DestroyEntry(a);
				_applied.Remove(uid);
			}
		}

		private void RemoveAllPlates()
		{
			foreach (var a in _applied.Values)
			{
				foreach (GameObject go in a.Clones)
				{
					try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
				}
			}
			_applied.Clear();
			TaggedHere = 0;
		}

		private void BuildPlates(object player, string uid, TagRecord rec)
		{
			// Tear down whatever we previously built for this user.
			if (_applied.TryGetValue(uid, out Applied old))
			{
				foreach (GameObject go in old.Clones)
				{
					try { if (go != null) UnityEngine.Object.Destroy(go); } catch { }
				}
				_applied.Remove(uid);
			}

			if (!ResolveNameplate(player, out GameObject quickStats, out Transform contents))
			{
				if (!_loggedFirstBuild)
				{
					_loggedFirstBuild = true;
					VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] could not resolve nameplate for a tagged user ({rec.Tags.Length} tag(s)) — quickStats/contents not found.");
				}
				return;
			}

			float baseY = EffectiveBaseY();
			float spacing = EffectiveSpacing();

			var applied = new Applied { DbVersion = _dbVersion };

			// REGISTERED BEFORE THE FIRST PLATE EXISTS (2026-09-13). The entry used to be written to
			// _applied only at the very end, behind `if (applied.Clones.Count > 0)`. Anything that
			// threw part-way through the build below — a MakePlate failure, an effect-engine throw —
			// is swallowed by ApplyPass's catch, and every clone instantiated up to that point was
			// left parented to the nameplate with nothing tracking it: invisible to RemoveAllPlates()
			// and to PruneDead(), i.e. a plate the OFF switch could never remove. Registering the
			// (initially empty) record up front makes _applied the teardown authority from the first
			// instantiation onward. The tail below still records Lines and drops it again if the
			// build produced nothing.
			_applied[uid] = applied;

			// Bottom-up stack: header plate first, then one plate per tag.
			int line = 0;
			if (ModConfig.FewTagsShowHeader.Value)
			{
				GameObject header = MakePlate(quickStats, contents, baseY + line * spacing,
					rec.Malicious ? MaliciousStr : HeaderStr);
				if (header != null) { applied.Clones.Add(header); line++; }
			}

			// No ceiling: the owner wants every tag a user has. The setting is the only cap (min 1).
			int max = Mathf.Max(1, ModConfig.FewTagsMaxTagsPerUser.Value);
			for (int i = 0; i < rec.Tags.Length && i < max; i++)
			{
				string tag = rec.Tags[i];
				if (string.IsNullOrWhiteSpace(tag)) continue;
				if (IsAbusive(tag)) continue;   // third-party DB, anyone can write to it
				// Consume the ".FX." prefix BEFORE the text reaches the label, or it renders as
				// part of the tag (".SR.VRChat Archive is …" was showing up verbatim in game).
				string body = SplitFxPrefix(tag, out string fx);
				if (string.IsNullOrWhiteSpace(body)) continue;
				GameObject plate = MakePlate(quickStats, contents, baseY + line * spacing, body);
				if (plate != null)
				{
					applied.Clones.Add(plate);
					line++;
					// Hand the label to the VaTags effect engine instead of leaving it frozen.
					// EXCEPT when the tag carries its own rich-text markup: the engine splits the
					// body per GLYPH and wraps each one in its own span, so a body containing
					// "<color=#ff0000>" would be chopped mid-tag and render as visible garbage.
					// VaTags' own tags are validated plain text; FewTags' are third-party and
					// frequently pre-styled, so those stay static — which is correct for them.
					var lbl = LastPlateLabel;
					if (lbl != null && body.IndexOf('<') < 0)
					{
						var p = VaTagsModule.NewPlate(plate, lbl,
							new VaTagsModule.VaTag { Text = body, Fx = fx });
						applied.Plates.Add(p);
						// Compose the first frame NOW: MakePlate wrote the plain body, so without
						// this the tag shows unstyled until the next 15 Hz tick.
						try { VaTagsModule.Animate(p, VaClock.Now, force: true); } catch { }
					}
				}
			}

			if (ModConfig.FewTagsShowBigPlates.Value && rec.BigTextActive && !string.IsNullOrWhiteSpace(rec.BigText))
			{
				// Same dotted prefix as the tag lines, and it has to be consumed BEFORE Size is
				// prepended: once the "<size=..>" span is in front, the string no longer starts
				// with '.' and the prefix would survive into the label as visible text.
				string bigBody = SplitFxPrefix(rec.BigText, out string bigFx);
				GameObject big = MakePlate(quickStats, contents, BigTextY, (rec.Size ?? "") + bigBody, hideBackground: true);
				if (big != null)
				{
					applied.Clones.Add(big);
					// Not animated when it carries markup — including the Size span this plate
					// always prepends, which is exactly the per-glyph hazard described above.
					var blbl = LastPlateLabel;
					if (blbl != null && bigFx != "none" && bigBody.IndexOf('<') < 0
						&& string.IsNullOrEmpty(rec.Size))
					{
						var bp = VaTagsModule.NewPlate(big, blbl,
							new VaTagsModule.VaTag { Text = bigBody, Fx = bigFx });
						applied.Plates.Add(bp);
						try { VaTagsModule.Animate(bp, VaClock.Now, force: true); } catch { }
					}
				}
			}

			if (applied.Clones.Count > 0)
			{
				applied.Lines = line;   // already registered in _applied above
				if (!_loggedFirstBuild)
				{
					_loggedFirstBuild = true;
					VRChatArchiveModPlugin.Logger.LogInfo($"[FewTags] rendered {applied.Clones.Count} plate(s) for a tagged user (baseY={baseY}, spacing={spacing}).");
				}
			}
			else
			{
				// Nothing was built (every tag filtered, or MakePlate never succeeded): drop the
				// placeholder so an empty record cannot masquerade as "this user already has plates"
				// and block a later rebuild.
				_applied.Remove(uid);
			}
		}

		// Clones the nameplate's quickStats popup, keeps only its "Trust Text" label and
		// turns it into one floating tag line.
		// The FewTags database is third party and open to anyone, so it carries slurs aimed at real
		// people. This filters what THIS client draws above someone's head — it does not and cannot
		// change the database. Matching is done on a letter-only, lowercase form so that spacing,
		// punctuation and l33t-speak padding cannot walk a slur straight past the check.
		private static readonly string[] Abusive =
		{
			"nigger", "nigga", "faggot", "tranny", "kike", "chink", "retard", "coon", "spic",
		};

		private static bool IsAbusive(string tag)
		{
			try
			{
				if (!ModConfig.FewTagsFilterSlurs.Value || string.IsNullOrEmpty(tag)) return false;

				var sb = new System.Text.StringBuilder(tag.Length);
				foreach (char c in tag)
				{
					char l = char.ToLowerInvariant(c);
					if (l >= 'a' && l <= 'z') sb.Append(l);
					else if (l == '0') sb.Append('o');
					else if (l == '1' || l == '!') sb.Append('i');
					else if (l == '3') sb.Append('e');
					else if (l == '4' || l == '@') sb.Append('a');
					else if (l == '5' || l == '$') sb.Append('s');
					else if (l == '7') sb.Append('t');
				}
				string flat = sb.ToString();
				if (flat.Length == 0) return false;

				foreach (string bad in Abusive)
					if (flat.IndexOf(bad, StringComparison.Ordinal) >= 0) return true;
			}
			catch { }
			return false;
		}

		// The label ResolveNameplate anchored on, remembered so MakePlate does not have to hunt
		// again — and, most importantly, so it does not fall back to a hardcoded "Trust Text".
		// "root/child/leaf" — used to reject anchors nested under a Group / Icon / Bubble sub-module.
		private static string ChainName(Transform t)
		{
			var sb = new System.Text.StringBuilder();
			int n = 0;
			while (t != null && n++ < 8) { if (sb.Length > 0) sb.Insert(0, '/'); sb.Insert(0, t.name); t = t.parent; }
			return sb.ToString();
		}

		private static bool _makePlateFailLogged;
		private static bool _plateShapeLogged;

		private static int Depth(Transform t, Transform root) { int d=0; while (t!=null && t!=root && d<10) { d++; t=t.parent; } return d; }


		private static bool Log(string msg) { try { VRChatArchiveModPlugin.Logger.LogWarning(msg); } catch { } return true; }

		internal static string LastAnchorName { get; private set; } = "Trust Text";

		// Which nameplate layout this build uses. The Fragments layout (ExpandedInfo) has much
		// taller plates, so it needs its own spacing — 78 in the reference against 28 for the old
		// Quick Stats layout. Using one number for both is what stacked every tag on top of the
		// next and over the player's name.
		internal static bool IsExpandedInfo { get; private set; }

		// The font size of the label we clone, read off the real nameplate. Spacing has to clear
		// the text, and the text is whatever THIS build's layout uses — on the Fragments layout the
		// label we anchor to is a good deal taller than the old Quick Stats one, which is why a
		// fixed 78 still left the rows sitting on top of each other.
		internal static float AnchorFontSize { get; private set; }

		// Where the FIRST plate sits. Same reasoning as the spacing: the Fragments plate is taller,
		// so the legacy height started the stack inside the nameplate rather than above it.
		internal static float EffectiveBaseY()
		{
			try
			{
				return IsExpandedInfo
					? ModConfig.FewTagsBaseYExpanded.Value
					: ModConfig.FewTagsBaseY.Value;
			}
			catch { return 205f; }
		}

		internal static float EffectiveSpacing()
		{
			try
			{
				float cfg = IsExpandedInfo
					? ModConfig.FewTagsSpacingExpanded.Value
					: ModConfig.FewTagsSpacing.Value;
				// SELF-CALIBRATING FLOOR. A line has to be at least ~1.7x its own font size clear of
				// the next one or the glyphs collide. Measuring beats guessing: the number comes
				// from the label actually on screen, so a future layout change corrects itself.
				// 0 (the default now) = TIGHT: exactly that floor, lines stacked like text.
				float floor = AnchorFontSize > 0f ? AnchorFontSize * 1.7f : (IsExpandedInfo ? 78f : 28f);
				return cfg <= 0f ? floor : Mathf.Max(cfg, floor);
			}
			catch { return 78f; }
		}

		// Nameplate furniture that must not ride along on a tag plate — the reference's
		// ObjectsToDestroy (Util/Utils.cs L16).
		private static readonly string[] PlateJunk =
		{
			"Trust Icon", "Performance Icon", "Performance Text", "Friend Anchor Stats", "Reason",
			"Shared Connections Icon", "Shared Connections Text", "Spacing", "Earmuffs Icon",
			"Age Verification Icon", "Performance Rank Icon", "Group Icon",
		};

		// The TMP label MakePlate last wrote to. Callers that need to animate the text must use
		// THIS rather than searching the clone again: the anchor name is build-dependent
		// ("Trust Text" on the old layout, "GroupName" on the current one) and the branches around
		// it are deactivated, so a re-search matched nothing and every tag silently lost its
		// animation. Set on every successful build, read immediately after the call.
		internal static TextMeshProUGUI LastPlateLabel { get; private set; }

		internal static GameObject MakePlate(GameObject quickStats, Transform contents, float y, string richText, bool hideBackground = false)
		{
			try
			{
				// STAY IN THE MODEL'S COORDINATE FRAME.
				//
				// `y` here is measured in the old plate's own local units — the value that used to
				// stack tags neatly above the nameplate. Parenting the clone to a DIFFERENT
				// transform (the positioner instead of the old contents container) changes the
				// coordinate frame, so "y = 91" starts meaning something like "91 metres" and the
				// plate ends up somewhere off-screen — invisible, but still constructed. Exactly
				// what you see.
				//
				// So the clone keeps the model's OWN local position, rotation AND scale, and then
				// we only add "y" on top — a nudge in the same units the model itself was placed
				// in, which is what the old code was really doing without saying so.
				GameObject clone = UnityEngine.Object.Instantiate(quickStats, contents);
				if (clone == null) return null;
				clone.name = CloneName;

				// ABSOLUTE, x = 0 — the reference's exact line (Plate.cs L107/L118):
				//     _gameObject.transform.localPosition = new Vector3(0f, position, 0);
				// Adding the model's own localPosition on top (what this did before) double-counts
				// the offset the panel already carries and pushes the plate off to one side.
				clone.transform.localPosition = new Vector3(0f, y, 0f);

				// THE CANVASGROUP IS WHY NOTHING SHOWED. VRChat fades the nameplate with a
				// CanvasGroup; the clone inherits it, complete with whatever alpha the original had
				// at that instant — frequently 0. The plate is then built, positioned and textured
				// perfectly, and drawn fully transparent. DestroyChildren in the reference
				// (Util/Utils.cs L653-660) disables it for exactly this reason.
				try
				{
					var cg = clone.GetComponent<CanvasGroup>();
					if (cg != null) cg.enabled = false;
				}
				catch { }

				// The group pill is the visible body of the plate on the ExpandedInfo layout, and it
				// ships inactive. The reference switches it on by name.
				try
				{
					Transform pill = FindDeep(clone.transform, "GroupPill");
					if (pill != null) pill.gameObject.SetActive(true);
				}
				catch { }

				// Icons that belong to the real nameplate and mean nothing on a tag plate. Left in
				// place they draw over the text and widen the row. Names from the reference's
				// ObjectsToDestroy list.
				try
				{
					foreach (string junk in PlateJunk)
					{
						Transform j = FindDeep(clone.transform, junk);
						if (j != null) j.gameObject.SetActive(false);
					}
				}
				catch { }

				// LOOK FOR THE ANCHOR WE ACTUALLY RESOLVED, then any TMP as last resort. This used
				// to search hardcoded "Trust Text" — and, on a build that renamed it, quietly
				// destroyed every clone: quickStats/contents came back fine, the plate was cloned,
				// then Destroy(clone); return null. "resolve-failed=0, plates=0" was the visible
				// result: not a resolve failure, a build failure with no message.
				Transform trust = FindDeep(clone.transform, LastAnchorName);
				if (trust == null)
				{
					try { trust = clone.GetComponentInChildren<TextMeshProUGUI>(true)?.transform; } catch { }
				}
				if (trust == null)
				{
					_makePlateFailLogged = _makePlateFailLogged || Log("[FewTags] MakePlate: no label under the cloned plate.");
					LastPlateLabel = null;
					UnityEngine.Object.Destroy(clone);
					return null;
				}

				// Keep only the branch that carries the label.
				for (int i = 0; i < clone.transform.childCount; i++)
				{
					Transform child = clone.transform.GetChild(i);
					bool keep = child == trust || trust.IsChildOf(child);
					if (!keep) child.gameObject.SetActive(false);
				}
				trust.gameObject.SetActive(true);

				var tmp = trust.GetComponent<TextMeshProUGUI>();
				if (tmp == null)
				{
					UnityEngine.Object.Destroy(clone);
					return null;
				}
				// SetTextSafe, from the reference (Util/Utils.cs L334-344). Each of these can make a
				// correctly-built label render as nothing:
				//   autoSizing on a rect this small shrinks the text toward zero;
				//   an overflow mode of Truncate/Ellipsis clips it away entirely.
				tmp.richText = true;
				// overrideColorTags=true makes TMP IGNORE every <color> span and paint the whole label
				// in tmp.color — which turns the member gradient (and rainbow/scroll/cylon/…) into one
				// flat colour. VRChat's own nameplate label ships with this ON (and a game update can
				// flip it), and the clone inherits it, so force it off or per-character colour never shows.
				tmp.overrideColorTags = false;
				// A tinted base colour multiplies the <color> vertices and mutes the gradient; the spans
				// carry the real colours, so the label's own colour must be plain white.
				tmp.color = Color.white;
				tmp.enableAutoSizing = false;
				tmp.overflowMode = TextOverflowModes.Overflow;
				tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
				// CENTRING, the reference's way (Util/Utils.cs L346-355). A HorizontalLayoutGroup two
				// levels up drives the row's alignment; without this the label keeps the nameplate's
				// own left-ish anchoring and the stack looks ragged instead of centred over the head.
				try
				{
					var gp = tmp.transform.parent != null ? tmp.transform.parent.parent : null;
					var lg = gp != null ? gp.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>() : null;
					if (lg != null) lg.childAlignment = TextAnchor.MiddleCenter;
				}
				catch { }
				tmp.text = richText;
				// Draw over the world only if Nameplate ESP is active
				try { tmp.isOverlay = ModConfig.NameplateEsp != null && ModConfig.NameplateEsp.Value; } catch { }
				LastPlateLabel = tmp;

				if (hideBackground)
				{
					// Big plates render as free text: disable every graphic that isn't the label.
					var graphics = clone.GetComponentsInChildren<UnityEngine.UI.Graphic>(true);
					for (int i = 0; i < graphics.Length; i++)
					{
						var g = graphics[i];
						if (g == null) continue;
						if (g.transform == trust) continue;
						g.enabled = false;
					}
				}

				clone.SetActive(true);
				return clone;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] plate build failed: {e.Message}");
				return null;
			}
		}

		internal static Transform FindDeep(Transform root, string name)
		{
			if (root == null) return null;
			try
			{
				if (root.name == name) return root;
				for (int i = 0; i < root.childCount; i++)
				{
					Transform hit = FindDeep(root.GetChild(i), name);
					if (hit != null) return hit;
				}
			}
			catch { }
			return null;
		}

		// ---------------------------------------------------------------- game access (reflection)

		private static Type _playerType;
		private static Il2CppSystem.Type _nameplateIl2cppType;
		private static Il2CppSystem.Type _playerIl2cppType;
		private static MethodInfo _tryCastPlayer;
		private static bool _typesResolved;

		private static void ResolveTypes()
		{
			if (_typesResolved) return;
			_typesResolved = true;
			try
			{
				var asm = Assembly.Load("Assembly-CSharp");
				_playerType = asm.GetType("VRC.Player");
				if (_playerType != null)
				{
					_playerIl2cppType = Il2CppInterop.Runtime.Il2CppType.From(_playerType);
					_tryCastPlayer = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
						.GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)
						?.MakeGenericMethod(_playerType);
				}

				// The nameplate is no longer a named member of VRCPlayer on this build; the type still
				// exists, so it is reached as a COMPONENT in the player's hierarchy instead.
				// THE NAMESPACE IS A GUESS, so try the plausible ones and accept that all may miss.
				// Nothing depends on this any more — ResolveNameplate walks to "Trust Text" from the
				// player when the type is unavailable — but when it does resolve it saves the walk.
				// ISOLATED ON PURPOSE. The managed type still exists while the il2cpp class behind it
				// does not, so asking for its il2cpp type throws — and letting that escape aborted the
				// whole resolve, taking VRC.Player down with it and leaving the tags unable to render
				// for a type nothing actually depends on.
				foreach (string cand in new[] { "PlayerNameplate", "VRC.UI.PlayerNameplate", "VRC.PlayerNameplate" })
				{
					try
					{
						var npType = asm.GetType(cand);
						if (npType == null) continue;
						_nameplateIl2cppType = Il2CppInterop.Runtime.Il2CppType.From(npType);
						break;
					}
					catch { }
				}
				if (_nameplateIl2cppType == null)
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[FewTags] PlayerNameplate type not resolvable by name on this build — "
						+ "plates will be anchored structurally instead.");
			}
			catch (Exception e)
			{
				// The chain matters here: the outer TargetInvocationException says nothing, and what is
				// underneath is the difference between "the class is gone" and "one member of it is".
				var chain = new System.Text.StringBuilder();
				for (Exception x = e; x != null; x = x.InnerException)
					chain.Append(chain.Length > 0 ? " <- " : "").Append(x.GetType().Name).Append(": ").Append(x.Message);
				VRChatArchiveModPlugin.Logger.LogWarning($"[FewTags] type resolve failed: {chain}");
			}
			if (_playerType == null || _tryCastPlayer == null)
				VRChatArchiveModPlugin.Logger.LogWarning("[FewTags] VRC.Player type unavailable — tags cannot render this build.");
		}

		// Enumerate players via the SDK-stable VRChatArchiveMod.Core.VaPlayers.All() (same source as ESP),
		// then map each to its VRC.Player component so UserIdOf/ResolveNameplate can read the
		// APIUser id and the nameplate. No dependency on the obfuscated PlayerManager, which
		// this build renamed out of the VRC namespace.
		// internal: shared with VaTagsModule (players, user ids, nameplates, reflection kit).
		// CACHED, one frame at a time.
		//
		// Three modules call this every apply pass — InstancePanels, VaTags, and FewTags's own —
		// and each call did, per player: three GetComponent lookups then a reflection Invoke
		// through Il2CppObjectBase.TryCast. On a full instance the profiler measured
		// InstancePanels=724 ms/s, VaTags=467 ms/s, FewTags=60 ms/s — a full second of the game's
		// time each second spent recomputing the same list.
		//
		// The result does not change between the modules' passes within one frame, so this hands
		// back the cached list unless the frame has moved on.
		private static readonly List<object> _playerCache = new List<object>(64);
		private static int _playerCacheFrame = -1;

		internal static IEnumerable<object> EnumeratePlayers()
		{
			if (_playerCacheFrame == Time.frameCount) return _playerCache;
			_playerCacheFrame = Time.frameCount;
			_playerCache.Clear();

			ResolveTypes();
			if (_playerIl2cppType == null) return _playerCache;

			var players = VRChatArchiveMod.Core.VaPlayers.All();
			if (players == null) return _playerCache;
			int count;
			try { count = players.Count; } catch { return _playerCache; }

			for (int i = 0; i < count; i++)
			{
				try
				{
					VRCPlayerApi api = players[i];
					if (api == null) continue;
					// A NULL CHECK IS NOT A LIVENESS CHECK. VRCPlayerApi is not a UnityEngine.Object, so
					// `api == null` is the plain managed test and says nothing about the il2cpp object
					// behind it. Asking a stale handle for its gameObject sends Il2CppObjectPool into a
					// dead pointer, and that is an access violation inside the proxy -- no exception, no
					// catch, no process. It only began to bite once PlayerManager was recovered as the
					// RIGHT class and this list started carrying real entries again.
					if (!Core.NativeGuard.Alive(api)) continue;
					GameObject go = api.gameObject;
					if (go == null || !Core.NativeGuard.Alive(go)) continue;

					Component comp = go.GetComponentSafe(_playerIl2cppType);
					if (comp == null)
					{
						// Only walk the hierarchy when the direct hit failed. The 3-lookup fallback
						// was paying the price EVERY player: two extra il2cpp queries multiplied by
						// forty is what put VaTags at 467 ms/s.
						comp = go.GetComponentInChildrenSafe(_playerIl2cppType, true)
							?? go.GetComponentInParentSafe(_playerIl2cppType);
						if (comp == null) continue;
					}

					// TryCast, NOT reflection.Invoke: same result, without the method-lookup and
					// argument-boxing cost that made this the biggest single item in the profile.
					var b = comp as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
					object player = b != null ? _tryCastPlayerDirect(b) : (object)comp;
					if (player != null) _playerCache.Add(player);
				}
				catch { }
			}
			return _playerCache;
		}

		// TryCast<T>() with the target type only known at runtime. Built once from _playerType and
		// invoked as a plain delegate; the overhead is a virtcall, not a full reflection Invoke.
		private static Func<Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, object> _tryCastPlayerDirect =
			b => { try { return _tryCastPlayer?.Invoke(b, null); } catch { return null; } };

		internal static string UserIdOf(object player)
		{
			try
			{
				object apiUser = GetMemberByTypeName(player, "APIUser", "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				if (apiUser == null) return null;
				object id = GetMember(apiUser, "id");
				return id as string;
			}
			catch { return null; }
		}

		// player → VRCPlayer → PlayerNameplate → (quickStats template, contents parent).
		// WHERE IT GAVE UP, not just that it did.
		//
		// This returns false from four different places and said which from none of them, so
		// "nameplate-resolve-failed=1" could mean the player object was wrong, the nameplate member
		// moved with a game update, or the fallback could not find "Trust Text". Those need
		// completely different fixes and the log could not tell them apart — which is how a
		// regression here turned into a guessing game.
		//
		// One line, once per distinct reason, then silent.
		internal static string LastResolveFailure { get; private set; } = "";
		private static string _loggedFailure = "";

		private static bool Failed(string why)
		{
			LastResolveFailure = why;
			if (_loggedFailure != why)
			{
				_loggedFailure = why;
				try { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] resolve failed: " + why); } catch { }
			}
			return false;
		}

		// Every label the trust text has been called. "Trust Text" is the classic one; the rest are
		// what a Fragment-based nameplate is likely to use. Order matters only for speed.
		// The plate's OWN label — not a bolted-on sub-module. Live log said the fallback picked
		// "GroupName" (the little group pill), which cloned a pill and parented it wrong; the plates
		// went out of sight because their MODEL was wrong, not their position. So the label search
		// prefers actual player-name / rank labels first, and everything with "Group" in it is
		// explicitly avoided at the end.
		private static readonly string[] AnchorNames =
		{
			// what the plate is really called on this build (Fragments layout, seen 2026-08-28)
			"Text_PlayerName", "PlayerNameText", "Text_Name", "NameText",
			// classic FewTags landmark
			"Trust Text", "TrustText", "Text_TrustRank", "Trust", "Text_Trust",
			// legacy shapes
			"Text_Status", "StatusText",
		};
		// Labels that BELONG to something else and must not be chosen as the plate anchor.
		private static readonly string[] AnchorDeny =
		{
			"Group",   // GroupName, GroupPill, etc.
			"Icon",
			"Bubble",
		};

		// ONE DUMP, THE FIRST TIME IT MATTERS.
		//
		// A rename can only be fixed by knowing the new name, and nothing in the mod could see the
		// nameplate's children. This writes them out once — bounded, so a deep hierarchy cannot turn
		// a diagnostic into a freeze — and then never again for the session.
		private static bool _dumpedSubtree;

		// OFF, AND IT STAYS OFF IN A PLAYER'S SESSION.
		//
		// Measured in production on 2026-09-18: this probe took 13 632 ms for ONE nameplate. It runs
		// Resources.FindObjectsOfTypeAll<TMP_Text>() over the whole scene, and in a busy world that is
		// tens of thousands of labels — seconds of frozen game, on the main thread, inside the plate
		// pass. The VaTags circuit breaker then did exactly what it was built to do and switched tag
		// plates off for the rest of the session.
		//
		// So the freeze was never the FEATURE. It was this diagnostic, left in the hot path after it had
		// already answered its question (nameplates live under the global NameplateManager, not under the
		// player). A probe that costs thirteen seconds is a thing you arm deliberately when you are
		// investigating, never something a player pays for because a lookup missed.
		internal static bool DeepProbe;

		private static void DumpPlayerSubtreeOnce(Transform root)
		{
			if (_dumpedSubtree || !DeepProbe) return;

			// NOT WHILE YOU ARE ALONE. This fired on the first failure, which is always in the first
			// seconds of a session — before anyone else has joined. It then printed "players in the
			// instance: 1", marked itself done, and stayed silent for the rest of the session, so a
			// world with 29 people in it was never looked at. Exactly the same one-shot mistake the
			// VaTags summary line had.
			//
			// A probe for OTHER players' plates is worthless with no other players, so it waits.
			try
			{
				int here = 0;
				foreach (var e in VaTagsModule.Roster) if (e != null) here++;
				if (here < 2) return;   // stay armed; try again once somebody is around
			}
			catch { }

			_dumpedSubtree = true;
			try
			{
				// THE NAMEPLATE IS NOT UNDER THE PLAYER ANY MORE. The first dump of this proved it:
				// the player's subtree is the avatar rig and nothing else — Armature, Hips, Spine,
				// the mirror and shadow clones — and 400 nodes of bones with no plate in sight. So
				// walking down from the player can never find it, whatever the anchor is called.
				//
				// This looks for it where it actually lives instead: every object in the scene whose
				// name mentions a nameplate, with its path and its labels. One scene-wide scan, once
				// per session, on a path that is already broken — the cost buys the only thing that
				// can unblock this.
				var sb = new System.Text.StringBuilder(8192);
				sb.Append("[Nameplate] scene-wide probe — where nameplates actually live:\n");

				// SEARCH BY THE TEXT, NOT BY THE OBJECT NAME.
				//
				// The first scene-wide pass looked for objects called *Nameplate* and found only the
				// SETTINGS menu — "Nameplate Opacity", "Nameplate Scale". No player plate is named
				// that any more, so the name is useless as a search key.
				//
				// A plate is identifiable by what it SAYS: it carries a label holding a player's
				// display name. That survives every rename, and it is the same property the plates
				// are cloned from. So collect the display names in the instance and find whatever
				// label is showing one.
				var names = new List<string>();
				try
				{
					foreach (var e in VaTagsModule.Roster)
						if (e != null && !string.IsNullOrWhiteSpace(e.Name)) names.Add(e.Name);
				}
				catch { }
				sb.Append("  players in the instance: ").Append(names.Count);
				if (names.Count <= 1) sb.Append("  <-- ALONE: there may simply be no other plate to find");
				sb.Append('\n');

				int found = 0;
				foreach (var tmp in Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>())
				{
					if (tmp == null || found >= 8) continue;
					string txt;
					try { txt = tmp.text ?? ""; } catch { continue; }
					if (txt.Length == 0 || txt.Length > 64) continue;

					bool isName = false;
					for (int i = 0; i < names.Count; i++)
						if (txt.IndexOf(names[i], StringComparison.Ordinal) >= 0) { isName = true; break; }
					if (!isName) continue;

					Transform t;
					try { t = tmp.transform; } catch { continue; }
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					// The plate is the label's ancestry, so climb a little to show the whole card.
					for (int up = 0; up < 3 && t.parent != null; up++) t = t.parent;

					found++;
					sb.Append("\n--- ").Append(PathOf(t)).Append("  (active=");
					try { sb.Append(t.gameObject.activeInHierarchy); } catch { sb.Append('?'); }
					sb.Append(")\n");
					int c = 0;
					Walk(t, 1, sb, ref c);
				}

				if (found == 0) sb.Append("  nothing in the scene is named *Nameplate*.\n");
				VRChatArchiveModPlugin.Logger.LogWarning(sb.ToString());
			}
			catch (Exception e)
			{
				try { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] probe failed: " + e.Message); } catch { }
			}
		}

		private static string PathOf(Transform t)
		{
			try
			{
				var parts = new List<string>();
				for (Transform c = t; c != null && parts.Count < 12; c = c.parent) parts.Add(c.name);
				parts.Reverse();
				return string.Join("/", parts);
			}
			catch { return "?"; }
		}

		private static void Walk(Transform t, int depth, System.Text.StringBuilder sb, ref int n)
		{
			if (t == null || n >= 120 || depth > 6) return;
			n++;
			sb.Append(' ', depth * 2).Append(t.name);

			// The text is what identifies the anchor, so any label carries its content.
			try
			{
				var tmp = t.GetComponent<TMPro.TMP_Text>();
				if (tmp != null)
				{
					string txt = tmp.text ?? "";
					if (txt.Length > 24) txt = txt.Substring(0, 24) + "…";
					sb.Append("   [TMP: \"").Append(txt).Append("\"]");
				}
			}
			catch { }

			sb.Append('\n');
			for (int i = 0; i < t.childCount && n < 400; i++) Walk(t.GetChild(i), depth + 1, sb, ref n);
		}

		internal static bool ResolveNameplate(object player, out GameObject quickStats, out Transform contents)
		{
			quickStats = null;
			contents = null;
			try
			{
				if (player == null) return Failed("the player object was null");

				// FewTags-Rewrite V2 recipe. Verified on THIS VRChat build by reading the
				// reference source: the field is "NameplateContainer", a GameObject exposed on
				// VRCPlayer under some obfuscated name — no lookup by type name required, we scan
				// the VRCPlayer's GameObject fields and take the one whose object is called
				// "NameplateContainer".
				GameObject container = FindNameplateContainer(player);
				if (container == null)
				{
					// Last resort so an unknown build still gets some diagnostic instead of silence.
					GameObject pgo = AsGameObject(player)
						?? AsGameObject(GetMemberByTypeName(player, "VRCPlayer", "_vrcplayer",
							"prop_VRCPlayer_0", "field_Private_VRCPlayer_0"));
					if (pgo == null) return Failed("the player object has no GameObject behind it");
					DumpPlayerSubtreeOnce(pgo.transform);
					return Failed("no NameplateContainer field on VRCPlayer — subtree dumped");
				}
				Transform from = container.transform;

				// EXACT PATHS AND PARENTING FROM FewTags-Rewrite V2/Plate/Plate.cs L67-89. The
				// author was explicit: GetNameplateContainer returns the HOST, not the thing to
				// clone. What gets cloned is the Quick Stats / ExpandedInfo PANEL, and the clone is
				// parented to that PANEL'S OWN PARENT — a sibling of it — not to the container and
				// not to the panel itself. Parenting to the container (my previous mistake) is what
				// put the plate in the wrong coordinate frame and off-screen.
				const string PATH_OLD = "PlayerNameplate/Canvas/NameplateGroup/Nameplate/Contents/Quick Stats";
				const string PATH_NEW = "PlayerNameplate/Canvas/NameplateGroup/NameplateFragment/ExpandedInfo";

				// Reference order: Quick Stats first, ExpandedInfo only if that is null.
				Transform panel = from.Find(PATH_OLD);
				IsExpandedInfo = false;
				if (panel == null)
				{
					panel = from.Find(PATH_NEW);
					IsExpandedInfo = panel != null;
				}
				if (panel == null)
				{
					DumpPlayerSubtreeOnce(from);
					return Failed("neither Quick Stats nor ExpandedInfo exists under NameplateContainer — subtree dumped");
				}
				Transform parent = panel.parent;
				if (parent == null)
				{
					DumpPlayerSubtreeOnce(from);
					return Failed("the plate panel has no parent — subtree dumped");
				}

				quickStats = panel.gameObject;   // the MODEL to Instantiate
				contents = parent;               // the PARENT to hang the clone from (panel.parent)

				// Give MakePlate an anchor name to search for inside the clone. The container
				// panel always carries a TMP label (that's what identifies it as a plate); grab
				// the first one and remember its name so MakePlate does not fall back to a
				// hard-coded "Trust Text" that this build does not have.
				try
				{
					var lbl = panel.GetComponentInChildren<TMPro.TMP_Text>(true);
					if (lbl != null && !string.IsNullOrEmpty(lbl.name))
					{
						LastAnchorName = lbl.name;
						try { AnchorFontSize = lbl.fontSize; } catch { }
					}
				}
				catch { }

				if (!_plateShapeLogged)
				{
					_plateShapeLogged = true;
					try
					{
						VRChatArchiveModPlugin.Logger.LogInfo(
							"[Nameplate] container='NameplateContainer' | panel='" + panel.name
							+ "' | anchor='" + LastAnchorName + "' fontSize=" + AnchorFontSize.ToString("0.#")
							+ " | spacing=" + EffectiveSpacing().ToString("0.#"));
					}
					catch { }
				}

				LastResolveFailure = "";
				_loggedFailure = "";
				return true;
			}
			catch (Exception e) { return Failed("threw: " + e.Message); }
		}

		// From FewTags-Rewrite V2/Util/Utils.cs. VRCPlayer holds a set of GameObject fields under
		// obfuscated names; one of them is the nameplate host, identified by its OBJECT NAME
		// "NameplateContainer" — the only property that survives il2cpp renames between builds.
		//
		// THE KEY DETAIL: the reference uses vrcplayer.GetIl2CppType().GetFields(), the IL2CPP
		// class API — NOT managed reflection. On a proxy, GetType().GetFields() lists the WRAPPER's
		// members (NativeFieldInfoPtr_*, Pointer, ...), never the game's real fields, so the search
		// found nothing and reported "no NameplateContainer field". Enumerating the native fields
		// with il2cpp_class_get_fields is what actually walks VRCPlayer's own layout.
		private static bool _fieldsDumped;

		internal static GameObject FindNameplateContainer(object player)
		{
			IntPtr basePtrForMiss = IntPtr.Zero;
			try
			{
				object vrcObj = GetMemberByTypeName(player, "VRCPlayer",
					"_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
				if (vrcObj == null) vrcObj = player;

				var vp = vrcObj as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (vp == null || vp.Pointer == IntPtr.Zero) return null;

				IntPtr basePtr = vp.Pointer;
				basePtrForMiss = basePtr;

				// A FAILING LOOKUP MUST NOT RUN EVERY FRAME.
				//
				// When no container is found the code below falls through the whole chain: every
				// property of the VRCPlayer proxy read, every parameterless GameObject getter INVOKED,
				// then the field table walked -- for each player, on each pass. That is fine as the rare
				// path it was written to be, and ruinous as the normal one: with the container gone from
				// VRCPlayer on this build it became the normal one, and VaTags went to 800 ms/s with the
				// game at 25 fps. A miss is remembered for two seconds, which is far tighter than a
				// nameplate ever appears.
				float nowF;
				try { nowF = VaClock.Now; } catch { nowF = 0f; }
				if (_resolveFailAt.TryGetValue(basePtr, out float failedAt) && nowF - failedAt < 2f) return null;

				// THE CONTAINER MOVED OUT OF THE PLAYER (VRChat 1903).
				//
				// VRCPlayer used to expose its nameplate host as a GameObject member, and every path
				// below looks for exactly that. On this build its field table carries no GameObject at
				// all, so all of them fail — yet the objects are still in the scene, under the global
				// manager the chat bubbles already moved to:
				//     _Application <guid>/NameplateManager/NameplateContainer/PlayerNameplate/…
				// One container per player, so finding them is not enough: each has to be matched back
				// to its owner, which the nameplate does hold — a reference to the VRCPlayer itself.
				GameObject viaManager = FromNameplateManager(basePtr, PtrOf(player), player);
				if (viaManager != null) return viaManager;

				// THE CHAIN BELOW IS A PROPERTY OF THE BUILD, NOT OF THE PLAYER.
				//
				// Everything after this line looks for the container ON VRCPlayer: every proxy property
				// read, every parameterless GameObject getter INVOKED across 271 methods, then the whole
				// field table walked. The field dump settles that on this build the container is not there
				// for ANYONE, so a failure on one player cannot become a success on the next -- and paying
				// it again every two seconds per player is what put the mod at 938 ms/s with VRChat at 0
				// fps. Three complete failures retire it for the session. The manager path above stays, and
				// a build that still carries the field returns before ever reaching this line.
				if (_chainDead) return null;

				var seen = _fieldsDumped ? null : new List<string>();

				// PROPERTIES AND METHODS FIRST — the FewTags author's own note: the nameplate host is
				// exposed as a member whose RETURN/PROPERTY type is GameObject, not a plain field.
				// Il2CppInterop generates the game's real members as C# properties/methods on the
				// wrapper type, so managed reflection over the WRAPPER (unlike GetFields, which only
				// showed wrapper plumbing) does see them.
				Type wt = vrcObj.GetType();
				foreach (var pi in wt.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (pi.PropertyType == null || pi.PropertyType.Name != "GameObject") continue;
					GameObject go;
					try { go = pi.GetValue(vrcObj) as GameObject; } catch { continue; }
					if (go == null) continue;
					string n; try { n = go.name; } catch { continue; }
					if (seen != null) seen.Add("prop:" + n);
					if (n == "NameplateContainer") return go;
				}
				foreach (var mi in wt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (mi.ReturnType == null || mi.ReturnType.Name != "GameObject") continue;
					if (mi.GetParameters().Length != 0) continue;   // getters only
					GameObject go;
					try { go = mi.Invoke(vrcObj, null) as GameObject; } catch { continue; }
					if (go == null) continue;
					string n; try { n = go.name; } catch { continue; }
					if (seen != null) seen.Add("method:" + n);
					if (n == "NameplateContainer") return go;
				}

				// Walk the class AND ITS PARENTS: il2cpp_class_get_fields only returns fields
				// declared on the exact class, so a field on a base type would be invisible with a
				// single-class walk. Climb via il2cpp_class_get_parent.
				// NOT THROUGH THE EXPORTS. On this build il2cpp_class_get_fields is bound to the PROPERTY
				// iterator and il2cpp_field_get_type hands back something that is not an Il2CppType at
				// all -- so this walk was reading nonsense and passing it to il2cpp_class_from_type,
				// which is an access violation. Core/MemberAlign measured the real field layout at
				// startup and reads it directly.
				IntPtr klass = IL2CPP.il2cpp_object_get_class(basePtr);
				while (klass != IntPtr.Zero)
				{
					foreach (IntPtr field in Core.MemberAlign.LiveFields(klass))
					{
						IntPtr ftype = Core.MemberAlign.FieldTypePtr(field);
						IntPtr fclass = ftype != IntPtr.Zero ? IL2CPP.il2cpp_class_from_type(ftype) : IntPtr.Zero;
						if (fclass == IntPtr.Zero) continue;
						string cn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(fclass));
						if (cn != "GameObject") continue;

						GameObject go = ReadGameObjectField(basePtr, field);
						if (go == null) continue;
						string n;
						try { n = go.name; } catch { continue; }

						if (seen != null) seen.Add(n);
						if (n == "NameplateContainer") return go;
					}
					klass = Core.MemberAlign.ParentOf(klass);
				}

				// FIRST FAILURE ONLY: print every GameObject field name we found, so the real name
				// of the nameplate host can be read straight off the log instead of guessed.
				if (seen != null)
				{
					_fieldsDumped = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[Nameplate] VRCPlayer GameObject fields seen: "
						+ (seen.Count == 0 ? "(none)" : string.Join(", ", seen)));

					// AND EVERY OTHER FIELD, WITH ITS TYPE. The GameObject list came back empty on this
					// build, which says the container is no longer a GameObject field -- but not what it
					// became. Guessing the new shape from an empty list is how this stays broken for
					// another week; the actual field table says it in one line.
					try
					{
						var sb = new System.Text.StringBuilder();
						int shown = 0;
						IntPtr walk = IL2CPP.il2cpp_object_get_class(basePtr);
						while (walk != IntPtr.Zero && shown < 90)
						{
							foreach (IntPtr fp in Core.MemberAlign.LiveFields(walk))
							{
								if (shown >= 90) break;
								string fn = Core.MemberAlign.LiveFieldName(fp);
								IntPtr ft = Core.MemberAlign.FieldTypePtr(fp);
								IntPtr fc = ft != IntPtr.Zero ? IL2CPP.il2cpp_class_from_type(ft) : IntPtr.Zero;
								string tn = null;
								try { if (fc != IntPtr.Zero) tn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(fc)); } catch { }
								if (string.IsNullOrEmpty(tn)) continue;
								if (shown++ > 0) sb.Append(", ");
								sb.Append(tn).Append(' ').Append(fn ?? "?");
							}
							walk = Core.MemberAlign.ParentOf(walk);
						}
						VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] VRCPlayer field table: "
							+ (shown == 0 ? "(vide — la lecture des champs ne marche pas ici)" : sb.ToString()));
					}
					catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] field dump threw: " + ex.Message); }

					// AND WHERE THE NAMEPLATES ACTUALLY LIVE NOW.
					//
					// The field table settles that VRCPlayer no longer carries a GameObject for its
					// nameplate, so the container moved -- the chat bubbles moved the same way, under a
					// global NameplateManager. One scene-wide scan says where, with the parent path.
					//
					// OPT-IN ONLY, for the same reason as the sweep in FindManager: this enumerates every
					// loaded object including assets, and it already answered its question -- the manager
					// is found by name now. A diagnostic that costs seconds is something you arm on
					// purpose while investigating, never something a player pays for on a missed lookup.
					if (DeepProbe) try
					{
						var found = new List<string>();
						var all = Resources.FindObjectsOfTypeAll<GameObject>();
						for (int i = 0; i < all.Count && found.Count < 14; i++)
						{
							GameObject g = all[i];
							if (g == null || !NativeGuard.Alive(g)) continue;
							string gn;
							try { gn = g.name; } catch { continue; }
							if (string.IsNullOrEmpty(gn) || gn.IndexOf("ameplate", StringComparison.Ordinal) < 0) continue;
							string path = gn;
							try
							{
								Transform up = g.transform.parent;
								for (int d = 0; d < 4 && up != null; d++) { path = up.name + "/" + path; up = up.parent; }
							}
							catch { }
							found.Add(path);
						}
						VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] objets 'nameplate' dans la scene : "
							+ (found.Count == 0 ? "(aucun)" : string.Join(" | ", found)));
					}
					catch (Exception ex) { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] scene scan threw: " + ex.Message); }
				}
			}
			catch { }
			if (basePtrForMiss != IntPtr.Zero)
				try { _resolveFailAt[basePtrForMiss] = VaClock.Now; } catch { }
			if (!_chainDead && ++_chainMisses >= 3)
			{
				_chainDead = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] VRCPlayer ne porte pas de conteneur sur ce build — la recherche sur le joueur est abandonnee pour la session (seul le NameplateManager est lu). C'est ce qui coutait ~900 ms/s.");
			}
			return null;
		}

		private static readonly Dictionary<IntPtr, float> _resolveFailAt = new Dictionary<IntPtr, float>();
		private static bool _chainDead;
		private static int _chainMisses;

		// ---- the nameplate table, rebuilt from the scene -----------------------------------------
		//
		// Every NameplateContainer in the scene, indexed by EVERY object its components point at. Built
		// at most once every two seconds and shared by the whole pass: the scan walks all loaded
		// objects, which is cheap once and ruinous per player.
		//
		// Indexed by value, not by declared type. The plate's own component holds its player, but not
		// under a field this build declares as VRCPlayer -- the dump of a live container shows
		// matManager, cullDisableBox, rootCanvas and three dozen obfuscated types, none of them
		// announcing what they are. So nothing is matched on a type name: whatever the plate points at
		// is recorded, and a player is found if it is pointed at.
		private static readonly Dictionary<IntPtr, GameObject> _plateByRef = new Dictionary<IntPtr, GameObject>();
		private static float _plateScanAt = -999f;
		private static bool _viaManagerLogged, _plateCountLogged, _plateGaveUp, _distLogged;
		private static int _plateEmpty, _managerTries;
		private static readonly List<GameObject> _plates = new List<GameObject>();
		private static GameObject _manager;

		// The pointers that all stand for one player: the two the caller holds, plus their GameObject and
		// Transform, which anything drawing above their head is likely to keep instead.
		private static IEnumerable<IntPtr> HandlesOf(IntPtr vrcPlayerPtr, IntPtr playerPtr)
		{
			yield return vrcPlayerPtr;
			yield return playerPtr;
			GameObject go = null;
			try
			{
				var c = vrcPlayerPtr != IntPtr.Zero ? new Component(vrcPlayerPtr) : null;
				if (c != null && NativeGuard.Alive(c)) go = c.gameObject;
			}
			catch { }
			if (go == null || !NativeGuard.Alive(go)) yield break;
			yield return PtrOf(go);
			Transform t = null;
			try { t = go.transform; } catch { }
			if (t != null && NativeGuard.Alive(t)) yield return PtrOf(t);
		}

		private static IntPtr PtrOf(object o)
		{
			var b = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
			try { return b != null ? b.Pointer : IntPtr.Zero; } catch { return IntPtr.Zero; }
		}

		private static GameObject FromNameplateManager(IntPtr vrcPlayerPtr, IntPtr playerPtr, object player)
		{
			if (_plateGaveUp) return null;
			try
			{
				float now = VaClock.Now;
				if (now - _plateScanAt >= 1f && FindManager())
				{
					_plateScanAt = now;
					_plates.Clear();
					_plateByRef.Clear();
					Transform root = null;
					try { root = _manager.transform; } catch { }
					int n = 0;
					try { if (root != null) n = root.childCount; } catch { }
					for (int i = 0; i < n && i < 128; i++)
					{
						Transform ch = null;
						try { ch = root.GetChild(i); } catch { continue; }
						if (ch == null || !NativeGuard.Alive(ch)) continue;
						GameObject g = null;
						try { g = ch.gameObject; } catch { continue; }
						if (g == null || !NativeGuard.Alive(g)) continue;
						string gn;
						try { gn = g.name; } catch { continue; }
						if (gn != "NameplateContainer") continue;
						_plates.Add(g);

						// IndexPlate IS NOT CALLED ANY MORE, AND THAT IS THE WHOLE POINT.
						//
						// It walked every component of every container and every reference field of each,
						// proving each pointer readable with a VirtualQuery syscall, to build a map from
						// "any object a plate points at" to the plate. On this build that map has NEVER
						// matched anybody: the first scan reported 207 references indexed and not one of
						// them was a player. Plates are matched by the NAME written on them instead.
						//
						// So it was pure cost, and with a full instance it was ruinous: 39 containers'
						// worth of field walks, rebuilt every second. The timing added to ApplyPlates
						// caught it outright --
						//
						//     [VaTags/plates] pass took 72425.9 ms — 2 wanted, 0 unresolved, 2 live set(s)
						//
						// seventy-two seconds to put two plates on screen. The dictionary stays (empty), so
						// the direct-reference lookup below is a dictionary miss costing nothing, and a
						// build where plates DO carry their owner can have the indexing switched back on.
					}
					if (!_plateCountLogged)
					{
						_plateCountLogged = true;
						VRChatArchiveModPlugin.Logger.LogWarning($"[Nameplate] scan : {_plates.Count} conteneur(s) sous NameplateManager, {_plateByRef.Count} reference(s) indexee(s).");
					}
					if (_plates.Count == 0 && ++_plateEmpty >= 10)
					{
						_plateGaveUp = true;
						VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] aucun conteneur sous le manager apres 10 essais — recherche arretee pour cette session.");
					}
					else if (_plates.Count > 0) _plateEmpty = 0;
				}
				if (_plates.Count == 0) return null;

				// FIRST, THE DIRECT LINK, when the plate happens to keep one.
				foreach (IntPtr cand in HandlesOf(vrcPlayerPtr, playerPtr))
				{
					if (cand == IntPtr.Zero) continue;
					if (_plateByRef.TryGetValue(cand, out GameObject direct) && direct != null && NativeGuard.Alive(direct))
						return Announce(direct, "reference");
				}

				// THEN, BY THE NAME WRITTEN ON IT.
				//
				// Two earlier attempts failed and both are worth recording, because each looked right.
				//
				//   1. The plate's own fields. Two hundred references were indexed off the live
				//      containers and not one of them is the player: on this build the plate keeps no
				//      handle on whoever it belongs to, or keeps it somewhere a field walk cannot see.
				//
				//   2. Its position. A nameplate floats over its player, so the nearest container should
				//      be theirs -- except every container reads (0,0,0). That was taken as proof the
				//      plate did not exist ("VRChat draws none for you"), and a screenshot of the plate,
				//      drawn, with the owner's name on it, settled that it does. The container's WORLD
				//      position is simply not where the plate appears: it is laid out inside a canvas and
				//      placed at render time, so Transform.position says nothing about it.
				//
				// What the plate cannot hide is the name it displays. That is the whole point of a
				// nameplate, it is a plain string, and it survives obfuscation, renaming and whatever
				// VRChat does to its transforms next.
				string want = DisplayNameOf(player);
				if (!string.IsNullOrEmpty(want))
				{
					GameObject hit = null;
					int matches = 0;
					for (int i = 0; i < _plates.Count; i++)
					{
						GameObject g = _plates[i];
						if (g == null || !NativeGuard.Alive(g)) continue;
						if (!ShowsName(g, want)) continue;
						matches++;
						if (hit == null) hit = g;
					}
					// Exactly one, or none: two plates carrying the same name means two players share a
					// display name, and handing tags to the wrong one is worse than handing them to
					// nobody.
					if (matches == 1) return Announce(hit, "nom affiche");
					if (matches > 1 && !_distLogged)
					{
						_distLogged = true;
						VRChatArchiveModPlugin.Logger.LogWarning($"[Nameplate] {matches} plaques portent le nom '{want}' — appariement refuse pour ne pas taguer le mauvais joueur.");
						return null;
					}
				}

				if (!_distLogged)
				{
					_distLogged = true;
					VRChatArchiveModPlugin.Logger.LogWarning($"[Nameplate] pas d'appariement : {_plates.Count} plaque(s) sous le manager, aucune ne porte le nom '{want ?? "?"}' et aucune ne reference le joueur. Textes vus : {TextsOf()}");
				}
				return null;
			}
			catch { return null; }
		}

		// The display name VRChat itself shows for this player, read off their APIUser. Same path the
		// roster uses, so a name good enough to print in the panel is good enough to match a plate.
		private static string DisplayNameOf(object player)
		{
			try
			{
				object apiUser = GetMemberByTypeName(player, "APIUser", "prop_APIUser_0", "field_Private_APIUser_0");
				if (apiUser == null) return null;
				string n = GetMember(apiUser, "displayName") as string;
				return string.IsNullOrEmpty(n) || n == "?" ? null : n;
			}
			catch { return null; }
		}

		// Does any label under this container read exactly this name?
		//
		// Compared against the RAW text and against the text with rich-text tags removed: VRChat
		// colours the name by trust rank, so the literal string can arrive wrapped in <color=...>,
		// and our own tag plates add more tags on top of that.
		private static bool ShowsName(GameObject container, string want)
		{
			try
			{
				var texts = container.GetComponentsInChildren<TMPro.TMP_Text>(true);
				if (texts == null) return false;
				for (int i = 0; i < texts.Length; i++)
				{
					var t = texts[i];
					if (t == null) continue;
					string s2;
					try { s2 = t.text; } catch { continue; }
					if (string.IsNullOrEmpty(s2)) continue;
					if (string.Equals(s2, want, StringComparison.Ordinal)) return true;
					if (string.Equals(StripTags(s2), want, StringComparison.Ordinal)) return true;
				}
			}
			catch { }
			return false;
		}

		private static string StripTags(string s2)
		{
			if (s2.IndexOf('<') < 0) return s2;
			var sb = new System.Text.StringBuilder(s2.Length);
			bool inTag = false;
			for (int i = 0; i < s2.Length; i++)
			{
				char c = s2[i];
				if (c == '<') { inTag = true; continue; }
				if (c == '>') { inTag = false; continue; }
				if (!inTag) sb.Append(c);
			}
			return sb.ToString().Trim();
		}

		// Every label the containers carry, for the one diagnostic line. Without it "no plate matched"
		// and "the plate spells the name differently" are the same silence -- the mistake that cost
		// this feature two sessions.
		private static string TextsOf()
		{
			try
			{
				var seen = new List<string>();
				for (int i = 0; i < _plates.Count && seen.Count < 12; i++)
				{
					GameObject g = _plates[i];
					if (g == null || !NativeGuard.Alive(g)) continue;
					var texts = g.GetComponentsInChildren<TMPro.TMP_Text>(true);
					if (texts == null) continue;
					for (int j = 0; j < texts.Length && seen.Count < 12; j++)
					{
						string s2;
						try { s2 = texts[j]?.text; } catch { continue; }
						if (string.IsNullOrEmpty(s2)) continue;
						s2 = StripTags(s2);
						if (s2.Length > 0 && !seen.Contains(s2)) seen.Add(s2);
					}
				}
				return seen.Count == 0 ? "(aucun)" : string.Join(" | ", seen);
			}
			catch { return "(illisible)"; }
		}

		private static GameObject Announce(GameObject g, string how)
		{
			if (!_viaManagerLogged)
			{
				_viaManagerLogged = true;
				VRChatArchiveModPlugin.Logger.LogInfo($"[Nameplate] container trouve sous NameplateManager par {how} — VRCPlayer ne le porte plus sur ce build.");
			}
			return g;
		}

		private static Transform PlayerTransform(IntPtr vrcPlayerPtr)
		{
			if (vrcPlayerPtr == IntPtr.Zero) return null;
			try
			{
				var c = new Component(vrcPlayerPtr);
				if (!NativeGuard.Alive(c)) return null;
				Transform t = c.transform;
				return t != null && NativeGuard.Alive(t) ? t : null;
			}
			catch { return null; }
		}

		// The global manager, found ONCE by a single scene sweep and then kept. A world change drops it
		// (the object goes with the scene), and the next sweep is paid then, not every few seconds.
		private static bool FindManager()
		{
			if (_manager != null && NativeGuard.Alive(_manager)) return true;
			_manager = null;
			if (_managerTries >= 6) return false;
			_managerTries++;

			// GameObject.Find FIRST, AND ALMOST ALWAYS LAST.
			//
			// Resources.FindObjectsOfTypeAll<GameObject>() does not enumerate the scene -- it enumerates
			// every loaded object INCLUDING assets and prefabs, and in a busy world that is hundreds of
			// thousands of entries. Measured here on 2026-09-18: 14 673 ms inside a single nameplate
			// resolve, which tripped the circuit breaker and cost the owner their tag plates twice in one
			// session. The first fix disarmed a different sweep and missed this one, because I fixed the
			// call I had just read instead of the call the timing pointed at.
			//
			// GameObject.Find walks only ACTIVE scene objects, natively, and the manager is an active
			// scene object -- so it answers the same question in a fraction of a millisecond. The old
			// sweep stays as a deliberate, opt-in fallback for a build where the name changes.
			try
			{
				GameObject quick = GameObject.Find("NameplateManager");
				if (quick != null && NativeGuard.Alive(quick))
				{
					_manager = quick;
					VRChatArchiveModPlugin.Logger.LogInfo("[Nameplate] NameplateManager trouve — les plaques seront lues sous lui.");
					return true;
				}
			}
			catch { }

			if (!DeepProbe) return false;
			try
			{
				var all = Resources.FindObjectsOfTypeAll<GameObject>();
				for (int i = 0; i < all.Count; i++)
				{
					GameObject g = all[i];
					if (g == null || !NativeGuard.Alive(g)) continue;
					string gn;
					try { gn = g.name; } catch { continue; }
					if (gn != "NameplateManager") continue;
					// The scene instance, not the prefab: a prefab has no parent chain above it.
					try { if (g.transform.parent == null) continue; } catch { continue; }
					_manager = g;
					VRChatArchiveModPlugin.Logger.LogInfo("[Nameplate] NameplateManager trouve — les plaques seront lues sous lui.");
					return true;
				}
			}
			catch { }
			return false;
		}

		internal static void ForgetNameplates()
		{
			_manager = null; _managerTries = 0; _plateGaveUp = false; _plateEmpty = 0;
			_plateByRef.Clear(); _plateScanAt = -999f;
			_resolveFailAt.Clear(); _plates.Clear();
		}

		// Everything this container's components point at, recorded against the container. The plate
		// keeps a handle on its player somewhere in there; which field it is does not matter, only that
		// the value comes back.
		private static unsafe void IndexPlate(GameObject container)
		{
			try
			{
				var comps = container.GetComponentsInChildren<Component>(true);
				int limit = comps.Count < 48 ? comps.Count : 48;
				for (int i = 0; i < limit; i++)
				{
					Component c = comps[i];
					if (c == null || !NativeGuard.Alive(c)) continue;
					IntPtr inst = c.Pointer;
					IntPtr klass;
					try { klass = IL2CPP.il2cpp_object_get_class(inst); } catch { continue; }
					for (int depth = 0; depth < 4 && klass != IntPtr.Zero; depth++)
					{
						// Precomputed offsets: the field table and its per-entry memory checks are paid
						// once per CLASS, not once per instance per pass.
						int[] offs = Core.MemberAlign.ReferenceOffsets(klass);
						for (int k = 0; k < offs.Length; k++)
						{
							int off = offs[k];
							IntPtr val;
							try
							{
								if (!NativeGuard.IsReadable(inst + off, IntPtr.Size)) continue;
								val = *(IntPtr*)((byte*)inst + off);
							}
							catch { continue; }
							if (val == IntPtr.Zero || !NativeGuard.IsLiveObject(val)) continue;
							_plateByRef[val] = container;
						}
						klass = Core.MemberAlign.ParentOf(klass);
					}
				}
			}
			catch { }
		}

		// WHAT IS ACTUALLY INSIDE A NAMEPLATE CONTAINER, ONCE.
		//
		// Nothing attached, and three different reasons would look identical from the outside: the
		// containers are templates with no owner, the owner is held under a type this does not expect,
		// or the field read is wrong. Printing the components and every object-valued field of the first
		// container answers all three in one line, and it runs only when the match came up empty.
		private static void DumpPlate(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<GameObject> all)
		{
			try
			{
				GameObject first = null;
				for (int i = 0; i < all.Count && first == null; i++)
				{
					GameObject g = all[i];
					if (g == null || !NativeGuard.Alive(g)) continue;
					try { if (g.name == "NameplateContainer") first = g; } catch { }
				}
				if (first == null) return;

				string path = "";
				try
				{
					path = first.name;
					Transform up = first.transform.parent;
					for (int d = 0; d < 5 && up != null; d++) { path = up.name + "/" + path; up = up.parent; }
				}
				catch { }

				var sb = new System.Text.StringBuilder();
				int lines = 0;
				var comps = first.GetComponentsInChildren<Component>(true);
				int limit = comps.Count < 24 ? comps.Count : 24;
				for (int i = 0; i < limit && lines < 70; i++)
				{
					Component c = comps[i];
					if (c == null || !NativeGuard.Alive(c)) continue;
					IntPtr inst = c.Pointer;
					IntPtr klass;
					try { klass = IL2CPP.il2cpp_object_get_class(inst); } catch { continue; }
					string cn = null;
					try { cn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)); } catch { }
					sb.Append(nl2).Append("   [").Append(Short(cn)).Append("] ");
					lines++;
					for (IntPtr k = klass; k != IntPtr.Zero && lines < 70; k = Core.MemberAlign.ParentOf(k))
					{
						foreach (IntPtr f in Core.MemberAlign.LiveFields(k))
						{
							IntPtr ft = Core.MemberAlign.FieldTypePtr(f);
							if (ft == IntPtr.Zero) continue;
							IntPtr fc;
							try { fc = IL2CPP.il2cpp_class_from_type(ft); } catch { continue; }
							if (fc == IntPtr.Zero) continue;
							string tn = null;
							try { tn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(fc)); } catch { }
							if (string.IsNullOrEmpty(tn)) continue;
							string fn = Core.MemberAlign.LiveFieldName(f);
							sb.Append(Short(tn)).Append(' ').Append(fn ?? "?").Append(", ");
							if (++lines >= 70) break;
						}
					}
				}
				VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] contenu de " + path + " :" + sb);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Nameplate] dump du conteneur : " + e.Message); }
		}

		private const string nl2 = "\n";

		// Obfuscated names are 23 look-alike glyphs; printing them whole makes the dump unreadable.
		private static string Short(string n)
		{
			if (string.IsNullOrEmpty(n)) return "?";
			foreach (char c in n) if (c > 126 || c < 32) return "#" + (n.GetHashCode() & 0xFFF).ToString("X3");
			return n;
		}

		// Reads one instance field as a GameObject reference. The field's storage is a pointer to
		// the il2cpp object; we pool it back into a managed GameObject wrapper. Anything that is not
		// actually a live GameObject is rejected by the wrapper / the name read above.
		private static unsafe GameObject ReadGameObjectField(IntPtr instance, IntPtr field)
		{
			try
			{
				uint off = IL2CPP.il2cpp_field_get_offset(field);
				if (off == 0) return null;
				IntPtr slot = (IntPtr)((byte*)instance + off);
				IntPtr obj = *(IntPtr*)slot;
				if (obj == IntPtr.Zero) return null;
				// Only bother if it is a Unity Object; GameObject.name will throw otherwise and be
				// swallowed by the caller.
				return new GameObject(obj);
			}
			catch { return null; }
		}

		// `is` AND `as` LIE ABOUT IL2CPP PROXIES.
		//
		// The managed type of a proxy is not its runtime type, so `o is GameObject` is false for an
		// object that IS a GameObject, and the whole method fell through to null. Everything
		// downstream then read that as "the nameplate has no quickStats", took the structural
		// fallback, and — since that used `as Component`, the same trap — failed there too. The
		// result was a nameplate that resolves perfectly well reported as unresolvable.
		//
		// TryCast asks il2cpp, which is the only authority on what a proxy actually is. The plain
		// casts stay as a fallback for genuine managed objects.
		// THE NAMEPLATE, VIA THE POSITIONER — which is where the game actually keeps it.
		//
		// A metadata reflection over the interop said it plainly: VRCPlayer no longer exposes a
		// PlayerNameplate at all. What it exposes is
		//     VRCPlayer.field_Public_PlayerNameplatePositioner_0  ->  PlayerNameplatePositioner
		// and the positioner IS the plate's Component (its .gameObject is the plate root). Every
		// previous version of this method searched the player's SUBTREE for a PlayerNameplate — but
		// the nameplate was not in the subtree, so the search could only ever fail, whatever the
		// anchor was called ("Trust Text" or otherwise). Two dumps proved it: the player's tree is
		// just the avatar rig, and a scene-wide sweep for *Nameplate* found only the Settings menu.
		//
		// So: reach the positioner by its typed member, cast through TryCast (`as` lies on il2cpp
		// proxies — the mistake that ate this feature for a day), and return it as a Component.
		private static Component FindNameplate(object player)
		{
			try
			{
				// The positioner is on VRCPlayer, not on VRC.Player. Both wrappers appear in this
				// code base; the field lives on the former.
				object vrcplayer = GetMemberByTypeName(player, "VRCPlayer",
					"_vrcplayer", "prop_VRCPlayer_0", "field_Private_VRCPlayer_0");
				if (vrcplayer == null) vrcplayer = player;   // sometimes the player object IS a VRCPlayer

				object positioner = GetMemberByTypeName(vrcplayer, "PlayerNameplatePositioner",
					"field_Public_PlayerNameplatePositioner_0", "prop_PlayerNameplatePositioner_0",
					"playerNameplatePositioner", "nameplatePositioner");
				if (positioner == null) return null;

				var b = positioner as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				var comp = b != null ? b.TryCast<Component>() : positioner as Component;
				return comp;
			}
			catch { return null; }
		}

		private static GameObject AsGameObject(object o)
		{
			if (o == null) return null;
			try
			{
				var b = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
				if (b != null)
				{
					var go = b.TryCast<GameObject>();
					if (go != null) return go;
					var tr = b.TryCast<Transform>();
					if (tr != null) return tr.gameObject;
					var cp = b.TryCast<Component>();
					if (cp != null) return cp.gameObject;
				}
			}
			catch { }

			if (o is GameObject g) return g;
			if (o is Transform t) return t.gameObject;
			if (o is Component c) { try { return c.gameObject; } catch { return null; } }
			return null;
		}

		// --- tiny reflection kit (IL2CPP interop exposes game fields as properties) ---

		private static readonly Dictionary<string, MemberInfo> MemberCache = new Dictionary<string, MemberInfo>();

		internal static object GetMember(object obj, string name)
		{
			if (obj == null) return null;
			Type t = obj.GetType();
			string key = t.FullName + "::" + name;
			if (!MemberCache.TryGetValue(key, out MemberInfo mi))
			{
				mi = (MemberInfo)t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
					?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				MemberCache[key] = mi;
			}
			return ReadMember(obj, mi);
		}

		// First tries the given member names, then any property/field whose type name matches.
		internal static object GetMemberByTypeName(object obj, string typeName, params string[] names)
		{
			if (obj == null) return null;
			foreach (string n in names)
			{
				object v = GetMember(obj, n);
				if (v != null) return v;
			}
			Type t = obj.GetType();
			string key = t.FullName + "::bytype::" + typeName;
			if (!MemberCache.TryGetValue(key, out MemberInfo mi))
			{
				mi = FindByTypeName(t, typeName, exactPrefix: false);
				MemberCache[key] = mi;
			}
			return ReadMember(obj, mi);
		}

		private static object GetMemberByTypeNamePrefix(object obj, string typePrefix, params string[] names)
		{
			if (obj == null) return null;
			foreach (string n in names)
			{
				object v = GetMember(obj, n);
				if (v != null) return v;
			}
			Type t = obj.GetType();
			string key = t.FullName + "::byprefix::" + typePrefix;
			if (!MemberCache.TryGetValue(key, out MemberInfo mi))
			{
				mi = FindByTypeName(t, typePrefix, exactPrefix: true);
				MemberCache[key] = mi;
			}
			return ReadMember(obj, mi);
		}

		private static MemberInfo FindByTypeName(Type t, string typeName, bool exactPrefix)
		{
			try
			{
				foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					string n = p.PropertyType.Name;
					if (exactPrefix ? n.StartsWith(typeName, StringComparison.Ordinal) : n == typeName) return p;
				}
				foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					string n = f.FieldType.Name;
					if (exactPrefix ? n.StartsWith(typeName, StringComparison.Ordinal) : n == typeName) return f;
				}
			}
			catch { }
			return null;
		}

		private static object GetStaticByTypeName(Type t, string typeName, params string[] names)
		{
			foreach (string n in names)
			{
				try
				{
					PropertyInfo p = t.GetProperty(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
					object v = Core.ProxyGuard.GetValue(p, null);
					if (v != null) return v;
				}
				catch { }
			}
			try
			{
				foreach (PropertyInfo p in t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (p.PropertyType.Name != typeName) continue;
					object v = Core.ProxyGuard.GetValue(p, null);
					if (v != null) return v;
				}
			}
			catch { }
			return null;
		}

		// Exposed because the same danger exists for any direct member access on a game object, not
		// just the reflected ones: a dead proxy has to be turned into null before it is touched.
		internal static T AliveOrNull<T>(T o) where T : class => Core.NativeGuard.Alive(o) ? o : null;

		private static object ReadMember(object obj, MemberInfo mi)
		{
			// The guard has to come BEFORE the read, not around it: reading a field off a proxy whose
			// native object is not there is an AccessViolation, and .NET 6 ends the process on those
			// rather than raising something the catch below could see.
			if (mi == null || !Core.NativeGuard.Alive(obj)) return null;
			try
			{
				// And the class itself has to exist on this build: through MissingTypeGuard's
				// placeholder a getter resolves to a System.Object method and invoking it is fatal.
				if (mi is PropertyInfo p) return Core.ProxyGuard.GetValue(p, obj);
				if (mi is FieldInfo f) return Core.ProxyGuard.GetValue(f, obj);
			}
			catch { }
			return null;
		}
	}
}
