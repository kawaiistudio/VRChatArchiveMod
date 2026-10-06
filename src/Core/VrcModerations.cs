using System;
using System.Collections.Generic;
using System.Net.Http;

namespace VRChatArchiveMod.Core
{
	// WHO BLOCKED YOU, ASKED THE WAY THE OFFICIAL CLIENT ASKS.
	//
	// WHY NOT THE GAME'S OWN CALL
	//
	// ApiPlayerModeration.FetchAllAgainstMe takes two callbacks, and handing a managed callback to
	// il2cpp goes through DelegateSupport.ConvertDelegate, which kills the process on this build. So
	// the [B] marker fell back to reading a boolean off the player -- by its 1886 INDEX
	// (prop_Boolean_17). The obfuscator reorders members, so on 1903 that index lands on a different
	// boolean, one that is true for nearly everyone, and the marker appeared on the whole room. The
	// module disarmed itself rather than accuse real people, which was the right call and left the
	// feature dead.
	//
	// THE ROUTE THAT DOES NOT DEPEND ON THE BUILD AT ALL
	//
	// "Who blocked me" is not a secret the game hides in a field -- it is an endpoint. The same one
	// the official client reads: GET /api/1/auth/user/playermoderated returns the moderations whose
	// TARGET is you, block entries included. Asked over HTTP with the session's own cookie, the answer
	// is authoritative, needs no delegate, no proxy and no member index, and it keeps working the next
	// time VRChat permutes its members.
	//
	// Read-only, one request, and it sends nothing about anyone: no state is changed, the account is
	// not touched, and the mod identifies itself honestly in the User-Agent with a contact URL rather
	// than pretending to be a browser.
	internal static class VrcModerations
	{
		private static readonly HttpClient Http = Make();
		private static HttpClient Make()
		{
			var c = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
			// VRChat asks API consumers to identify themselves and give a way to be reached. A contact
			// URL, never the owner's personal address.
			c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
				"VRChatArchiveMod/" + PluginInfo.Version + " (https://vrchatarchive.org)");
			return c;
		}

		private static bool _tokenLogged;

		/// <summary>The session cookie the game already holds. Found by reflection on the hand-written
		/// VRC.Core.ApiCredentials -- a plain type, not an obfuscated one -- and never logged.</summary>
		internal static string AuthToken()
		{
			try
			{
				var t = System.Reflection.Assembly.Load("VRCCore-Standalone").GetType("VRC.Core.ApiCredentials")
					?? Type.GetType("VRC.Core.ApiCredentials, VRCCore-Standalone");
				if (t == null) return null;

				const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Static
					| System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

				// By what it RETURNS and what it is called, not by a fixed name: VRChat has shipped
				// GetAuthToken(), GetAuthTokenProvider() and a plain AuthToken property across builds.
				foreach (var m in t.GetMethods(F))
				{
					if (m.GetParameters().Length != 0 || m.ReturnType != typeof(string)) continue;
					if (m.Name.IndexOf("AuthToken", StringComparison.OrdinalIgnoreCase) < 0) continue;
					string v = m.Invoke(null, null) as string;
					if (!string.IsNullOrEmpty(v)) { Say(m.Name); return v; }
				}
				foreach (var p in t.GetProperties(F))
				{
					if (p.PropertyType != typeof(string)) continue;
					if (p.Name.IndexOf("AuthToken", StringComparison.OrdinalIgnoreCase) < 0) continue;
					string v = p.GetValue(null, null) as string;
					if (!string.IsNullOrEmpty(v)) { Say(p.Name); return v; }
				}
			}
			catch { }
			return null;
		}

		// The key is a public client constant, but a user log has no reason to carry it.
		private static string Trim(string url)
		{
			int i = url.IndexOf("?apiKey", StringComparison.Ordinal);
			return i > 0 ? url.Substring(0, i) : url;
		}

		private static void Say(string member)
		{
			if (_tokenLogged) return;
			_tokenLogged = true;
			// The MEMBER's name, never the token itself.
			VRChatArchiveModPlugin.Logger.LogInfo("[Moderations] jeton de session lu via ApiCredentials." + member + ".");
		}

		/// <summary>User ids of the people who have blocked YOU. null means the question could not be
		/// asked -- which is not the same as "nobody", and the caller must not treat it as one.</summary>
		internal static HashSet<string> FetchBlockedMe(out string error)
		{
			error = null;
			string token = AuthToken();
			if (string.IsNullOrEmpty(token))
			{
				error = "pas de jeton de session (pas encore connecte ?)";
				return null;
			}

			try
			{
				// SEVERAL CANDIDATES, BECAUSE ONE GUESS ALREADY COST A TEST SESSION.
				//
				// The first attempt used a single path and came back 404, so the owner ran a whole block test
				// against a route that never existed. VRChat also wants an apiKey on most endpoints, and the
				// shape of this one is not publicly documented. So the candidates are tried in order, the first
				// 200 wins, and whichever answered is written to the log -- settled once, never guessed again.
				const string Key = "JlE5Jldo5Jibnk5O5hTx6XVqsJu4WJ26";
				string[] candidates =
				{
					"https://api.vrchat.cloud/api/1/auth/user/playermoderated?apiKey=" + Key,
					"https://api.vrchat.cloud/api/1/auth/user/playermoderations?apiKey=" + Key,
					"https://vrchat.com/api/1/auth/user/playermoderated?apiKey=" + Key,
				};

				string lastErr = null;
				foreach (string url in candidates)
				{
					var req = new HttpRequestMessage(HttpMethod.Get, url);
					req.Headers.TryAddWithoutValidation("Cookie", "auth=" + token);
					var res = Http.SendAsync(req).GetAwaiter().GetResult();
					string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
					if (!res.IsSuccessStatusCode)
					{
						lastErr = "HTTP " + (int)res.StatusCode;
						VRChatArchiveModPlugin.Logger.LogInfo("[Moderations] " + lastErr + " sur " + Trim(url));
						continue;
					}
					VRChatArchiveModPlugin.Logger.LogInfo("[Moderations] 200 sur " + Trim(url) + " — "
						+ (body == null ? 0 : body.Length) + " octets. C'est LE bon chemin.");
					return ParseBlocks(body);
				}
				error = lastErr ?? "aucun chemin n'a repondu";
				return null;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + " : " + e.Message;
				return null;
			}
		}

		// The payload is a flat array of moderations. Only "block" counts, and only the SOURCE matters
		// -- the source is the person who did it, the target is you. Parsed by hand rather than pulling
		// a JSON dependency in: the shape is three fields deep and a malformed entry must be skipped,
		// not throw away the whole answer.
		internal static HashSet<string> ParseBlocks(string json)
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			if (string.IsNullOrEmpty(json)) return set;
			try
			{
				using var doc = System.Text.Json.JsonDocument.Parse(json);
				if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return set;
				foreach (var el in doc.RootElement.EnumerateArray())
				{
					try
					{
						if (!el.TryGetProperty("type", out var ty)) continue;
						string type = ty.GetString();
						if (!string.Equals(type, "block", StringComparison.OrdinalIgnoreCase)) continue;
						string src = null;
						if (el.TryGetProperty("sourceUserId", out var s)) src = s.GetString();
						if (string.IsNullOrEmpty(src) && el.TryGetProperty("sourceUserID", out var s2)) src = s2.GetString();
						if (!string.IsNullOrEmpty(src)) set.Add(src);
					}
					catch { }
				}
			}
			catch { }
			return set;
		}
	}
}
