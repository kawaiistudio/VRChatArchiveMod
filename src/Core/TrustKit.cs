using System;
using UnityEngine;
using VRC.Core;

namespace VRChatArchiveMod.Core
{
	// Single source of truth for VRChat trust-rank colours, shared by ESP, the instance
	// panels and the PLAYERS tab so a player is the same colour everywhere.
	//
	// Uses APIUser's TYPED has*TrustLevel properties (the same access ESP has always used
	// successfully) rather than parsing the raw tags list, which is easy to get wrong.
	public static class TrustKit
	{
		public const string HexVisitor  = "#D6D6E0";
		public const string HexNewUser  = "#1778FF";
		public const string HexUser     = "#2BCF5C";
		public const string HexKnown    = "#FF7B42";
		public const string HexTrusted  = "#8143E6";
		public const string HexNuisance = "#8B0000";
		public const string HexVrcTeam  = "#FF1F1F";
		public const string HexFriend   = "#FFFF00";

		// Trust colour as a hex string, ready for rich text. Friends override to gold,
		// matching VRChat's own nameplate behaviour.
		public static string HexOf(object apiUserObj, bool friendOverride = true)
		{
			try
			{
				var u = apiUserObj as APIUser;
				if (u == null) return HexVisitor;

				if (friendOverride)
				{
					try { if (u.isFriend) return HexFriend; } catch { }
				}
				try { if (u.hasSuperPowers) return HexVrcTeam; } catch { }
				try { if ((int)u.developerType >= 2) return HexVrcTeam; } catch { }
				try { if (u.hasVeryNegativeTrustLevel || u.hasNegativeTrustLevel) return HexNuisance; } catch { }
				try { if (u.hasLegendTrustLevel || u.hasVeteranTrustLevel) return HexTrusted; } catch { }
				try { if (u.hasTrustedTrustLevel) return HexKnown; } catch { }
				try { if (u.hasKnownTrustLevel) return HexUser; } catch { }
				try { if (u.hasBasicTrustLevel) return HexNewUser; } catch { }
				return HexVisitor;
			}
			catch { return HexVisitor; }
		}

		// Short rank label ("Trusted", "Known", …) for panels that show it in text.
		public static string RankOf(object apiUserObj)
		{
			switch (HexOf(apiUserObj, friendOverride: false))
			{
				case HexVrcTeam:  return "VRChat Team";
				case HexNuisance: return "Nuisance";
				case HexTrusted:  return "Trusted";
				case HexKnown:    return "Known";
				case HexUser:     return "User";
				case HexNewUser:  return "New User";
				default:          return "Visitor";
			}
		}

		public static Color ColorOf(object apiUserObj)
		{
			// A SIGNATURE COLOUR, DECIDED IN ONE PLACE. Listed ids are drawn with a cycling rainbow
			// instead of their trust colour, on every client running this mod. It lives here rather
			// than in each ESP because there are three of them — capsule, box, mesh glow — and a rule
			// implemented three times is a rule that will be right in two of them.
			if (IsRainbow(apiUserObj)) return Spectrum();
			return ColorUtility.TryParseHtmlString(HexOf(apiUserObj), out Color c) ? c : Color.white;
		}

		public static Color ColorOf(VRC.SDKBase.VRCPlayerApi api)
		{
			if (api == null) return Color.white;
			if (IsRainbowFor(api)) return Spectrum();
			var user = ApiUsers.Get(api);
			if (user != null) return ColorOf(user);
			return ColorUtility.TryParseHtmlString(HexVisitor, out Color c) ? c : Color.white;
		}

		public static Color Spectrum()
		{
			float speed = 0.35f;
			try { speed = ModConfig.RainbowSpeed.Value; } catch { }
			return Color.HSVToRGB(Mathf.Repeat(VaClock.Now * speed, 1f), 0.85f, 1f);
		}

		// THE ID, WITHOUT APIUser — WHICH IS WHY THE RAINBOW HAD STOPPED.
		//
		// IsRainbow needed an APIUser to read an id, and on 1903 ApiUsers.Get() returns null for every
		// player: VRC.Player's members cannot be placed on this build, as the startup log says outright.
		// So the verdict was false for everyone and the capsule, the box and the glow all drew a plain
		// trust colour. Nothing was wrong with the rainbow itself; its INPUT was never arriving.
		//
		// The id is available elsewhere and always has been: the roster carries it, resolved through the
		// SDK (VRCPlayerApi), which works on this build. Same answer, no obfuscated member involved.
		public static string UidOf(object playerApi)
		{
			try
			{
				var api = playerApi as VRC.SDKBase.VRCPlayerApi;
				if (api == null) return null;
				int pid; try { pid = api.playerId; } catch { return null; }
				var roster = Modules.VaTagsModule.Roster;
				if (roster == null) return null;
				int n; try { n = roster.Count; } catch { return null; }
				for (int i = 0; i < n; i++)
				{
					Modules.VaTagsModule.PlayerEntry e;
					try { e = roster[i]; } catch { break; }
					if (e == null || string.IsNullOrEmpty(e.UserId)) continue;
					// PlayerId, NOT a cast. The roster holds the VRC.Player COMPONENT, not a VRCPlayerApi, so
					// "e.Player as VRCPlayerApi" was null for every entry and this never matched anyone --
					// which is exactly why the rainbow worked "one time in two": it silently fell back to the
					// flickering APIUser path. The entry already carries the actor number.
					int q = e.PlayerId;
					if (q == pid) return e.UserId;
				}
			}
			catch { }
			return null;
		}

		/// <summary>The rainbow verdict from an id alone — no APIUser, no il2cpp string marshal.</summary>
		public static bool IsRainbowId(string uid)
		{
			try
			{
				if (string.IsNullOrEmpty(uid)) return false;
				try { if (ModConfig.LegendaryRainbow.Value && Modules.VaTagsModule.IsLegendary(uid)) return true; }
				catch { }
				string ids = ModConfig.RainbowUserIds.Value ?? "";
				if (ids.Length == 0) return false;
				return ids.IndexOf(uid, System.StringComparison.OrdinalIgnoreCase) >= 0;
			}
			catch { return false; }
		}

		/// <summary>Rainbow for a live player: the id comes from the roster, APIUser is not needed.</summary>
		public static bool IsRainbowFor(object playerApi) => IsRainbowId(UidOf(playerApi));

		public static bool IsRainbow(object apiUserObj)
		{
			try
			{
				if (apiUserObj == null) return false;

				// The id is read ONCE, before either test: it is the only expensive part of this method
				// (a string marshal out of il2cpp) and both rules need it.
				string uid = "";
				var u = apiUserObj as VRC.Core.APIUser;
				if (u != null) { try { uid = u.id ?? ""; } catch { } }
				if (uid.Length == 0) return false;

				// A RANK, NOT A LIST. Archive Legendary is granted and revoked by the tag system on its
				// own — EnsureRankTag rewrites the badge on every pass and takes it off the moment the
				// level drops — so binding the rainbow to it makes the rainbow last exactly as long as
				// the rank does: nothing to grant by hand, nothing to clean up after a demotion. The
				// site draws that tier as a swept gradient; this is the same statement in game.
				try
				{
					if (ModConfig.LegendaryRainbow.Value && Modules.VaTagsModule.IsLegendary(uid)) return true;
				}
				catch { }

				// The hand-kept list still stands beside it, for a signature somebody was given rather
				// than earned.
				string ids = ModConfig.RainbowUserIds.Value ?? "";
				if (ids.Length == 0) return false;
				return ids.IndexOf(uid, System.StringComparison.OrdinalIgnoreCase) >= 0;
			}
			catch { return false; }
		}
	}
}
