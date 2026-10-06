using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRC.Core;
using VRC.DataModel;
using VRChatArchiveMod.Core;

using Category = ObjectPublicStBo1BoILSt1NuBoInUnique;
using Panel    = MonoBehaviourPublicOb_aGa_c_aOb_sGa_e_lUnique;
using Section  = MonoBehaviour1PublicGr_lGa_pCa_fILBoInBoUnique;
using DmBase   = ObjectPublicAbstractObStStObObUnique<VRC.Core.ApiAvatar>;
using Il2IList = Il2CppSystem.Collections.IList;

namespace VRChatArchiveMod.Modules
{
	public class AvatarSearchResult
	{
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public string AuthorId { get; set; } = "";
		public string AuthorName { get; set; } = "";
		public string Description { get; set; } = "";
		public string ImageUrl { get; set; } = "";
		public string ThumbnailImageUrl { get; set; } = "";
		public string ReleaseStatus { get; set; } = "public";
	}

	public class AvatarSearchModule : IModule
	{
		public override string Name => "AvatarSearch";

		private const string VrcdbSearchUrl = "https://vrcx.vrcdb.com/avatars/Avatar/VRCX";
		private const string SearchFieldName = "Avatar_Search_Field";

		private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
		})
		{
			Timeout = TimeSpan.FromSeconds(6)
		};

		private static Panel _panel;
		private static Il2CppSystem.Type _panelIl2;
		private static Transform _header;
		private static Transform _options;
		private static GameObject _searchFieldGo;
		private static TMP_Text _searchTextDisplay;
		private static Sprite _searchIconSprite;

		private static bool _isTyping;
		private static string _currentBuffer = "";
		private static float _blinkTimer;
		private static bool _blinkState;
		private static float _nextPoll;

		private static volatile bool _isSearching;
		private static List<AvatarSearchResult> _pendingResults;
		private static string _pendingQuery;
		private static string _lastSearchQuery = "";

		private static Section _wiredSection;
		private static IntPtr _wiredSectionPtr = IntPtr.Zero;
		private static Il2CppSystem.Action<IAvatar> _clickAction;

		private static readonly Dictionary<IntPtr, AvatarSearchResult> OurSearchAvatars = new();
		private static readonly Dictionary<IntPtr, string> OurSearchIds = new();
		private static readonly object _lock = new();

		public override void OnUpdate()
		{
			try
			{
				float now = VaClock.Now;

				// Process completed async search results on the main thread
				if (_pendingResults != null)
				{
					var results = _pendingResults;
					var query = _pendingQuery;
					_pendingResults = null;
					_pendingQuery = null;
					_isSearching = false;

					PopulateSearchResults(query, results);
				}

				// Only run UI hooks when Main Menu is open
				bool menuOpen = false;
				try { menuOpen = Core.QuickMenu.MainVisible; } catch { }
				if (!menuOpen)
				{
					if (_isTyping) SetTypingState(false);
					return;
				}

				// Throttle UI injection checks
				if (now >= _nextPoll)
				{
					_nextPoll = now + 1.5f;

					if (_panel == null) _panel = FindPanel();
					if (_panel != null)
					{
						EnsureSearchUiInjected();
					}
				}

				// Handle typing input on Desktop
				if (_isTyping)
				{
					HandleKeyboardInput(now);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] OnUpdate error: " + e.Message);
			}
		}

		private void EnsureSearchUiInjected()
		{
			try
			{
				if (_panel == null) return;

				Transform side = _panel.transform.Find("Panel_MM_DynamicSidePanel/Side");
				if (side == null) return;

				_header = side.Find("Header");
				_options = side.Find("Options");
				if (_header == null || _options == null) return;

				Transform existing = _header.Find(SearchFieldName);
				if (existing != null)
				{
					_searchFieldGo = existing.gameObject;
					return;
				}

				Transform shop = _header.Find("Shop");
				if (shop == null) return;

				// 1. Extend Header height by 80px (from 279 to 359)
				var headerRt = _header.GetComponent<RectTransform>();
				if (headerRt != null && headerRt.sizeDelta.y < 350f)
				{
					headerRt.sizeDelta = new Vector2(headerRt.sizeDelta.x, 359f);
				}

				// 2. Adjust Options rect so categories below it move down cleanly without overlap
				var optionsRt = _options.GetComponent<RectTransform>();
				if (optionsRt != null && optionsRt.sizeDelta.y > -350f)
				{
					optionsRt.anchoredPosition = new Vector2(optionsRt.anchoredPosition.x, -179.5f);
					optionsRt.sizeDelta = new Vector2(optionsRt.sizeDelta.x, -359f);
				}

				// 3. Find search sprite from header Button_Search if available
				if (_searchIconSprite == null)
				{
					var btnSearch = side.root.Find("Container/MMParent/Panel_MM_Header/Content/HeaderRight/Button_Search/Icon");
					if (btnSearch != null)
					{
						var img = btnSearch.GetComponent<Image>();
						if (img != null && img.sprite != null) _searchIconSprite = img.sprite;
					}
				}

				// 4. Clone Shop button to create Avatar_Search_Field
				_searchFieldGo = GameObject.Instantiate(shop.gameObject, _header);
				_searchFieldGo.name = SearchFieldName;
				_searchFieldGo.transform.SetParent(_header, false);

				var searchRt = _searchFieldGo.GetComponent<RectTransform>();
				if (searchRt != null)
				{
					searchRt.anchoredPosition = new Vector2(0f, -271f); // Placed 76px below Shop's -195px
					searchRt.sizeDelta = new Vector2(-32f, 72f);
					searchRt.pivot = new Vector2(0.5f, 1f);
				}

				// 5. Update icon and text
				var iconT = _searchFieldGo.transform.Find("Background_Field/Icon");
				if (iconT != null && _searchIconSprite != null)
				{
					var iconImg = iconT.GetComponent<Image>();
					if (iconImg != null) iconImg.sprite = _searchIconSprite;
				}

				var textT = _searchFieldGo.transform.Find("Background_Field/Text_FieldContent");
				if (textT != null)
				{
					_searchTextDisplay = textT.GetComponent<TMP_Text>();
					if (_searchTextDisplay != null)
					{
						_searchTextDisplay.text = string.IsNullOrEmpty(_lastSearchQuery) ? "Search Avatars..." : _lastSearchQuery;
					}
				}

				// 6. Setup button click listener
				var btn = _searchFieldGo.GetComponent<Button>();
				if (btn != null)
				{
					btn.onClick.RemoveAllListeners();
					btn.onClick.AddListener(new Action(() =>
					{
						ToggleTypingOrOpenKeyboard();
					}));
				}

				VRChatArchiveModPlugin.Logger.LogInfo("[AvatarSearch] Search UI successfully injected under Shop button.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] Injection error: " + e.Message);
			}
		}

		private void ToggleTypingOrOpenKeyboard()
		{
			if (_isSearching)
			{
				Core.Toast.Show("Search in progress, please wait...");
				return;
			}

			// Toggle typing state
			SetTypingState(!_isTyping);
		}

		private void SetTypingState(bool typing)
		{
			_isTyping = typing;
			if (_isTyping)
			{
				_currentBuffer = "";
				if (_searchTextDisplay != null) _searchTextDisplay.text = "|";
				Core.Toast.Show("Type avatar name and press Enter");
			}
			else
			{
				if (_searchTextDisplay != null)
				{
					_searchTextDisplay.text = string.IsNullOrEmpty(_lastSearchQuery) ? "Search Avatars..." : _lastSearchQuery;
				}
			}
		}

		private void HandleKeyboardInput(float now)
		{
			// Enter pressed: submit query
			if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
			{
				string query = _currentBuffer.Trim();
				SetTypingState(false);
				if (!string.IsNullOrEmpty(query))
				{
					StartAvatarSearch(query);
				}
				return;
			}

			// Escape pressed: cancel typing
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				SetTypingState(false);
				return;
			}

			// Backspace pressed: remove character
			if (Input.GetKeyDown(KeyCode.Backspace))
			{
				if (_currentBuffer.Length > 0)
				{
					_currentBuffer = _currentBuffer.Substring(0, _currentBuffer.Length - 1);
				}
			}

			// Read typed characters
			string input = Input.inputString;
			if (!string.IsNullOrEmpty(input))
			{
				foreach (char c in input)
				{
					if (c == '\b' || c == '\r' || c == '\n') continue;
					if (c >= ' ' && _currentBuffer.Length < 60)
					{
						_currentBuffer += c;
					}
				}
			}

			// Cursor blink effect
			if (now >= _blinkTimer)
			{
				_blinkTimer = now + 0.45f;
				_blinkState = !_blinkState;
			}

			if (_searchTextDisplay != null)
			{
				_searchTextDisplay.text = _currentBuffer + (_blinkState ? "|" : " ");
			}
		}

		public static void StartAvatarSearch(string query)
		{
			if (string.IsNullOrWhiteSpace(query))
			{
				Core.Toast.Show("Please enter an avatar name to search.");
				return;
			}

			if (_isSearching)
			{
				Core.Toast.Show("A search is already in progress...");
				return;
			}

			_isSearching = true;
			_lastSearchQuery = query;
			if (_searchTextDisplay != null) _searchTextDisplay.text = query;

			Core.Toast.Show($"Searching VRCDB for \"{query}\"...");

			// Update header title immediately to show searching
			UpdateHeaderTitle($"Searching: \"{query}\"...");

			Task.Run(async () =>
			{
				try
				{
					string encodedQuery = Uri.EscapeDataString(query);
					string url = $"{VrcdbSearchUrl}?search={encodedQuery}&n=500";

					using var req = new HttpRequestMessage(HttpMethod.Get, url);
					req.Headers.Add("Referer", "https://vrcx.app");
					req.Headers.Add("User-Agent", "VRCX");

					using var resp = await _httpClient.SendAsync(req);
					if (!resp.IsSuccessStatusCode)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[AvatarSearch] API HTTP error: {resp.StatusCode}");
						_pendingResults = new List<AvatarSearchResult>();
						_pendingQuery = query;
						return;
					}

					string jsonStr = await resp.Content.ReadAsStringAsync();
					using var doc = JsonDocument.Parse(jsonStr);

					var rawAvatars = new List<AvatarSearchResult>();
					if (doc.RootElement.ValueKind == JsonValueKind.Array)
					{
						foreach (var el in doc.RootElement.EnumerateArray())
						{
							string id = el.TryGetProperty("id", out var pId) ? pId.GetString() ?? "" : "";
							if (!id.StartsWith("avtr_")) continue;

							string name = "";
							if (el.TryGetProperty("avatarName", out var pName)) name = pName.GetString() ?? "";
							else if (el.TryGetProperty("name", out var pName2)) name = pName2.GetString() ?? "";

							string authorName = el.TryGetProperty("authorName", out var pAn) ? pAn.GetString() ?? "" : "";
							string authorId = el.TryGetProperty("authorId", out var pAi) ? pAi.GetString() ?? "" : "";
							string desc = el.TryGetProperty("description", out var pDesc) ? pDesc.GetString() ?? "" : "";
							string img = el.TryGetProperty("imageUrl", out var pImg) ? pImg.GetString() ?? "" : "";
							string thumb = el.TryGetProperty("thumbnailImageUrl", out var pTh) ? pTh.GetString() ?? "" : "";
							string status = el.TryGetProperty("releaseStatus", out var pSt) ? pSt.GetString() ?? "public" : "public";

							rawAvatars.Add(new AvatarSearchResult
							{
								Id = id,
								Name = name,
								AuthorName = authorName,
								AuthorId = authorId,
								Description = desc,
								ImageUrl = img,
								ThumbnailImageUrl = thumb,
								ReleaseStatus = status
							});
						}
					}

					VRChatArchiveModPlugin.Logger.LogInfo($"[AvatarSearch] VRCDB returned {rawAvatars.Count} raw records for '{query}'. Filtering deleted & private avatars...");

					// FILTERING ENGINE:
					// Run concurrent checks on avatar image/thumbnail URLs.
					// If VRChat CDN returns HTTP 404, the avatar is deleted or private!
					// Only keep avatars whose assets return 200 OK.
					var validList = new List<AvatarSearchResult>();
					var semaphore = new SemaphoreSlim(16);

					var tasks = rawAvatars.Select(async av =>
					{
						string checkUrl = !string.IsNullOrEmpty(av.ThumbnailImageUrl) ? av.ThumbnailImageUrl : av.ImageUrl;
						if (string.IsNullOrEmpty(checkUrl)) return;
						if (av.ReleaseStatus != null && !av.ReleaseStatus.Equals("public", StringComparison.OrdinalIgnoreCase)) return;

						await semaphore.WaitAsync();
						try
						{
							using var headReq = new HttpRequestMessage(HttpMethod.Head, checkUrl);
							headReq.Headers.Add("User-Agent", "Mozilla/5.0");
							using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
							using var headResp = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);

							if (headResp.IsSuccessStatusCode)
							{
								lock (validList) validList.Add(av);
							}
						}
						catch
						{
							// 404 or connection failure: avatar is dead/private
						}
						finally
						{
							semaphore.Release();
						}
					});

					await Task.WhenAll(tasks);

					VRChatArchiveModPlugin.Logger.LogInfo(
						$"[AvatarSearch] Filtered: {validList.Count} public active avatars kept ({rawAvatars.Count - validList.Count} dead/private discarded).");

					_pendingResults = validList;
					_pendingQuery = query;
				}
				catch (Exception ex)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] Query error: " + ex.Message);
					_pendingResults = new List<AvatarSearchResult>();
					_pendingQuery = query;
				}
			});
		}

		private static void PopulateSearchResults(string query, List<AvatarSearchResult> results)
		{
			try
			{
				if (_panel == null) _panel = FindPanel();
				if (_panel == null)
				{
					Core.Toast.Show("Avatar menu not open.");
					return;
				}

				var view = _panel._avatarListView;
				if (view == null)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] _avatarListView is null.");
					return;
				}

				// Update page title header
				UpdateHeaderTitle($"Search: \"{query}\" ({results.Count})");
				Core.Toast.Show($"Found {results.Count} public avatars for '{query}'");

				if (results.Count == 0)
				{
					// Empty list
					var emptyList = new Il2CppSystem.Collections.Generic.List<IAvatar>();
					var emptySrc = new ReactiveProperty<Il2IList>(emptyList.TryCast<Il2IList>())
						.TryCast<InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique<Il2IList>>();
					view.Method_Public_Void_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_IList_Boolean_0(emptySrc, false);
					return;
				}

				var list = new Il2CppSystem.Collections.Generic.List<IAvatar>();
				lock (_lock)
				{
					OurSearchAvatars.Clear();
					OurSearchIds.Clear();
				}

				foreach (var item in results)
				{
					if (string.IsNullOrEmpty(item.Id)) continue;

					ApiAvatar a = API.FromCacheOrNew<ApiAvatar>(item.Id);
					if (a != null)
					{
						a.name = item.Name ?? "";
						a.authorName = item.AuthorName ?? "";
						a.authorId = item.AuthorId ?? "";
						a.imageUrl = item.ImageUrl ?? "";
						a.thumbnailImageUrl = item.ThumbnailImageUrl ?? "";
						a.releaseStatus = "public";
					}

					var dm = new Object1PublicOb1ILOb1CaILNuIn1Unique();
					dm.Method_Public_Virtual_Void_ApiAvatar_0(a);
					try { ((DmBase)dm).prop_String_0 = item.Id; } catch { }

					var av = dm.TryCast<IAvatar>();
					if (av != null)
					{
						list.Add(av);
						lock (_lock)
						{
							OurSearchAvatars[av.Pointer] = item;
							OurSearchIds[av.Pointer] = item.Id;
						}
					}
				}

				var src = new ReactiveProperty<Il2IList>(list.TryCast<Il2IList>())
					.TryCast<InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique<Il2IList>>();
				if (src != null)
				{
					view.Method_Public_Void_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_IList_Boolean_0(src, false);
					WireClick(view);
					VRChatArchiveModPlugin.Logger.LogInfo($"[AvatarSearch] Avatar grid populated with {list.Count} search cards.");
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] PopulateSearchResults error: " + e.Message);
			}
		}

		private static void WireClick(Section view)
		{
			try
			{
				if (view == null) return;
				if (_clickAction != null && _wiredSectionPtr == view.Pointer) return;

				_clickAction = Core.Il2CppDelegates.TryConvert<Il2CppSystem.Action<IAvatar>>(
					(Action<IAvatar>)OnCardClicked, "AvatarSearch");
				if (_clickAction == null) return;

				view.Method_Public_add_Void_Action_1_IAvatar_0(_clickAction);
				_wiredSectionPtr = view.Pointer;
				_wiredSection = view;

				VRChatArchiveModPlugin.Logger.LogInfo("[AvatarSearch] Card click handler wired.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] WireClick error: " + e.Message);
			}
		}

		private static void OnCardClicked(IAvatar av)
		{
			try
			{
				if (av == null) return;

				string avatarId = null;
				lock (_lock)
				{
					if (!OurSearchIds.TryGetValue(av.Pointer, out avatarId)) return;
				}
				if (string.IsNullOrEmpty(avatarId)) return;

				VRChatArchiveModPlugin.Logger.LogInfo($"[AvatarSearch] Card clicked: {avatarId}");

				// Trigger API fetch so VRChat gets full avatar model package
				try { API.Fetch<ApiAvatar>(avatarId); } catch { }

				var panel = _panel;
				if (panel == null) return;
				var pane = panel._selectedAvatarPanel;
				if (pane == null) return;

				// Open right-side avatar preview panel (hedgehog preview area)
				pane.Method_Public_UniTask_1_Boolean_IAvatar_Boolean_Boolean_Boolean_Boolean_String_0(av);

				// Update Archive modules so "Save to Archive" button knows the selected avatar
				ArchiveHijackModule.LastPreviewedId = avatarId;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[AvatarSearch] OnCardClicked error: " + e.Message);
			}
		}

		private static void UpdateHeaderTitle(string title)
		{
			try
			{
				if (_panel == null) return;
				var header = _panel.transform.Find("Panel_MM_DynamicSidePanel/Main/Panel_MM_Avatars/Header_MM_H2");
				if (header != null)
				{
					var titleT = header.Find("LeftItemContainer/Text_Title");
					if (titleT != null)
					{
						var tmp = titleT.GetComponent<TMP_Text>();
						if (tmp != null) tmp.text = title;
					}
				}
			}
			catch { }
		}

		private static Panel FindPanel()
		{
			try
			{
				if (_panelIl2 == null) _panelIl2 = Il2CppType.Of<Panel>();
				var found = Resources.FindObjectsOfTypeAll(_panelIl2);
				if (found == null) return null;
				for (int i = 0; i < found.Length; i++)
				{
					var o = found[i];
					if (o == null) continue;
					Panel p = null;
					try { p = o.TryCast<Panel>(); } catch { continue; }
					if (p == null) continue;
					try { if (!p.gameObject.scene.IsValid()) continue; } catch { continue; }
					return p;
				}
			}
			catch { }
			return null;
		}
	}
}
