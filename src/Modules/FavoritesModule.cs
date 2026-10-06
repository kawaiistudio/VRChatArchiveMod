using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// ARCHIVE & VRCX LOCAL FAVOURITES — the user's unlimited favourites, in game.
	//
	// Three layers, deliberately separate:
	//   1. the LIST (ids + metadata from Archive bridge and VRCX SQLite database)
	//   2. the METADATA (real name, author, thumbnail url) resolved from VRCX or Archive bridge
	//   3. the THUMBNAIL itself, fetched only for cards actually on screen and cached on disk
	//
	// Integrates VRCX Local Favorites (%APPDATA%\VRCX\VRCX.sqlite3) seamlessly with Archive favourites.
	public class FavoritesModule : IModule
	{
		public override string Name => "Favorites";

		public sealed class Fav
		{
			public string Id;
			public string Name;          // server-stored or VRCX-cached name
			public string Author = "";
			public string Image = "";    // thumbnail or full-size image url
			public bool MetaDone;

			public Texture2D Thumb;
			// 0 not asked · 1 fetching · 2 ready · 3 unavailable
			public int ThumbState;
		}

		private static readonly List<Fav> Items = new List<Fav>();
		private static readonly List<string> Ids = new List<string>();
		private static readonly Dictionary<string, Fav> ById = new Dictionary<string, Fav>(StringComparer.OrdinalIgnoreCase);
		private static readonly object Gate = new object();

		private static readonly HttpClient DirectHttp = MakeDirectHttp();
		private static HttpClient MakeDirectHttp()
		{
			var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
			c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 VRChatArchiveMod/3.9");
			return c;
		}

		public static string LastStatus = "";

		// Bumped on every change to the list. Anything DISPLAYING these favourites watches this and
		// redraws when it moves.
		public static int Revision { get; private set; }
		private static void Bump() { unchecked { Revision++; } }
		public static bool Loaded { get; private set; }
		public static int Count { get { lock (Gate) return Ids.Count; } }

		private float _nextRefresh;
		private float _nextVrcxCheck;
		private static bool _vrcxLoaded;
		private static bool _busy;

		public static List<string> Snapshot() { lock (Gate) return new List<string>(Ids); }
		public static List<Fav> Favourites() { lock (Gate) return new List<Fav>(Items); }
		public static bool Has(string id)
		{
			if (string.IsNullOrEmpty(id)) return false;
			lock (Gate) return ById.ContainsKey(id);
		}

		public static string IdForName(string name)
		{
			if (string.IsNullOrEmpty(name)) return null;
			string want = name.Trim();
			lock (Gate)
			{
				foreach (var f in Items)
				{
					if (!string.IsNullOrEmpty(f.Name) && string.Equals(f.Name.Trim(), want, StringComparison.OrdinalIgnoreCase))
						return f.Id;
				}
			}
			return null;
		}

		public override void OnUpdate()
		{
			try
			{
				// Decoding happens here because creating a Texture2D is main-thread only. A couple
				// per frame: a full grid arriving at once would otherwise be a visible hitch.
				DrainThumbs(2);

				float now = VaClock.Now;

				// Check VRCX database changes every 1.0 second
				if (now >= _nextVrcxCheck)
				{
					_nextVrcxCheck = now + 1.0f;
					if (VrcxLocalFavorites.HasChanged() || (!_vrcxLoaded && VrcxLocalFavorites.Exists))
					{
						_vrcxLoaded = true;
						_ = RefreshAsync();
					}
				}

				if (VaAuth.InsideClient)
				{
					if (now >= _nextMeta) { _nextMeta = now + 1f; PumpMeta(); }
				}

				if (now < _nextRefresh) return;
				_nextRefresh = now + (Loaded ? 120f : 15f);
				_ = RefreshAsync();
			}
			catch { }
		}

		// ---------------------------------------------------------------- list

		public static async System.Threading.Tasks.Task RefreshAsync()
		{
			if (_busy) return;
			_busy = true;
			try
			{
				var found = new List<Fav>();

				// 1. Load from VRCX Local SQLite database (%APPDATA%\VRCX\VRCX.sqlite3)
				try
				{
					var vrcxAvatars = VrcxLocalFavorites.LoadAll();
					foreach (var v in vrcxAvatars)
					{
						if (string.IsNullOrEmpty(v.Id)) continue;
						var f = new Fav
						{
							Id = v.Id,
							Name = !string.IsNullOrWhiteSpace(v.Name) ? v.Name : v.Id,
							Author = v.AuthorName ?? "",
							Image = !string.IsNullOrWhiteSpace(v.ThumbnailUrl) ? v.ThumbnailUrl : (v.ImageUrl ?? ""),
							MetaDone = true // VRCX cache_avatar already holds complete metadata
						};
						found.Add(f);
						AvatarIndex.Register(f.Id, f.Name, f.Author, f.Image);
					}
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] VRCX local favorites load failed: " + ex.Message);
				}

				// 2. Load from Archive bridge if connected
				if (VaAuth.InsideClient)
				{
					try
					{
						var (ok, raw, status) = await VaAuth.FavAsync("list", "");
						if (ok && !string.IsNullOrEmpty(raw))
						{
							using var doc = JsonDocument.Parse(raw);
							Collect(doc.RootElement, found);
						}
					}
					catch (Exception ex)
					{
						VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] Archive bridge load failed: " + ex.Message);
					}
				}

				lock (Gate)
				{
					// Merge rather than replace: keep resolved names and already-decoded thumbnails
					var kept = new Dictionary<string, Fav>(ById, StringComparer.OrdinalIgnoreCase);
					Items.Clear(); Ids.Clear(); ById.Clear();
					foreach (var f in found)
					{
						if (ById.ContainsKey(f.Id)) continue;
						Fav use = kept.TryGetValue(f.Id, out var old) ? old : f;
						if (use != f)
						{
							if (!use.MetaDone && !string.IsNullOrEmpty(f.Name)) use.Name = f.Name;
							if (string.IsNullOrEmpty(use.Author) && !string.IsNullOrEmpty(f.Author)) use.Author = f.Author;
							if (string.IsNullOrEmpty(use.Image) && !string.IsNullOrEmpty(f.Image)) use.Image = f.Image;
							if (f.MetaDone) use.MetaDone = true;
						}
						Ids.Add(use.Id); Items.Add(use); ById[use.Id] = use;
					}
				}
				Loaded = true;
				Bump();
				LastStatus = Count + " favourite(s) (VRCX + Archive)";
				VRChatArchiveModPlugin.Logger.LogInfo($"[Favorites] {Count} avatar favourite(s) active.");
			}
			catch (Exception e) { LastStatus = "favourites failed: " + e.Message; }
			finally { _busy = false; }
		}

		private static void Collect(JsonElement e, List<Fav> outp)
		{
			switch (e.ValueKind)
			{
				case JsonValueKind.Object:
					if (e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
					{
						string id = idEl.GetString();
						if (!string.IsNullOrEmpty(id) && id.StartsWith("avtr_", StringComparison.Ordinal))
						{
							string nm = e.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
								? nEl.GetString() : null;
							outp.Add(new Fav { Id = id, Name = string.IsNullOrWhiteSpace(nm) ? id : nm });
							return;
						}
					}
					foreach (var p in e.EnumerateObject()) Collect(p.Value, outp);
					break;
				case JsonValueKind.Array:
					foreach (var c in e.EnumerateArray()) Collect(c, outp);
					break;
				case JsonValueKind.String:
					string v = e.GetString();
					if (!string.IsNullOrEmpty(v) && v.StartsWith("avtr_", StringComparison.Ordinal))
						outp.Add(new Fav { Id = v, Name = v });
					break;
			}
		}

		// ---------------------------------------------------------------- metadata

		private static float _nextMeta;
		private static bool _metaBusy;
		public static int MetaPending { get; private set; }

		private static void PumpMeta()
		{
			if (_metaBusy || !Loaded) return;
			var batch = new List<Fav>(100);
			int pending = 0;
			lock (Gate)
			{
				foreach (var f in Items)
				{
					if (f.MetaDone) continue;
					pending++;
					if (batch.Count < 100) batch.Add(f);
				}
			}
			MetaPending = pending;
			if (batch.Count == 0) return;
			_metaBusy = true;
			_ = ResolveAsync(batch);
		}

		private static async System.Threading.Tasks.Task ResolveAsync(List<Fav> batch)
		{
			try
			{
				var sb = new StringBuilder("{\"action\":\"meta\",\"ids\":[");
				for (int i = 0; i < batch.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append('"').Append(batch[i].Id).Append('"');
				}
				sb.Append("]}");

				var (ok, raw, status) = await VaAuth.FavRawAsync(sb.ToString());
				if (!ok)
				{
					foreach (var f in batch) f.MetaDone = true;
					VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] metadata batch failed (" + status + ")");
					return;
				}

				using var doc = JsonDocument.Parse(raw);
				if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object)
				{
					foreach (var p in res.EnumerateObject())
					{
						string key = p.Name;                       // "avatar:avtr_xxx"
						int c = key.IndexOf(':');
						string id = c >= 0 ? key.Substring(c + 1) : key;
						Fav f;
						lock (Gate) { ById.TryGetValue(id, out f); }
						if (f == null) continue;

						string title = Str(p.Value, "title");
						string image = Str(p.Value, "image");
						string author = Str(p.Value, "authorName");
						SplitTitle(ref title, ref author);
						if (!string.IsNullOrWhiteSpace(title)) f.Name = title;
						if (!string.IsNullOrWhiteSpace(author)) f.Author = author;
						if (!string.IsNullOrWhiteSpace(image)) f.Image = image;
					}
				}
				foreach (var f in batch) f.MetaDone = true;
			}
			catch (Exception e)
			{
				foreach (var f in batch) f.MetaDone = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] metadata: " + e.Message);
			}
			finally { _metaBusy = false; }
		}

		private static string Str(JsonElement e, string prop)
			=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

		private static readonly string[] Boilerplate =
		{
			" - an avatar on VRChat", " - an avatar on vrchat", " on VRChat. Click", ". Click to view",
		};

		private static string CleanAuthor(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			foreach (string b in Boilerplate)
			{
				int i = s.IndexOf(b, StringComparison.OrdinalIgnoreCase);
				if (i > 0) s = s.Substring(0, i);
			}
			return s.Trim().TrimEnd('-', '.', ',').Trim();
		}

		private static void SplitTitle(ref string title, ref string author)
		{
			try
			{
				author = CleanAuthor(author);
				if (string.IsNullOrWhiteSpace(title)) return;
				const string sep = " by ";

				if (!string.IsNullOrWhiteSpace(author))
				{
					string tail = sep + author;
					if (title.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
						title = title.Substring(0, title.Length - tail.Length).TrimEnd();
					return;
				}

				int i = title.LastIndexOf(sep, StringComparison.Ordinal);
				if (i <= 0) return;
				string left = title.Substring(0, i).TrimEnd();
				string right = title.Substring(i + sep.Length).Trim();
				if (left.Length == 0 || right.Length == 0) return;
				title = left;
				author = right;
			}
			catch { }
		}

		// ---------------------------------------------------------------- thumbnails

		private static readonly ConcurrentQueue<(Fav fav, byte[] data)> Decoded = new ConcurrentQueue<(Fav, byte[])>();
		private static int _inflight;
		private const int MaxInflight = 4;

		private static string ThumbDir
		{
			get
			{
				string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "thumbs");
				try { Directory.CreateDirectory(d); } catch { }
				return d;
			}
		}

		public static void RequestThumb(Fav f)
		{
			if (f == null || f.ThumbState != 0) return;
			if (!f.MetaDone || string.IsNullOrEmpty(f.Image)) return;
			if (_inflight >= MaxInflight) return;
			f.ThumbState = 1;
			_inflight++;
			_ = FetchThumbAsync(f);
		}

		private static async System.Threading.Tasks.Task FetchThumbAsync(Fav f)
		{
			byte[] data = null;
			try
			{
				string cache = Path.Combine(ThumbDir, f.Id + ".img");
				try { if (File.Exists(cache)) data = File.ReadAllBytes(cache); } catch { }

				if (data == null || data.Length < 100)
				{
					data = await GetImageAsync(Small(f.Image));
					if (data == null) data = await GetImageAsync(f.Image);
					if (data != null && data.Length >= 100)
					{
						try { File.WriteAllBytes(cache, data); } catch { }
					}
				}
			}
			catch { }
			finally { Decoded.Enqueue((f, data)); }
		}

		private static async System.Threading.Tasks.Task<byte[]> GetImageAsync(string url)
		{
			try
			{
				if (string.IsNullOrEmpty(url)) return null;

				// 1. Bridge image request if inside Archive client
				if (VaAuth.InsideClient)
				{
					try
					{
						string body = "{\"action\":\"image\",\"url\":\"" + url.Replace("\"", "") + "\"}";
						var (ok, raw, _) = await VaAuth.FavRawAsync(body);
						if (ok)
						{
							using var doc = JsonDocument.Parse(raw);
							if (doc.RootElement.TryGetProperty("b64", out var b) && b.ValueKind == JsonValueKind.String)
								return Convert.FromBase64String(b.GetString());
						}
					}
					catch { }
				}

				// 2. Direct HTTP fallback (works for public VRChat API avatar thumbnails)
				if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
				{
					using var resp = await DirectHttp.GetAsync(url);
					if (resp.IsSuccessStatusCode)
					{
						return await resp.Content.ReadAsByteArrayAsync();
					}
				}
			}
			catch { }
			return null;
		}

		private static string Small(string image)
		{
			try
			{
				foreach (string marker in new[] { "/api/1/file/", "/api/1/image/" })
				{
					int i = image.IndexOf(marker, StringComparison.Ordinal);
					if (i < 0) continue;
					string[] parts = image.Substring(i + marker.Length).Split('/');
					if (parts.Length < 2 || !parts[0].StartsWith("file_", StringComparison.Ordinal)) continue;
					return image.Substring(0, i) + "/api/1/image/" + parts[0] + "/" + parts[1] + "/256";
				}
			}
			catch { }
			return image;
		}

		private const int MaxThumbWidth = 320;
		private const int ThumbWidth = 256;

		private static Texture2D Downscale(Texture2D src, string cachePath)
		{
			try
			{
				if (src == null || src.width <= MaxThumbWidth) return src;
				int w = ThumbWidth;
				int h = Mathf.Max(1, Mathf.RoundToInt(src.height * (w / (float)src.width)));

				var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
				var prev = RenderTexture.active;
				Graphics.Blit(src, rt);
				RenderTexture.active = rt;

				var dst = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
				dst.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
				dst.Apply(false, false);

				RenderTexture.active = prev;
				RenderTexture.ReleaseTemporary(rt);
				UnityEngine.Object.Destroy(src);

				try
				{
					var png = ImageConversion.EncodeToPNG(dst);
					if (png != null && png.Length > 100)
					{
						var managed = new byte[png.Length];
						for (int i = 0; i < png.Length; i++) managed[i] = png[i];
						File.WriteAllBytes(cachePath, managed);
					}
				}
				catch { }
				return dst;
			}
			catch { return src; }
		}

		private static void DrainThumbs(int max)
		{
			for (int n = 0; n < max; n++)
			{
				if (!Decoded.TryDequeue(out var item)) return;
				_inflight--;
				if (_inflight < 0) _inflight = 0;
				try
				{
					if (item.data == null || item.data.Length < 100) { item.fav.ThumbState = 3; continue; }
					var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
					{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
					if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(item.data)))
					{
						item.fav.ThumbState = 3;
						continue;
					}
					item.fav.Thumb = Downscale(tex, Path.Combine(ThumbDir, item.fav.Id + ".img"));
					item.fav.ThumbState = 2;
				}
				catch { item.fav.ThumbState = 3; }
			}
		}

		// ---------------------------------------------------------------- writes

		public static async System.Threading.Tasks.Task<bool> AddAsync(string avatarId, string name = null, string author = null, string image = null)
		{
			if (string.IsNullOrEmpty(avatarId)) return false;

			string safeName = !string.IsNullOrEmpty(name) ? name : avatarId;
			string safeAuthor = author ?? "";
			string safeImg = image ?? "";

			// 1. Write to VRCX Local SQLite database (%APPDATA%\VRCX\VRCX.sqlite3)
			try
			{
				VrcxLocalFavorites.AddFavorite(avatarId, safeName, safeAuthor, safeImg, safeImg);
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] VrcxLocalFavorites.AddFavorite failed: " + ex.Message);
			}

			// 2. Immediately update memory
			lock (Gate)
			{
				if (!ById.TryGetValue(avatarId, out var f))
				{
					f = new Fav { Id = avatarId, Name = safeName, Author = safeAuthor, Image = safeImg, MetaDone = true };
					Ids.Insert(0, avatarId);
					Items.Insert(0, f);
					ById[avatarId] = f;
				}
				else
				{
					if (!string.IsNullOrEmpty(safeName)) f.Name = safeName;
					if (!string.IsNullOrEmpty(safeAuthor)) f.Author = safeAuthor;
					if (!string.IsNullOrEmpty(safeImg)) f.Image = safeImg;
					f.MetaDone = true;
				}
			}
			AvatarIndex.Register(avatarId, safeName, safeAuthor, safeImg);
			Bump();
			LastStatus = "saved to your Archive favourites";

			// 3. Sync to Archive bridge if connected
			if (VaAuth.InsideClient)
			{
				try { _ = VaAuth.FavAsync("add", avatarId); } catch { }
			}

			return true;
		}

		public static async System.Threading.Tasks.Task<bool> RemoveAsync(string avatarId)
		{
			if (string.IsNullOrEmpty(avatarId)) return false;

			// 1. Remove from VRCX Local SQLite database
			try
			{
				VrcxLocalFavorites.RemoveFavorite(avatarId);
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Favorites] VrcxLocalFavorites.RemoveFavorite failed: " + ex.Message);
			}

			// 2. Immediately update memory
			lock (Gate)
			{
				if (ById.Remove(avatarId))
				{
					Ids.RemoveAll(x => string.Equals(x, avatarId, StringComparison.OrdinalIgnoreCase));
					Items.RemoveAll(x => string.Equals(x.Id, avatarId, StringComparison.OrdinalIgnoreCase));
				}
			}
			Bump();
			LastStatus = "removed from your Archive favourites";

			// 3. Sync to Archive bridge if connected
			if (VaAuth.InsideClient)
			{
				try { _ = VaAuth.FavAsync("remove", avatarId); } catch { }
			}

			return true;
		}

		private static string Error(string raw)
		{
			try
			{
				using var d = JsonDocument.Parse(raw);
				if (d.RootElement.TryGetProperty("error", out var e)) return e.GetString();
			}
			catch { }
			return null;
		}
	}
}
