using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// FAVOURITES OF ANY KIND — worlds, users and groups — resolved to name + thumbnail.
	//
	// FavoritesModule does exactly this for avatars and is the template. Rather than copy it three
	// more times, this is the same three layers (ids, metadata, thumbnail) with the kind as a
	// parameter, so the mod's own FAVORIS tab can show every type with the same cards.
	//
	// Ids come from the modules that already fetch them where one exists (worlds, users), so nothing
	// is requested twice; groups have no such module, so this fetches that list itself. Everything
	// goes through the desktop client's bridge — the account session never reaches this process.
	public class KindFavoritesModule : IModule
	{
		public override string Name => "KindFavorites";

		public static readonly Provider Worlds = new Provider("world", "wrld_", () => WorldFavoritesModule.Snapshot(), () => WorldFavoritesModule.Revision);
		public static readonly Provider Users  = new Provider("user",  "usr_",  () => UserFavoritesModule.Snapshot(),  () => UserFavoritesModule.Revision);
		public static readonly Provider Groups = new Provider("group", "grp_",  null, null);   // fetches its own list

		public override void OnUpdate()
		{
			// Cheap unless the tab is actually being looked at: each provider only works when it has
			// been asked for recently (Touch()), so an unopened section costs nothing.
			Worlds.Tick(); Users.Tick(); Groups.Tick();
		}

		public override void OnSceneLoaded(int buildIndex) { Worlds.DropTextures(); Users.DropTextures(); Groups.DropTextures(); }

		public sealed class Item
		{
			public string Id;
			public string Name = "";
			public string Author = "";
			public string Image = "";
			public bool MetaDone;
			public int ThumbState;      // 0 idle, 1 fetching, 2 ready, 3 failed
			public Texture2D Thumb;
		}

		public sealed class Provider
		{
			public readonly string Kind;
			private readonly string _prefix;
			private readonly Func<List<string>> _ids;
			private readonly Func<int> _rev;

			private readonly List<Item> _items = new List<Item>();
			private readonly Dictionary<string, Item> _byId = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
			private readonly object _gate = new object();
			private readonly ConcurrentQueue<(Item it, byte[] data)> _decoded = new ConcurrentQueue<(Item, byte[])>();

			private int _lastRev = int.MinValue;
			private float _nextMeta, _nextList, _wanted;
			private bool _metaBusy, _listBusy;
			private int _inflight;
			private const int MaxInflight = 4;

			public string Status = "idle";
			public int MetaPending { get; private set; }

			public Provider(string kind, string prefix, Func<List<string>> ids, Func<int> rev)
			{ Kind = kind; _prefix = prefix; _ids = ids; _rev = rev; }

			public List<Item> Items() { lock (_gate) return new List<Item>(_items); }
			public int Count { get { lock (_gate) return _items.Count; } }

			// The menu calls this while drawing the section, so a section nobody looks at does no work.
			public void Touch() { _wanted = VaClock.Now + 5f; }

			public void Tick()
			{
				try
				{
					if (!VaAuth.InsideClient) { Status = "needs the desktop client"; return; }
					float now = VaClock.Now;

					DrainThumbs(2);
					if (now > _wanted) return;      // not being looked at

					if (_ids != null)
					{
						int rev = _rev != null ? _rev() : 0;
						if (rev != _lastRev) { _lastRev = rev; Sync(_ids()); }
					}
					else if (now >= _nextList && !_listBusy)
					{
						_nextList = now + 120f;
						_listBusy = true;
						_ = FetchListAsync();
					}

					if (now >= _nextMeta) { _nextMeta = now + 1f; PumpMeta(); }
					PumpThumbs();
				}
				catch (Exception e) { Status = "failed: " + e.Message; }
			}

			private void Sync(List<string> ids)
			{
				lock (_gate)
				{
					var keep = new Dictionary<string, Item>(_byId, StringComparer.OrdinalIgnoreCase);
					_items.Clear(); _byId.Clear();
					foreach (var id in ids)
					{
						if (string.IsNullOrEmpty(id) || _byId.ContainsKey(id)) continue;
						Item it = keep.TryGetValue(id, out var old) ? old : new Item { Id = id, Name = "" };
						_items.Add(it); _byId[id] = it;
					}
				}
				Status = Count + " " + Kind + "(s)";
			}

			private async System.Threading.Tasks.Task FetchListAsync()
			{
				try
				{
					var (ok, raw, code) = await VaAuth.FavRawAsync(
						"{\"action\":\"list\",\"kind\":\"" + Kind + "\",\"id\":\"\"}");
					if (!ok) { Status = "list failed (" + code + ")"; return; }
					Sync(Collect(raw));
				}
				catch (Exception e) { Status = "list failed: " + e.Message; }
				finally { _listBusy = false; }
			}

			// Every id of our prefix in the response, in order, deduped — the same shape-agnostic
			// scrape the avatar and world lists use.
			private List<string> Collect(string raw)
			{
				var outp = new List<string>();
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (string.IsNullOrEmpty(raw)) return outp;
				int i = 0;
				while (true)
				{
					int at = raw.IndexOf(_prefix, i, StringComparison.Ordinal);
					if (at < 0) break;
					int end = at + _prefix.Length;
					while (end < raw.Length)
					{
						char c = raw[end];
						if (char.IsLetterOrDigit(c) || c == '-') end++; else break;
					}
					string id = raw.Substring(at, end - at);
					if (id.Length >= _prefix.Length + 30 && seen.Add(id)) outp.Add(id);
					i = end;
				}
				return outp;
			}

			// ------------------------------------------------------------------ metadata
			private void PumpMeta()
			{
				if (_metaBusy) return;
				var batch = new List<Item>(100);
				lock (_gate)
				{
					foreach (var it in _items) { if (!it.MetaDone) { batch.Add(it); if (batch.Count >= 100) break; } }
					int pend = 0;
					foreach (var it in _items) if (!it.MetaDone) pend++;
					MetaPending = pend;
				}
				if (batch.Count == 0) return;
				_metaBusy = true;
				_ = ResolveAsync(batch);
			}

			private async System.Threading.Tasks.Task ResolveAsync(List<Item> batch)
			{
				try
				{
					var sb = new StringBuilder("{\"action\":\"meta\",\"kind\":\"").Append(Kind).Append("\",\"ids\":[");
					for (int i = 0; i < batch.Count; i++)
					{
						if (i > 0) sb.Append(',');
						sb.Append('"').Append(batch[i].Id).Append('"');
					}
					sb.Append("]}");

					var (ok, raw, _) = await VaAuth.FavRawAsync(sb.ToString());
					if (!ok) { foreach (var b in batch) b.MetaDone = true; return; }

					using var doc = JsonDocument.Parse(raw);
					if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object)
					{
						foreach (var p in res.EnumerateObject())
						{
							string key = p.Name;                 // "world:wrld_xxx" / "user:usr_xxx"
							int c = key.IndexOf(':');
							string id = c >= 0 ? key.Substring(c + 1) : key;
							Item it;
							lock (_gate) { _byId.TryGetValue(id, out it); }
							if (it == null) continue;

							string title = Str(p.Value, "title");
							string image = Str(p.Value, "image");
							string author = Str(p.Value, "authorName");
							// World OG titles read "Name by Author"; users/groups are a plain name.
							if (Kind == "world" && !string.IsNullOrEmpty(title))
							{
								int at = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
								if (at > 0)
								{
									if (string.IsNullOrWhiteSpace(author)) author = title.Substring(at + 4).Trim();
									title = title.Substring(0, at).Trim();
								}
							}
							if (!string.IsNullOrWhiteSpace(title)) it.Name = title;
							if (!string.IsNullOrWhiteSpace(author)) it.Author = author;
							if (!string.IsNullOrWhiteSpace(image)) it.Image = image;
						}
					}
					foreach (var b in batch) b.MetaDone = true;
				}
				catch { foreach (var b in batch) b.MetaDone = true; }
				finally { _metaBusy = false; }
			}

			// ------------------------------------------------------------------ thumbnails
			private void PumpThumbs()
			{
				List<Item> snap; lock (_gate) snap = new List<Item>(_items);
				foreach (var it in snap)
				{
					if (_inflight >= MaxInflight) return;
					if (it.ThumbState != 0 || !it.MetaDone || string.IsNullOrEmpty(it.Image)) continue;
					it.ThumbState = 1; _inflight++;
					_ = FetchThumbAsync(it);
				}
			}

			private async System.Threading.Tasks.Task FetchThumbAsync(Item it)
			{
				byte[] data = null;
				try
				{
					string cache = Path.Combine(ThumbDir, it.Id + ".img");
					try { if (File.Exists(cache)) data = File.ReadAllBytes(cache); } catch { }
					if (data == null || data.Length < 100)
					{
						data = await GetImageAsync(Small(it.Image));
						if (data == null) data = await GetImageAsync(it.Image);
						if (data != null && data.Length >= 100) { try { File.WriteAllBytes(cache, data); } catch { } }
					}
				}
				catch { }
				finally { _decoded.Enqueue((it, data)); }
			}

			private void DrainThumbs(int max)
			{
				for (int n = 0; n < max; n++)
				{
					if (!_decoded.TryDequeue(out var q)) return;
					_inflight--; if (_inflight < 0) _inflight = 0;
					try
					{
						if (q.data == null || q.data.Length < 100) { q.it.ThumbState = 3; continue; }
						var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
						{ hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
						if (!ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(q.data)))
						{ q.it.ThumbState = 3; continue; }
						q.it.Thumb = tex;
						q.it.ThumbState = 2;
					}
					catch { q.it.ThumbState = 3; }
				}
			}

			// Textures belong to the scene that is going away; forget them so they are re-decoded.
			public void DropTextures()
			{
				lock (_gate)
					foreach (var it in _items) { it.Thumb = null; if (it.ThumbState == 2) it.ThumbState = 0; }
			}

			private async System.Threading.Tasks.Task<byte[]> GetImageAsync(string url)
			{
				try
				{
					if (string.IsNullOrEmpty(url)) return null;
					string body = "{\"action\":\"image\",\"kind\":\"" + Kind + "\",\"url\":\"" + url.Replace("\"", "") + "\"}";
					var (ok, raw, _) = await VaAuth.FavRawAsync(body);
					if (!ok) return null;
					using var doc = JsonDocument.Parse(raw);
					if (!doc.RootElement.TryGetProperty("b64", out var b) || b.ValueKind != JsonValueKind.String) return null;
					return Convert.FromBase64String(b.GetString());
				}
				catch { return null; }
			}

			private static string Str(JsonElement e, string prop)
				=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

			private static string ThumbDir
			{
				get
				{
					string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "thumbs");
					try { Directory.CreateDirectory(d); } catch { }
					return d;
				}
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
		}
	}
}
