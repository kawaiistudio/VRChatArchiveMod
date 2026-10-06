using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
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
	// VRCHAT ARCHIVE — by TAKING OVER an existing avatar category instead of inventing one.
	//
	// The previous attempt built a new category record and published it. It compiled — every
	// obfuscated name, field and constructor resolved — and then crashed the game on menu open,
	// with a NullReferenceException inside VRChat's own generic wrapper. Compiling proves a member
	// exists; it proves nothing about what the native code expects to find INSIDE it. A record has
	// fields whose meaning we never established, and the game dereferences them.
	//
	// So this fabricates none of that. It picks a category VRChat itself built — every field
	// already correct, including the ones we cannot name — renames the ROW'S TEXT in the UI, and
	// swaps only the grid's CONTENT when that row is selected. No record is constructed, no list is
	// republished (which is what broke selection), no fetchable is invented.
	//
	// One object is still built by hand: the IList of IAvatar handed to the grid. It is built
	// LAZILY, on the click, so a failure there can never take the menu down on open.
	public class ArchiveHijackModule : IModule
	{
		public override string Name => "ArchiveHijack";

		// The slot to take over, most-wanted first. SDK Test Avatars is empty for anyone who has
		// not uploaded from the SDK, which makes it the cheapest thing in the list to borrow.
		// SDK Test Avatars was the wrong shelf, and it explains the Apply trouble: that category
		// holds LOCAL SDK builds, so VRChat's own Apply takes a different route for anything living
		// in it. A VRC+ favourites slot means "avatars I saved to wear", which is exactly what ours
		// are. Configurable, because which slot is expendable is the user's call, not ours.
		private static string[] Preferred
		{
			get
			{
				string chosen = "";
				try { chosen = (ModConfig.ArchiveCategoryName.Value ?? "").Trim(); } catch { }
				if (chosen.Length == 0) return new[] { "SDK Test Avatars", "Fallbacks", "Other" };
				return new[] { chosen, "SDK Test Avatars", "Fallbacks", "Other" };
			}
		}
		private const string Title = "ARCHIVE FAVORITES";

		public static string Status = "off";
		public static bool Active { get; private set; }

		private static string _targetId = "";
		private static Panel _panel;
		private static bool _hooked;
		private float _next;

		private static bool _panelLogged;
		private static float _lastWaitLog;
		private static int _findFails;

		public override void OnUpdate()
		{
			try
			{
				// No gate: the Archive category is always on. It used to check a setting that could
				// be switched off from the client, and this early return is where the feature went
				// to die in silence.

				// Behind the guard and inside the try, both deliberately. This touches obfuscated
				// VRChat UI types whose generated names change with the game build, so on a build the
				// mod has not been rebuilt against it throws TypeLoadException the moment it is
				// called — every frame, ~100 times a second, which floods the log and costs real
				// frame time. Behind the guard, turning the feature off actually turns it off; inside
				// the try, a build mismatch degrades this one feature instead of spamming.
				PumpPendingPreview();

				// Runs every frame — it rate-limits itself, and holding the fetches back to the 2s
				// tick below would make a 147-avatar list take five minutes to fill in.
				PumpFetches();

				float now = VaClock.Now;
				if (now < _next) return;
				_next = now + 2f;

				// CACHE THE PANEL. This used to run a process-wide Resources scan every two
				// seconds forever, which the spike hunter measured at 48 ms/s — the panel does not
				// move once the menu exists, so look for it only while we do not have one.
				// A SEARCH THAT CANNOT SUCCEED MUST STOP SEARCHING.
				//
				// FindPanel deliberately stopped caching a panel that holds no categories, so that it
				// would keep looking for the real one. On 1903 there IS no real one -- the obfuscated
				// panel type was reassigned to another class -- so "keep looking" became a full
				// Resources.FindObjectsOfTypeAll sweep plus a category read per candidate, every two
				// seconds, for ever. The profiler caught it at ArchiveHijack=641 ms/s: a dead feature
				// costing two thirds of the mod's entire budget.
				//
				// Ten attempts is generous for a panel that appears the moment the avatar menu opens.
				// After that this stands down for the session; ArchiveFavGridModule renders the Archive
				// favourites without borrowing any VRChat type, so nothing is lost by stopping.
				if (_panel == null && _findFails < 10) _panel = FindPanel();
				if (_panel == null)
				{
					if (_findFails < 10 && ++_findFails >= 10)
					{
						Status = "avatar-menu panel not resolvable on this build — ARCHIVE FAVORITES is drawn by the grid instead";
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[ArchiveHijack] panneau introuvable apres 10 essais — le type obfusque 1886 pointe sur une autre "
							+ "classe sur ce build. Module en veille pour la session (ArchiveFavGrid dessine les favoris).");
					}
					else if (_findFails < 10)
					{
						Status = "waiting for the avatar menu";
						// Said once every ~30s while stuck: the difference between "you haven't opened the
						// avatar menu yet" and "the panel type no longer resolves on this game build".
						if (now - _lastWaitLog > 30f) { _lastWaitLog = now; VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] still cannot find the avatar-menu panel (FindPanel returned null)."); }
					}
					return;
				}
				_findFails = 0;
				if (!_panelLogged) { _panelLogged = true; VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] avatar-menu panel found."); }

				if (string.IsNullOrEmpty(_targetId) && !PickTarget()) return;
				Hook();
				Retitle();

				// THE LIST CHANGED -> REDRAW IT. Save/Remove updated the data immediately, but the
				// grid was only ever built when the category was SELECTED, so a removed avatar sat
				// there until you navigated away and back. FavoritesModule bumps a revision on every
				// change and we follow it.
				int rev = FavoritesModule.Revision;
				if (_showing && rev != _lastRev)
				{
					_lastRev = rev;
					Refill();
				}

				// Safety net only: cards redraw themselves as their records fill in — watched live.
				// This catches any bound before their record existed, and runs only while OUR
				// category is the one on screen.
				if (_showing && _refillAt > 0f && now >= _refillAt) { _refillAt = 0f; Refill(); }

				// RE-ASSERT WHILE OUR CATEGORY IS ON SCREEN (2026-09-13).
				//
				// The borrowed row is SDK Test Avatars, and for most accounts that list is EMPTY.
				// Selecting it starts VRChat's OWN async load for it, which finishes AFTER our
				// postfix has handed the grid our 127 avatars — and hands the grid its empty list,
				// wiping ours. The log shows exactly that: "grid filled from the Archive" and then a
				// blank page. Nothing re-filled afterwards, because the revision had not changed and
				// the single _refillAt shot had already been spent.
				//
				// SETTLE, THEN LEAVE IT ALONE (2026-09-13). Re-assert on a fast cadence ONLY inside
				// the short window opened at selection — long enough to beat VRChat's one async
				// blank, then it stops so the grid is never re-bound (and never flashes) again. A
				// Save/Remove is handled by the revision path above; navigating away clears _showing.
				if (_showing && _reassertAt > 0f && now >= _reassertAt)
				{
					if (now < _reassertUntil) { _reassertAt = now + 0.3f; Refill(); }
					else _reassertAt = 0f;   // window closed: grid stays as filled, no more re-binds
				}
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] " + e.Message);
			}
		}

		public override void OnSceneLoaded(int buildIndex) { _targetId = ""; Active = false; _findFails = 0; }

		// ------------------------------------------------------------------ target

		// READ ONLY. Walks the categories VRChat published and remembers the id of the one we are
		// going to borrow. Nothing is written here.
		private bool PickTarget()
		{
			try
			{
				var obs = _panel.field_Private_ReactiveProperty_1_List_1_ObjectPublicStBo1BoILSt1NuBoInUnique_0;
				var native = obs != null ? obs.prop_T_0 : null;

				// SNAPSHOT, NEVER live[i].
				//
				// This method used to index the il2cpp list directly, and that is `List<T>.get_Item` --
				// mis-bound on VRChat 1903, which the mod reports at startup ("le token 0x060035A6
				// tombe sur une methode de forme 'o5' au lieu de 'g5'"). Calling it jumped into the
				// wrong method and ended the process:
				//
				//     System.AccessViolationException
				//       at Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke
				//       at Il2CppSystem.Collections.Generic.List`1.get_Item(Int32)
				//       at ArchiveHijackModule.PickTarget()
				//
				// Opening ARCHIVE FAVORITE crashed the game every time. Il2CppSeq.Items reads the
				// list's `_items`/`_size` FIELDS instead, which are offset reads, not calls.
				var live = Core.Il2CppSeq.Items(native);

				// SAY WHY, ONCE. PickTarget returned false from three different branches without a word,
				// so "the category never appears" and "the list is empty" and "nothing borrowable" all
				// looked identical -- the same silence that hid every other 1903 break this session. The
				// raw counts separate them: no field, no native list, or an empty snapshot.
				if (live.Count == 0)
				{
					if (!_pickDiag)
					{
						_pickDiag = true;
						int rawSize = -1;
						try { if (native != null) rawSize = native._size; } catch { }
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[ArchiveHijack] aucune categorie a emprunter : obs=" + (obs != null)
							+ " liste native=" + (native != null) + " _size=" + rawSize
							+ " snapshot=" + live.Count + ". Ouvre l'onglet AVATARS pour que VRChat remplisse la liste.");
					}
					Status = "no categories yet";
					return false;
				}
				_pickDiag = false;

				var names = new List<string>();
				Category chosen = null;
				string chosenName = "";

				for (int i = 0; i < live.Count; i++)
				{
					var c = live[i];
					if (c == null) continue;
					string nm = NameOf(c);
					names.Add(nm);
					// NEVER a VRC+ favourites slot. Those lock the moment the subscription lapses —
					// "This list is temporarily locked. Resubscribe to VRC+" — which then covers OUR
					// category and blocks Apply. SDK Test Avatars and the upload shelves never lock.
					if (IsVrcPlus(nm)) continue;
					foreach (string want in Preferred)
					{
						if (!string.Equals(nm, want, StringComparison.OrdinalIgnoreCase)) continue;
						chosen = c; chosenName = nm; break;
					}
					if (chosen != null) break;
				}

				// Nothing preferred present: take the LAST non-VRC+ one rather than guess. Never the
				// first — that is Recently Used, the one most likely to be wanted — and never a VRC+
				// slot, for the same locking reason as above.
				if (chosen == null && live.Count > 1)
				{
					for (int i = live.Count - 1; i >= 1; i--)
					{
						var c = live[i];
						if (c == null) continue;
						string nm = NameOf(c);
						if (IsVrcPlus(nm)) continue;
						chosen = c; chosenName = nm; break;
					}
				}
				if (chosen == null)
				{
					if (!_pickDiag2)
					{
						_pickDiag2 = true;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[ArchiveHijack] aucune categorie empruntable (toutes VRC+ ou une seule) parmi : "
							+ string.Join(", ", names));
					}
					Status = "no borrowable (non-VRC+) category to take over";
					return false;
				}
				_pickDiag2 = false;

				_targetId = chosen.field_Public_String_0 ?? "";
				if (string.IsNullOrEmpty(_targetId)) { Status = "category has no id"; return false; }

				_targetName = chosenName;
				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[ArchiveHijack] taking over '{chosenName}' (id {_targetId}). Categories present: {string.Join(", ", names)}");
				return true;
			}
			catch (Exception e)
			{
				Status = "could not read the categories: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] PickTarget: " + e.Message);
				return false;
			}
		}

		private static string _targetName = "";
		private static bool _pickDiag, _pickDiag2;

		private static string NameOf(Category c)
		{
			try
			{
				// The interface used to carry the value itself (prop_TYPE_0). It does not any more —
				// on this game build it is reduced to the subscribe/unsubscribe pair, and the value
				// lives on the concrete ReactiveProperty behind it. So: read the field as declared,
				// then cast down to the class that actually holds it.
				var o = c.field_Public_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_String_0;
				if (o == null) return "";
				var rp = o.TryCast<ReactiveProperty<string>>();
				return rp != null ? (rp.prop_T_0 ?? "") : "";
			}
			catch { return ""; }
		}

		// A VRChat+ favourites shelf. Its rows are titled "VRC+ Favorites 1..N"; every one of them
		// goes read-only the instant the subscription lapses, which is why we must never borrow one.
		private static bool IsVrcPlus(string name)
			=> !string.IsNullOrEmpty(name) && name.StartsWith("VRC+", StringComparison.OrdinalIgnoreCase);

		// ------------------------------------------------------------------ the row's text

		// The label is changed on the UI OBJECT, not in the data. Rewriting the category's name
		// observable would mean constructing one, and constructing the game's generic wrappers is
		// exactly what crashed. A TMP string costs nothing and cannot fault.
		// The heading over the grid. It is whatever text sits in the page's header and currently
		// reads the borrowed category's own name, so it is matched by that name rather than by a
		// path — the header moves between VRChat versions, the name does not.
		// RENAMED BY ITS TEXT, NEVER BY ITS PATH.
		//
		// This used to walk "Avatars Container" and read "Mask/Text_Name" out of each row. Both are
		// VRChat's own object names, and VRChat renames its menu objects between builds -- on 1903 the
		// container was not found at all, so the sidebar kept reading "SDK Test Avatars" and the log
		// never printed the retitle line. The label we are replacing is a string we already know, and
		// a string cannot rot: every active TMP_Text under the main menu that still reads the borrowed
		// category's name becomes ours. That covers the sidebar row, the page header and any breadcrumb
		// VRChat adds later, with nothing hardcoded but the text itself.
		private int RetitleTexts()
		{
			int done = 0;
			try
			{
				Transform root = Core.QuickMenu.Main();
				if (root == null) return 0;
				var texts = root.GetComponentsInChildren<TMPro.TMP_Text>(false);
				if (texts == null) return 0;
				for (int i = 0; i < texts.Length; i++)
				{
					var t = texts[i];
					if (t == null) continue;
					string s2;
					try { s2 = t.text ?? ""; } catch { continue; }
					if (string.Equals(s2, _targetName, StringComparison.Ordinal))
					{
						try { t.text = Title; done++; } catch { }
					}
					// Already ours from an earlier pass: still counts, otherwise the module would
					// report itself inactive the moment the rename succeeded.
					else if (string.Equals(s2, Title, StringComparison.Ordinal)) done++;
				}
			}
			catch { }
			return done;
		}

		private void Retitle()
		{
			try
			{
				if (string.IsNullOrEmpty(_targetName)) return;

				int done = RetitleTexts();

				if (done > 0)
				{
					if (!Active)
					{
						Active = true;
						Status = "'" + _targetName + "' now shows your Archive favourites";
						VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] row retitled to " + Title + " (" + done + " label(s)).");
					}
					return;
				}

				// SAID ONCE EVERY 30 s WHILE IT FAILS, with the name we are hunting for. "The row still
				// says SDK Test Avatars" and "the menu is not open" produce the same silence otherwise,
				// and that silence is what let this sit broken.
				float now;
				try { now = VaClock.Now; } catch { return; }
				if (now - _retitleWarnAt < 30f) return;
				_retitleWarnAt = now;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] aucun libelle '" + _targetName
					+ "' trouve sous le menu principal — la ligne ne peut pas etre renommee en " + Title
					+ " (menu ferme, ou VRChat n'affiche plus cette categorie).");
			}
			catch { }
		}

		private float _retitleWarnAt = -999f;

		// ------------------------------------------------------------------ the grid

		private void Hook()
		{
			if (_hooked) return;
			try
			{
				// EVERY handler that takes a category, not one picked by name.
				//
				// These names end in _PDM_0, _PDM_1… and that number is just declaration order, which
				// moves whenever VRChat edits the class. The old build had the selection on _PDM_0; this
				// one declares two, and hooking the first by name succeeded while never firing — a hook
				// that reports success and does nothing, which is the worst kind.
				//
				// Patching all of them is safe because the postfix already checks the category is ours:
				// the wrong one matches nothing and returns. It also survives the next reordering.
				var post = new HarmonyLib.HarmonyMethod(typeof(ArchiveHijackModule).GetMethod(
					nameof(AfterSelect),
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));

				int hooked = 0;
				foreach (var m in typeof(Panel).GetMethods(
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
					| System.Reflection.BindingFlags.Instance))
				{
					if (!m.Name.StartsWith("Method_Private_Void_", StringComparison.Ordinal)) continue;
					var ps = m.GetParameters();
					if (ps.Length != 1 || ps[0].ParameterType != typeof(Category)) continue;
					try { VRChatArchiveModPlugin.HarmonyInstance.Patch(m, postfix: post); hooked++; }
					catch (Exception pe) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] " + m.Name + ": " + pe.Message); }
				}

				_hooked = true;
				if (hooked == 0)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] no selection handler takes a category — the grid will keep its own avatars.");
					return;
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] selection hooked on " + hooked + " handler(s).");
			}
			catch (Exception e)
			{
				_hooked = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] hook failed: " + e.Message);
			}
		}

		// Runs AFTER VRChat has selected the borrowed category with its own, valid record — so all
		// of the game's machinery has already run on data it built itself. We only replace what the
		// grid is showing.
		private static string _lastWhy;
		private static bool _selectSeen;

		private static void Once(string why)
		{
			if (why == _lastWhy) return;
			_lastWhy = why;
			VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] grid not filled: " + why + ".");
		}

		private static void AfterSelect(Category __0)
		{
			try
			{
				// Proof the hook fires at all. Without it, "nothing happened" could equally mean the
				// postfix was never called -- a different problem with a different fix.
				if (!_selectSeen)
				{
					_selectSeen = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] selection postfix fired for the first time.");
				}

				// These four exits used to be silent, which made an empty grid indistinguishable from a
				// grid that was never asked to fill. Each says itself ONCE — the postfix runs on every
				// category click, so anything louder would bury the log.
				if (__0 == null || _panel == null) { Once("no category or no panel"); return; }
				if (string.IsNullOrEmpty(_targetId)) { Once("no target category picked yet"); return; }

				// Any OTHER category was selected: ours is no longer on screen, so the refill must
				// not fire and rebind a grid the user has navigated away from.
				string picked = __0.field_Public_String_0;
				if (!string.Equals(picked, _targetId, StringComparison.Ordinal))
				{
					Once("selected '" + (picked ?? "<null>") + "' but we borrowed '" + _targetId + "'");
					_showing = false;
					return;
				}
				_showing = true;
				// A BOUNDED SETTLE WINDOW, NOT A FOREVER TIMER (2026-09-13). VRChat's async load for
				// the borrowed category lands within a few hundred ms of selection and blanks the
				// grid ONCE; re-asserting a handful of times over the next ~2.5 s wins that race. The
				// old code kept re-asserting every 1.5 s for as long as the category was open, and
				// each Refill re-binds the list — a visible flash — so the page flickered "shows /
				// gone / shows" forever. The window closes and we stop touching the grid, which then
				// stays filled.
				float nowSel = VaClock.Now;
				_reassertAt = nowSel + 0.3f;
				_reassertUntil = nowSel + 2.5f;

				var view = _panel._avatarListView;
				if (view == null) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] no grid view."); return; }

				var avatars = BuildAvatars();
				if (avatars == null) { Once("BuildAvatars produced nothing"); return; }

				var src = new ReactiveProperty<Il2IList>(avatars)
					.TryCast<InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique<Il2IList>>();
				if (src == null) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] grid source cast failed."); return; }

				view.Method_Public_Void_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_IList_Boolean_0(src, false);
				WireClick(view);
				Status = "grid filled from the Archive";
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] grid filled from the Archive.");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] fill: " + e.Message); }
		}

		// Hands the same list over again, which rebinds every card to its record.
		private static void Refill()
		{
			try
			{
				if (_panel == null) return;
				var view = _panel._avatarListView;
				if (view == null) return;
				var avatars = BuildAvatars();
				if (avatars == null) return;
				var src = new ReactiveProperty<Il2IList>(avatars)
					.TryCast<InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique<Il2IList>>();
				if (src == null) return;
				view.Method_Public_Void_InterfacePublicAbstractIDisposableVoAc1ObVoAc1ObUnique_1_IList_Boolean_0(src, false);
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] refill: " + e.Message); }
		}

		// ------------------------------------------------------------------ the click
		//
		// NOTHING has to be registered anywhere for a card to open. The whole path — card →
		// section's Action<IAvatar> → the panel's handler → the preview pane — passes the IAvatar
		// OBJECT along; there is no id lookup against the avatar store, and the store has no
		// by-id map to look into. So we do not need the game to know our avatars; we only need
		// the click to reach the pane.
		//
		// We ADD to the section's real event (Delegate.Combine) rather than overwrite the field,
		// so VRChat's own subscriber keeps working for every category but ours. Our handler stays
		// inert unless the clicked avatar is one WE built, and unless the pane did not already
		// take it — so on a normal category this code does nothing at all.
		private static Il2CppSystem.Action<IAvatar> _clickAction;   // kept alive: a collected delegate is a hard crash
		private static IntPtr _wiredSection = IntPtr.Zero;
		private static readonly HashSet<IntPtr> Ours = new HashSet<IntPtr>();
		// Pointer -> avatar id, so a click can say WHICH avatar it was without reading the model back.
		private static readonly Dictionary<IntPtr, string> OurIds = new Dictionary<IntPtr, string>();
		// The avatar id the user last opened FROM our category. The Save/Remove button reads
		// this instead of guessing the id off the pane, which is what made the label wrong.
		public static string LastPreviewedId = "";

		private static void WireClick(Section view)
		{
			try
			{
				if (view == null) return;
				if (_clickAction != null && _wiredSection == view.Pointer) return;

				// Diagnostic worth one line in the log: null here means nobody is listening at all,
				// i.e. the click never had anywhere to go. Non-null means the failure was downstream.
				bool vanilla = false;
				try { vanilla = view.field_Private_Action_1_IAvatar_0 != null; } catch { }

				_clickAction = Core.Il2CppDelegates.TryConvert<Il2CppSystem.Action<IAvatar>>(
					(Action<IAvatar>)OnCardClicked, "ArchiveHijack");
				if (_clickAction == null) return;
				view.Method_Public_add_Void_Action_1_IAvatar_0(_clickAction);
				_wiredSection = view.Pointer;

				VRChatArchiveModPlugin.Logger.LogInfo(
					"[ArchiveHijack] card clicks wired (VRChat's own listener " + (vanilla ? "present" : "ABSENT") + ").");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] click wiring failed: " + e.Message);
			}
		}

		// The click we are still waiting on a record for. Pointer as well as id, because the pane
		// must be re-opened with the SAME IAvatar object the grid holds — a fresh one would not be
		// the row the user clicked.
		private static string _pendingId = "";
		private static IntPtr _pendingPtr = IntPtr.Zero;
		private static float _pendingUntil;

		// Re-open the preview once the record has arrived. Bounded in time: a fetch that never
		// completes (deleted or private avatar) must not leave this checking forever.
		private static void PumpPendingPreview()
		{
			try
			{
				if (string.IsNullOrEmpty(_pendingId)) return;
				if (VaClock.Now > _pendingUntil) { _pendingId = ""; return; }

				var rec = API.FromCacheOrNew<ApiAvatar>(_pendingId);
				bool done = false;
				try { done = rec != null && rec.Populated; } catch { }
				if (!done) return;

				string id = _pendingId;
				IntPtr ptr = _pendingPtr;
				_pendingId = "";

				var panel = _panel;
				var pane = panel?._selectedAvatarPanel;
				if (pane == null) return;

				// Only if the user is STILL looking at that card — re-opening a preview they have
				// moved on from would yank the pane out from under them. And the pane is holding
				// OUR object already, so it is handed straight back rather than looked up again:
				// re-reading the grid would risk a different instance for the same row.
				IAvatar cur;
				try { cur = pane.prop_IAvatar_0; } catch { return; }
				if (cur == null || cur.Pointer != ptr) return;

				pane.Method_Public_UniTask_1_Boolean_IAvatar_Boolean_Boolean_Boolean_Boolean_String_0(cur);
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] preview refreshed once " + id + " finished loading.");
			}
			catch { }
		}

		private static void OnCardClicked(IAvatar av)
		{
			try
			{
				if (av == null) return;
				if (!Ours.Contains(av.Pointer)) return;          // a real category: hands off

				// The exact id of what was just opened from OUR category. Everything in this category
				// is by definition in the Archive, so the Save/Remove button can trust this instead
				// of scanning the pane and guessing — which is why it read "Save" on an avatar that
				// was plainly already saved.
				try { if (OurIds.TryGetValue(av.Pointer, out string oid)) LastPreviewedId = oid ?? ""; } catch { }

				// JUMP THE QUEUE for the one you just clicked.
				//
				// Apply worked "sometimes" for the same reason cards were blank: an avatar still
				// waiting its turn in the fetch queue is an empty record, and VRChat has nothing to
				// apply. Whatever you click is what you want first, so it goes to the front.
				try
				{
					if (OurIds.TryGetValue(av.Pointer, out string clickedId) && !string.IsNullOrEmpty(clickedId))
					{
						var rec = API.FromCacheOrNew<ApiAvatar>(clickedId);
						bool done = false;
						try { done = rec != null && rec.Populated; } catch { }
						if (!done)
						{
							API.Fetch<ApiAvatar>(clickedId);
							// AND COME BACK FOR IT. Jumping the queue was only half the fix: the
							// pane is opened NOW, with a record that is still empty, and pressing
							// Apply on an empty record makes VRChat wear its loading placeholder
							// ("Utility Loading Simple") instead of the avatar on screen. So the
							// click is remembered and the preview is re-opened the moment the
							// record actually fills in — see PumpPendingPreview.
							_pendingId = clickedId;
							_pendingPtr = av.Pointer;
							_pendingUntil = VaClock.Now + 12f;
							VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] priority fetch for " + clickedId);
						}
					}
				}
				catch { }

				var panel = _panel;
				if (panel == null) return;
				var pane = panel._selectedAvatarPanel;
				if (pane == null) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] no preview pane."); return; }

				// VRChat's listener subscribed first, so it has already run. If it committed, leave it.
				try { var cur = pane.prop_IAvatar_0; if (cur != null && cur.Pointer == av.Pointer) return; } catch { }

				// Takes the object, never an id. Returns UniTask<bool> — a refusal is silent, which is
				// why the pane's own avatar is re-read above rather than trusted.
				pane.Method_Public_UniTask_1_Boolean_IAvatar_Boolean_Boolean_Boolean_Boolean_String_0(av);
				VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] preview opened for a card we own.");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] click: " + e.Message);
			}
		}

		// WE SEND IDS. VRCHAT FETCHES THE REST.
		//
		// The first version filled each ApiAvatar with our own scraped metadata and set
		// Populated = true, which told the game "this record is complete, do not go and get it".
		// That was wrong twice over: the names came out as "Name by Author by Author" because our
		// metadata is scraped from the page title, and a record the game never fetched is a record
		// it does not really know — which is the likeliest reason clicking a card opens nothing.
		//
		// A favourite IS an id. VRChat already knows how to turn one into a name, an author, an
		// image, a performance rating and a working preview; that is its data to own, not ours to
		// imitate. So we hand it ids and let it do exactly that.
		private static Il2IList BuildAvatars()
		{
			try
			{
				var ids = FavoritesModule.Snapshot();
				if (ids.Count == 0) return null;

				var list = new Il2CppSystem.Collections.Generic.List<IAvatar>();
				lock (Pending) Pending.Clear();
				Ours.Clear();
				OurIds.Clear();
				int made = 0, known = 0, blocked = 0;

				foreach (string id in ids)
				{
					if (string.IsNullOrEmpty(id)) continue;

					// FromCacheOrNew, not Fetch: this must not fire 147 requests the moment the row
					// is clicked. It returns instantly — the real record if the game has seen this
					// avatar before, an empty shell otherwise — and the shells are filled in
					// afterwards, a few at a time.
					ApiAvatar a = API.FromCacheOrNew<ApiAvatar>(id);
					if (a == null) continue;

					bool populated = false;
					try { populated = a.Populated; } catch { }
					if (populated) known++;
					else lock (Pending) Pending.Enqueue(id);

					// SKIP AVATARS VRCHAT FLAGGED. A failed security scan is what greys the card out
					// and, on some of them, crashes the client when the model is loaded. VRChat's own
					// scanStatus tells us before we ever put the card in the grid. Only avatars the
					// game has actually fetched carry a verdict, so an unfetched one is kept and
					// re-checked on the refill after its fetch lands.
					if (populated && IsBlocked(a)) { blocked++; continue; }

					var dm = new Object1PublicOb1ILOb1CaILNuIn1Unique();
					dm.Method_Public_Virtual_Void_ApiAvatar_0(a);

					// The DataModel carries its own id, separate from the ApiAvatar's. It lives on the
					// GENERIC BASE: the wrapper declares its own prop_String_0 with `new`, so
					// dm.prop_String_0 is a different string entirely and writing it would set the
					// wrong thing. Cast to the base and set the real one, so anything downstream that
					// keys on the model id does not see an empty string.
					try { ((DmBase)dm).prop_String_0 = id; } catch { }

					var av = dm.TryCast<IAvatar>();
					if (av == null) continue;
					list.Add(av);
					Ours.Add(av.Pointer);
					OurIds[av.Pointer] = id;
					made++;
				}

				if (made == 0) { VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] no IAvatar built."); return null; }
				VRChatArchiveModPlugin.Logger.LogInfo(
					$"[ArchiveHijack] built {made} IAvatar ({known} known, {made - known} to fetch, {blocked} scan-failed skipped).");
				return list.TryCast<Il2IList>();
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[ArchiveHijack] BuildAvatars: " + e.Message);
				return null;
			}
		}

		// Fills in the ones VRChat has never seen, a few at a time. Asking for 147 at once would be
		// a burst against the user's own account on VRChat's API — their own menu pages its
		// favourites for the same reason.
		//
		// Per-id is unavoidable here: VRChat's batch endpoint fetches a LIST it owns, and an
		// arbitrary set of ids is not one. So the only lever is the rate. Eight a second clears a
		// 150-avatar list in about twenty seconds instead of forty, and still reads as browsing
		// rather than scraping.
		private static readonly Queue<string> Pending = new Queue<string>();
		private const int PerTick = 8;
		private static float _refillAt;
		// Next re-assert of our list while our category is on screen. Counters VRChat's own async
		// load for the borrowed (usually empty) category, which lands after our fill and blanks it.
		private static float _reassertAt;
		// End of the post-selection settle window: re-asserts run only until here, then stop so the
		// grid is not re-bound (and does not flash) for the rest of the time the category is open.
		private static float _reassertUntil;
		private static bool _showing;
		private static int _lastRev = -1;
		private static float _nextFetch;

		private static void PumpFetches()
		{
			try
			{
				float now = VaClock.Now;
				if (now < _nextFetch) return;
				_nextFetch = now + 1f;

				// AUTO-CLEAN dead ids first (on its own slower cadence). It may re-enqueue anything
				// still unresolved, so it runs BEFORE we read the queue.
				ReapDeadFavourites(now);

				int left;
				lock (Pending) left = Pending.Count;
				if (left == 0) return;

				for (int i = 0; i < PerTick; i++)
				{
					string id = null;
					lock (Pending) { if (Pending.Count > 0) id = Pending.Dequeue(); }
					if (string.IsNullOrEmpty(id)) break;
					if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = now;
					_tries[id] = (_tries.TryGetValue(id, out int t) ? t : 0) + 1;
					_lastTry[id] = now;
					try { API.Fetch<ApiAvatar>(id); }   // the game fills its own record
					catch { }
				}

				// Cards DO redraw on their own as records fill in — watched live. A refill is only
				// a safety net for any that were bound before their record existed at all.
				_refillAt = now + 2f;

				lock (Pending) left = Pending.Count;
				if (left == 0) VRChatArchiveModPlugin.Logger.LogInfo("[ArchiveHijack] all avatars resolved by VRChat.");
			}
			catch { }
		}

		// ------------------------------------------------------------------ auto-clean dead favs
		//
		// Some saved ids have NOTHING behind them on VRChat: avatars the author deleted, ids that
		// never existed, avatars turned private. They never populate however often we ask, and they
		// are the blank "By:" cards — the ones whose tooltip shows the raw "{0} by {1}" because the
		// game has no name or author to fill in. This removes them from the Archive favourites for
		// good, but CONSERVATIVELY: an id is only declared dead after several fetches spread over
		// ~40 s, so a rate-limit or a merely-slow fetch is never mistaken for a missing avatar. A
		// scan-FAILED avatar is NOT dead — it has real data we simply refuse to display — so it is
		// kept. Toggle with Favorites/AutoCleanDeadIds.
		private static readonly Dictionary<string, int> _tries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, float> _firstSeen = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, float> _lastTry = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
		// Ids VRChat will not serve: deleted, privated, or gone. Kept in the Archive, never fetched
		// again, and skipped by the reaper so the retry bookkeeping does not grow forever.
		private static readonly HashSet<string> Unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private const int DeadTries = 4;        // fetch attempts before an id is declared dead
		private const float DeadAge = 40f;      // …and it must have stayed empty at least this long
		private const float RetryEvery = 9f;    // min seconds between our retries for one unresolved id
		private static float _nextReap;

		private static void ReapDeadFavourites(float now)
		{
			// Disabled: never auto-delete favorites (preserves VRCX local favorites)
		}

		// ------------------------------------------------------------------ helpers

		// True when VRChat has flagged this avatar's build as failed — the greyed-out, blocked-eye
		// state, and the one that can crash the client if its model is loaded. scanStatus is the
		// primary signal; a blocked assetUrl is the older shape of the same thing.
		private static bool IsBlocked(ApiAvatar a)
		{
			try
			{
				// Read by reflection: scanStatus lives on the ApiContentModel base and the compile
				// assembly does not surface it on ApiAvatar directly. OfflineAnalysisScanStatus.Failed
				// == 3 (Unknown=0, Unscanned=1, Passed=2, Failed=3).
				var t = a.GetType();
				foreach (string prop in new[] { "scanStatus", "offlineAnalysisScanStatus" })
				{
					try
					{
						var pi = t.GetProperty(prop);
						if (pi == null) continue;
						object v = pi.GetValue(a);
						if (v != null && Convert.ToInt32(v) == 3) return true;
					}
					catch { }
				}
			}
			catch { }
			return false;
		}

		// NON-GENERIC, AND THAT IS THE WHOLE FIX (2026-09-08).
		//
		// This read Resources.FindObjectsOfTypeAll<Panel>() — a GENERIC il2cpp lookup, which returns
		// an EMPTY array on this build. The loop therefore never ran a single iteration, FindPanel
		// returned null every time, and this module has logged "still cannot find the avatar-menu
		// panel (FindPanel returned null)" on every world load for weeks. Nothing was missing from
		// the game: the query could not see it.
		//
		// The mod already documents this trap and already works around it elsewhere — see
		// ObjectGravityModule, whose sweep carries "NON-GENERIC: FindObjectsOfType<VRC_Pickup>()
		// returns nothing on this build". Il2CppType.Of<T>() with the non-generic overload and
		// TryCast<T>() is the form that works here.
		//
		// The il2cpp Type is resolved once: Of<T>() walks the type system, and this is called on a
		// timer for as long as the panel has not been found.
		private static Il2CppSystem.Type _panelIl2;

		// The category list a panel exposes, snapshotted safely. The field name is a 1886 obfuscated
		// name matched by shape; the read goes through the pointer-validated Il2CppSeq.Items, so a wrong
		// panel yields an empty list instead of crashing.
		private static List<Category> CategoriesOf(Panel p)
		{
			try
			{
				var obs = p.field_Private_ReactiveProperty_1_List_1_ObjectPublicStBo1BoILSt1NuBoInUnique_0;
				var native = obs != null ? obs.prop_T_0 : null;
				return Core.Il2CppSeq.Items(native);
			}
			catch { return new List<Category>(); }
		}

		private static bool _panelDiag;

		private static Panel FindPanel()
		{
			try
			{
				if (_panelIl2 == null) _panelIl2 = Il2CppType.Of<Panel>();
				var found = Resources.FindObjectsOfTypeAll(_panelIl2);
				if (found == null) return null;

				// PREFER THE INSTANCE THAT ACTUALLY HOLDS CATEGORIES.
				//
				// The old code took the FIRST scene-valid instance, and on 1903 that was an empty look-alike:
				// PickTarget then read obs=True, native=True, _size=0 and gave up, so the sidebar never became
				// ARCHIVE FAVORITES. Several objects of this type live in the menu (a template, a pooled copy,
				// the live one); only the live one has the categories. Every candidate is checked and the one
				// whose list is non-empty wins; the first scene-valid one is kept only as a fallback.
				Panel firstScene = null;
				int scanned = 0, sceneValid = 0;
				for (int i = 0; i < found.Length; i++)
				{
					var o = found[i];
					if (o == null) continue;
					scanned++;
					Panel p = null;
					try { p = o.TryCast<Panel>(); } catch { continue; }
					if (p == null) continue;
					try { if (!p.gameObject.scene.IsValid()) continue; } catch { continue; }
					sceneValid++;
					if (firstScene == null) firstScene = p;
					if (CategoriesOf(p).Count > 0) return p;   // the real one
				}

				// None held categories: say so once, with the counts, so "wrong panel type on this build"
				// and "menu not open yet" are told apart instead of guessed at.
				if (firstScene != null && !_panelDiag)
				{
					_panelDiag = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[ArchiveHijack] " + sceneValid + " panneau(x) du type attendu, aucun ne porte de categories ("
						+ scanned + " objets scannes). Le type obfusque 1886 pointe probablement sur une autre classe sur ce build.");
				}
				// Not cached: returning null keeps the caller looking every tick instead of locking onto
				// a category-less panel. A real panel (with categories) is returned above and cached.
				return null;
			}
			catch { }
			return null;
		}

		// SCOPED TO THE MENU CANVAS. Resources.FindObjectsOfTypeAll<Transform>() returns every
		// Transform the process has loaded — inactive objects, prefabs and asset hierarchies
		// included — and this is called on a timer. The object being looked for is a menu object,
		// so the menu canvas is the only place worth walking.
		private static Transform FindActive(string name)
		{
			try
			{
				Transform root = Core.QuickMenu.Main() ?? Core.QuickMenu.Root();
				if (root != null)
				{
					var kids = root.GetComponentsInChildren<Transform>(true);
					if (kids != null)
						foreach (var k in kids)
						{
							if (k == null || k.name != name) continue;
							try { if (k.gameObject.activeInHierarchy) return k; } catch { }
						}
					return null;
				}

				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.name != name) continue;
					try
					{
						if (!t.gameObject.scene.IsValid()) continue;
						if (!t.gameObject.activeInHierarchy) continue;
					}
					catch { continue; }
					return t;
				}
			}
			catch { }
			return null;
		}
	}
}
