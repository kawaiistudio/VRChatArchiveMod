using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;
using VRC.Core;

namespace VRChatArchiveMod.Core
{
	// AVATAR PREVIEW — REVERSING THE PANE BY POINTERS ONLY.
	//
	// The goal: clicking a card in ARCHIVE FAVORITES opens VRChat's own 3D preview, exactly like a card
	// in avatars1 does.
	//
	// WHAT KILLED THE LAST ATTEMPT, PRECISELY
	//
	// Not observing, and not MemberAlign. One line: `Il2CppClassPointerStore<IAvatar>.NativeClassPtr`.
	// Merely touching a generated proxy forces its cctor, which resolves the obfuscated name, which on
	// 1903 runs a shape recovery and then il2cpp_runtime_class_init on the recovered class -- and that
	// ended the process. The crash log stops on exactly that recovery line ("58/0 parent
	// Il2CppObjectBase"). Every pointer-only read done before it had been running for minutes without
	// trouble.
	//
	// So the rule here is absolute: NO GENERATED PROXY TYPE IS EVER NAMED. No Il2CppClassPointerStore,
	// no `new` of an obfuscated class, no typed member access, no TryCast. Everything below is IntPtr
	// arithmetic and il2cpp C functions, each guarded by NativeGuard before it is dereferenced.
	//
	// WHAT IS ALREADY PROVEN ON THIS BUILD (measured live, not assumed)
	//
	//   * Panel_MM_DynamicSidePanel carries a component whose SERIALIZED field names survive obfuscation:
	//     _avatarListView, _avatarListPanel, _selectedAvatarPanel, _sortDropdown, ... So the avatar panel
	//     is identified BY NAME on a class whose own name was taken from it. A name cannot tie.
	//   * _selectedAvatarPanel points at the pane: 24 fields, of which 13 are plainly named
	//     (_avatarBounds, _avatarName, _useButton, _previewButton, _detailsButton, ...).
	//   * The pane's MEMBERS are permuted: its generated `prop_IAvatar_0` returned a Single. No typed
	//     member read on this class means anything, which is why none is attempted.
	//
	// WHAT THIS STEP ESTABLISHES
	//
	// Which of the pane's reference fields holds the avatar object, and what that object's class is --
	// by walking offsets and reading class names through il2cpp's own C API. That is the one fact still
	// missing, and it is obtainable without touching anything that can crash.
	internal static unsafe class AvatarPreview
	{
		private const string PanelHost = "Panel_MM_DynamicSidePanel";

		// Handed in by the grid module, which already stands inside the avatars menu. Searching from
		// QuickMenu.Main() reaches the WRIST menu, a different canvas from Canvas_MainMenu where this
		// panel lives -- that mistake made the probe silently find nothing.
		internal static Transform MenuRoot;

		private static IntPtr _panelObj, _panelClass, _paneObj;
		private static float _nextTry, _nextProbe, _saidAt;
		private static bool _saidPane;
		private static string _lastReport;
		private static readonly Dictionary<IntPtr, int> _methodCount = new Dictionary<IntPtr, int>();
		private static readonly Dictionary<IntPtr, bool> _implements = new Dictionary<IntPtr, bool>();

		internal static string Status
		{
			get
			{
				if (_paneObj == IntPtr.Zero) return "panneau non localise";
				return "panneau localise ; sonde du modele en cours";
			}
		}

		// ---------------------------------------------------------------- the click

		internal static string LastOpenedId;
		private static bool _isOpening;
		private static string _currentlyShowingId;
		private static float _lastOpenTime;

		internal static void Open(string id, string name, string author = null, string image = null)
		{
			if (string.IsNullOrEmpty(id)) return;
			float now; try { now = VaClock.Now; } catch { now = 0f; }

			if (_isOpening) return;
			if (string.Equals(id, _currentlyShowingId, StringComparison.Ordinal) && (now - _lastOpenTime < 0.6f))
			{
				return;
			}

			_isOpening = true;
			_currentlyShowingId = id;
			_lastOpenTime = now;
			LastOpenedId = id;

			try
			{
				// Warm VRChat's own record. ApiAvatar is a hand-written, non-obfuscated type: safe, and it
				// is what any later open will need anyway.
				ApiAvatar rec = null;
				bool hasAssets = false;
				try
				{
					rec = API.FromCacheOrNew<ApiAvatar>(id);
					if (rec != null)
					{
						if (string.IsNullOrEmpty(rec.id)) rec.id = id;
						if (string.IsNullOrEmpty(rec.name) && !string.IsNullOrEmpty(name)) rec.name = name;
						if (string.IsNullOrEmpty(rec.authorName) && !string.IsNullOrEmpty(author)) rec.authorName = author;
						var idxRec = AvatarIndex.ById(id);
						if (idxRec != null && !string.IsNullOrEmpty(idxRec.AuthorId)) rec.authorId = idxRec.AuthorId;
						if (string.IsNullOrEmpty(rec.releaseStatus)) rec.releaseStatus = "public";
						if (string.IsNullOrEmpty(rec.imageUrl) && !string.IsNullOrEmpty(image)) rec.imageUrl = image;
						if (string.IsNullOrEmpty(rec.thumbnailImageUrl) && !string.IsNullOrEmpty(image)) rec.thumbnailImageUrl = image;

						hasAssets = !string.IsNullOrEmpty(rec.assetUrl) || (rec.unityPackages != null && rec.unityPackages.Count > 0);
					}

					if (!hasAssets)
					{
						try { API.Fetch<ApiAvatar>(id); } catch { }
					}
					else if (rec != null)
					{
						rec.Populated = true;
					}
				}
				catch { }

				// ---- the open itself ------------------------------------------------------------
				if (_modelCtor == IntPtr.Zero || _bindApi == IntPtr.Zero || _concreteClass == IntPtr.Zero
					|| (_panelSelect == IntPtr.Zero && _paneSelect == IntPtr.Zero))
				{
					if (now - _saidAt < 3f) return;
					_saidAt = now;
					VRChatArchiveModPlugin.Logger.LogInfo("[Preview] " + (string.IsNullOrEmpty(name) ? id : name)
						+ " : le volet n'est pas encore reconnu (ouvre avatars1 une fois).");
					return;
				}

				string flag = OpenFlagPath();
				if (!_openChecked)
				{
					_openChecked = true;
					try
					{
						if (flag != null && System.IO.File.Exists(flag))
						{
							_openDisabled = true;
							System.IO.File.Delete(flag);
							VRChatArchiveModPlugin.Logger.LogWarning("[Preview] l'ouverture 3D n'est pas revenue la session precedente — DESACTIVEE. Le reste du mod fonctionne.");
						}
					}
					catch { }
				}
				if (_openDisabled) return;

				try
				{
					IntPtr api = IntPtr.Zero;
					if (rec != null) api = rec.Pointer;
					if (api == IntPtr.Zero) return;

					try { if (flag != null) System.IO.File.WriteAllText(flag, "1"); } catch { }
					try
					{
						IntPtr model = Invoke1(_modelCtor, IntPtr.Zero, IntPtr.Zero, _concreteClass);
						if (model != IntPtr.Zero)
						{
							Invoke1(_bindApi, model, api, IntPtr.Zero);
							if (_paneSelect != IntPtr.Zero && _paneObj != IntPtr.Zero)
								Invoke1(_paneSelect, _paneObj, model, IntPtr.Zero);
							else if (_panelSelect != IntPtr.Zero && _panelObj != IntPtr.Zero)
								Invoke1(_panelSelect, _panelObj, model, IntPtr.Zero);
						}
					}
					finally { try { if (flag != null) System.IO.File.Delete(flag); } catch { } }

					if (now - _saidAt < 1f) return;
					_saidAt = now;
					VRChatArchiveModPlugin.Logger.LogInfo("[Preview] " + (string.IsNullOrEmpty(name) ? id : name)
						+ " : ouvert dans le volet 3D de VRChat.");
				}
				catch (Exception e)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Preview] ouverture : " + e.Message);
				}
			}
			finally
			{
				_isOpening = false;
			}
		}

		// ---------------------------------------------------------------- the probe

		// Called every tick from the avatars grid, which guarantees a valid MenuRoot. Cheap: throttled,
		// and it stops doing anything once it has reported.
		internal static void Probe()
		{
			try
			{
				float now; try { now = VaClock.Now; } catch { return; }
				if (now < _nextProbe) return;
				_nextProbe = now + 0.5f;

				if (!EnsurePane()) return;

				// Every reference field of the pane, by offset, with the class of what it points at.
				// The avatar is in here; its class is the data model VRChat itself builds. Reporting
				// the whole set rather than guessing which field means one pass answers it for good.
				var report = new List<string>();
				foreach (IntPtr f in MemberAlign.LiveFields(_paneClassCache))
				{
					string declared = FieldTypeName(f);
					if (declared == null) continue;
					// Value types at a wrong offset are noise; only reference slots can hold the model.
					if (declared == "Single" || declared == "Int32" || declared == "Boolean"
						|| declared == "Byte" || declared == "Void" || declared == "Vector3") continue;

					uint off; try { off = IL2CPP.il2cpp_field_get_offset(f); } catch { continue; }
					if (off == 0) continue;
					IntPtr v; try { v = Marshal.ReadIntPtr(_paneObj, (int)off); } catch { continue; }
					if (v == IntPtr.Zero || !NativeGuard.IsLiveObject(v)) continue;

					IntPtr vk; try { vk = IL2CPP.il2cpp_object_get_class(v); } catch { continue; }
					if (vk == IntPtr.Zero) continue;

					string fname = null; try { fname = MemberAlign.LiveFieldName(f); } catch { }
					int nf = 0; try { nf = MemberAlign.LiveFields(vk).Count; } catch { }

					// THE METHOD COUNT IS WHAT NAMES THE MODEL, AND THIS IS THE PROBE THAT MAY ASK FOR IT.
					//
					// The separate object-graph walk that went looking for a live model killed the process
					// twice, and the breadcrumb file proved it -- written before the walk, still on disk
					// after the game died. Its Implements() leant on il2cpp_class_get_interfaces and
					// il2cpp_class_get_parent, two exports nothing in this codebase validates; the finder
					// deliberately reads a parent through UnityVersionHandler instead, precisely because
					// the exports are scrambled on 1903.
					//
					// So that walk is gone and the question comes back here, to the one reader that has run
					// for hours without a single incident. A class implementing a 58-method interface
					// cannot have fewer than 58 methods, so the count alone picks the model out of a field
					// report -- no interface test, no new API, nothing this probe was not already doing.
					// CACHED PER CLASS, because the uncached version cost the owner the menu.
					//
					// Measured immediately after it shipped: ArchiveFavGrid = 841 ms/s, the game at 3 fps,
					// this module the worst offender in the mod by a factor of ten. LiveMethodShapes does
					// not just count -- it builds a shape string for every method, reading each parameter's
					// type -- and this ran over every field's class twice a second, System.String's 245
					// methods included.
					//
					// A class's method count cannot change while the game runs, so it is answered once.
					// Same defect as the ESP lookup and the nameplate resolve before it: an expensive
					// question asked again on a timer when the answer was already known.
					int nm = 0;
					if (!_methodCount.TryGetValue(vk, out nm))
					{
						try { var t = new List<IntPtr>(); MemberAlign.LiveMethodShapes(vk, t); nm = t.Count; } catch { nm = 0; }
						_methodCount[vk] = nm;
					}

					// AND THE CONFIRMATION, ON THE ONE CANDIDATE THE COUNT PUTS FORWARD.
					//
					// A count above the interface's own method count is a strong hint, not a fact -- plenty
					// of unrelated classes are large. Asking whether the class actually implements the
					// interface settles it, and now that the exports are bound by their real 1903 names it
					// is a question that can be asked at all. It is asked of the pane's own fields only, a
					// dozen classes of live objects, never of a graph walk.
					string mark = "";
					if (_modelClass != IntPtr.Zero && nm >= 58)
						{
							if (!_implements.TryGetValue(vk, out bool impl))
							{
								impl = Implements(vk, _modelClass);
								_implements[vk] = impl;
							}
							if (impl)
							{
								mark = "  <== MODELE";
								// The concrete class, described ONCE. This is the last unknown: which of its
								// members speaks ApiAvatar, the record we already fetch for an archive avatar.
								// One live class, already initialised, so it is the same safe read the probe
								// has been doing all along.
								if (_concreteClass == IntPtr.Zero)
								{
									_concreteClass = vk;
									try { DescribeConcrete(vk); } catch { }
								}
							}
						}
					report.Add(Readable(fname) + " -> " + Readable(ClassName(vk)) + " [" + nf + "f/" + nm + "m]" + mark);
					if (report.Count >= 26) break;
				}

				string line = string.Join(" | ", report);
				if (line == _lastReport) return;      // only when it actually changes
				_lastReport = line;
				VRChatArchiveModPlugin.Logger.LogInfo("[Probe] pane refs : " + line);
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Probe] " + e.Message);
			}
		}

		// ---------------------------------------------------------------- learning the model class

		private static bool _discovered;

		// THE MODEL CLASS, READ OUT OF A SIGNATURE INSTEAD OF OUT OF A PROXY.
		//
		// Everything so far has been stuck on the same wall: to open the pane we need an IAvatar, and
		// naming IAvatar in C# forces the proxy cctor that ends the process. Six attempts died there.
		//
		// But we never needed to NAME it. The panel's own click handler TAKES an IAvatar, and a live
		// method carries its parameter types with it: il2cpp_method_get_param hands back the Il2CppType,
		// il2cpp_class_from_type turns that into the class pointer. It is the very same pointer the proxy
		// would have produced -- reached by reading metadata rather than by running a class initialiser.
		// Pure observation; nothing is constructed, nothing is called.
		//
		// So every method of the panel and of the pane is listed here with the class of each slot. The
		// model announces itself: a parameter class with ZERO fields (an interface has none) on a handler
		// that returns void and takes exactly one argument. Once that pointer is known, every later step
		// can compare against it -- factory, handler, instance -- without a generated type ever being
		// named again.
		internal static void Discover()
		{
			if (_discovered) return;
			if (!EnsurePane()) return;
			_discovered = true;
			try
			{
				var onPanel = DumpSlots("panneau", _panelClass);
				var onPane  = DumpSlots("volet", _paneClassCache);

				// THE MODEL IS THE ONE BOTH SIDES SPEAK.
				//
				// The list panel hands an avatar to the pane, so the type they exchange appears as a
				// parameter on BOTH classes. Zero fields means an interface, and of the interfaces they
				// share, the avatar is by far the widest -- 58 methods against 7 for its neighbour. That
				// intersection is what picks it out; no name is involved, so nothing here can be fooled by
				// a rename.
				IntPtr best = IntPtr.Zero; int bestM = 0;
				foreach (var c in onPanel)
				{
					if (c.Fields != 0) continue;
					if (!onPane.Exists(o => o.Klass == c.Klass)) continue;
					if (c.Methods > bestM) { bestM = c.Methods; best = c.Klass; }
				}
				if (best == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[Preview] aucune interface commune au panneau et au volet — modele non identifie.");
					return;
				}
				_modelClass = best;
				// The handler VRChat itself runs when a card is clicked: the panel method whose single
				// argument IS the model. Recorded now, by pointer, while we are already holding both.
				foreach (var c in onPanel) if (c.Klass == best) { _panelSelect = c.Method; break; }
				foreach (var c in onPane) if (c.Klass == best) { _paneSelect = c.Method; break; }
				VRChatArchiveModPlugin.Logger.LogInfo("[Preview] MODELE identifie : interface partagee panneau/volet, "
					+ bestM + " methodes, classe " + Readable(ClassName(best)) + ". Recherche d'une instance vivante...");
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Preview] Discover : " + e.Message); }
		}

		private struct Slot { public IntPtr Klass; public int Fields; public int Methods; public IntPtr Method; }

		// One line per method that could plausibly be "show this avatar": void return, exactly one
		// reference parameter. The parameter's class name, field count and method count are printed
		// because that trio is what tells an interface from a component from a plain data holder.
		private static List<Slot> DumpSlots(string what, IntPtr klass)
		{
			var slots = new List<Slot>();
			if (klass == IntPtr.Zero) return slots;

			var methods = new List<IntPtr>();
			try { MemberAlign.LiveMethodShapes(klass, methods); } catch { return slots; }

			var lines = new List<string>();
			foreach (IntPtr m in methods)
			{
				int n;
				try { n = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m).ParametersCount; } catch { continue; }
				if (n != 1) continue;

				// void return only: a getter that happens to take one argument is not a command.
				IntPtr rt; try { rt = IL2CPP.il2cpp_method_get_return_type(m); } catch { continue; }
				if (rt == IntPtr.Zero) continue;
				int rk; try { rk = (int)UnityVersionHandler.Wrap((Il2CppTypeStruct*)rt).Type; } catch { continue; }
				if (rk != 0x01) continue;   // IL2CPP_TYPE_VOID

				IntPtr pt; try { pt = IL2CPP.il2cpp_method_get_param(m, 0); } catch { continue; }
				if (pt == IntPtr.Zero) continue;
				int pk; try { pk = (int)UnityVersionHandler.Wrap((Il2CppTypeStruct*)pt).Type; } catch { continue; }
				if (pk != 0x12 && pk != 0x1c) continue;   // CLASS (covers interfaces) or OBJECT

				IntPtr pc; try { pc = IL2CPP.il2cpp_class_from_type(pt); } catch { continue; }
				if (pc == IntPtr.Zero || !NativeGuard.IsReadable(pc, 16)) continue;

				int nf = 0, nm = 0;
				try { nf = MemberAlign.LiveFields(pc).Count; } catch { }
				try { var tmp = new List<IntPtr>(); MemberAlign.LiveMethodShapes(pc, tmp); nm = tmp.Count; } catch { }

				string mn = null;
				try { IntPtr p = IL2CPP.il2cpp_method_get_name(m); if (p != IntPtr.Zero) mn = Marshal.PtrToStringAnsi(p); } catch { }

				slots.Add(new Slot { Klass = pc, Fields = nf, Methods = nm, Method = m });
				lines.Add(Readable(mn) + "(" + Readable(ClassName(pc)) + " " + nf + "f/" + nm + "m)");
				if (lines.Count >= 24) break;
			}

			VRChatArchiveModPlugin.Logger.LogInfo("[Preview] " + what + " : " + lines.Count
				+ " methode(s) void(1 ref) — " + (lines.Count == 0 ? "aucune" : string.Join(" | ", lines)));
			return slots;
		}

		// ---------------------------------------------------------------- finding a live model

		private static IntPtr _modelClass, _concreteClass, _panelSelect, _paneSelect, _modelCtor, _bindApi;
		private static float _nextHunt;
		private static bool _huntSaid;

		// WHY THE ASSEMBLY-WIDE SCAN HAD TO GO.
		//
		// The previous step walked all 4839 classes of Assembly-CSharp reading each one's method table, to
		// find whatever RETURNS the model. It crashed the game: EXCEPTION_ACCESS_VIOLATION reading address
		// 0x0 on the main thread. Most of those classes have never been touched, their method tables are
		// not built, and asking a cold class for its members dereferences a null. Careful about freezing,
		// careless about this -- the sweep was bounded in TIME and unbounded in what it touched.
		//
		// THE RULE THAT REPLACES IT: only ever look at classes belonging to objects that ALREADY EXIST.
		//
		// A live object's class is initialised by definition -- the object could not exist otherwise -- so
		// reading its members is the same safe operation Probe has been doing for hours. And VRChat is
		// already holding what we need: the list panel is displaying avatars right now, so a model
		// instance is sitting in one of its fields. Walking from the panel finds a REAL one instead of
		// hunting for a factory among classes nobody has ever instantiated.
		// AN ACCESS VIOLATION CANNOT BE CAUGHT, SO THE PROBE HAS TO REMEMBER KILLING THE GAME.
		//
		// Twice today a walk of mine ended the process outright, and both times the owner simply
		// relaunched into the same walk. No try/catch can prevent that, because the process is gone
		// before any handler runs -- but a file written before the risky part and deleted after it
		// survives exactly the case that matters. Found on startup, it means the last attempt did not
		// come back, and the probe stays down until somebody deliberately re-arms it.
		//
		// The feature is worth some risk. Making a player pay the same crash twice is not.
		private static string FlagPath()
		{
			try { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "va_preview_hunt.flag"); }
			catch { return null; }
		}

		private static bool _huntDisabled, _huntChecked;

		internal static void HuntModel()
		{
			if (_huntDisabled || _modelClass == IntPtr.Zero || _concreteClass != IntPtr.Zero) return;

			float now; try { now = VaClock.Now; } catch { return; }
			if (now < _nextHunt) return;
			_nextHunt = now + 1f;
			if (!EnsurePane()) return;

			string flag = FlagPath();
			if (!_huntChecked)
			{
				_huntChecked = true;
				try
				{
					if (flag != null && System.IO.File.Exists(flag))
					{
						_huntDisabled = true;
						System.IO.File.Delete(flag);
						VRChatArchiveModPlugin.Logger.LogWarning("[Preview] la recherche du modele n'est pas revenue de la session precedente "
							+ "(le jeu s'est arrete pendant) — elle reste DESACTIVEE. Tout le reste du mod fonctionne.");
						return;
					}
				}
				catch { }
			}
			try { if (flag != null) System.IO.File.WriteAllText(flag, "1"); } catch { }

			try
			{
				var seen = new HashSet<IntPtr>();
				var queue = new List<KeyValuePair<IntPtr, int>>
				{
					new KeyValuePair<IntPtr, int>(_panelObj, 0),
					new KeyValuePair<IntPtr, int>(_paneObj, 0),
				};

				int examined = 0;
				for (int qi = 0; qi < queue.Count && examined < 600; qi++)
				{
					IntPtr obj = queue[qi].Key;
					int depth = queue[qi].Value;
					if (obj == IntPtr.Zero || !seen.Add(obj)) continue;
					examined++;

					IntPtr k; try { k = IL2CPP.il2cpp_object_get_class(obj); } catch { continue; }
					if (k == IntPtr.Zero) continue;

					if (Implements(k, _modelClass))
					{
						_concreteClass = k;
						int nf = 0, nm = 0;
						try { nf = MemberAlign.LiveFields(k).Count; } catch { }
						try { var t = new List<IntPtr>(); MemberAlign.LiveMethodShapes(k, t); nm = t.Count; } catch { }
						VRChatArchiveModPlugin.Logger.LogInfo("[Preview] MODELE VIVANT trouve : classe "
							+ Readable(ClassName(k)) + ", " + nf + " champs / " + nm + " methodes, profondeur " + depth
							+ ". C'est le type concret que VRChat construit pour chaque avatar affiche.");
						DescribeConcrete(k);
						return;
					}

					if (depth >= 3) continue;

					// An array: its elements are the avatars a list is holding. x64 il2cpp layout --
					// length at 0x18, first element at 0x20.
					string kn = ClassName(k);
					if (kn != null && kn.EndsWith("[]"))
					{
						if (!NativeGuard.IsReadable(obj, 0x20)) continue;
						int len = 0;
						try { len = (int)Marshal.ReadInt64(obj, 0x18); } catch { continue; }
						if (len < 0 || len > 4096) continue;
						if (len > 64) len = 64;
						if (len > 0 && !NativeGuard.IsReadable(obj, 0x20 + len * IntPtr.Size)) continue;
						for (int i = 0; i < len && i < 64; i++)
						{
							IntPtr el; try { el = Marshal.ReadIntPtr(obj, 0x20 + i * IntPtr.Size); } catch { break; }
							if (el != IntPtr.Zero && NativeGuard.IsLiveObject(el))
								queue.Add(new KeyValuePair<IntPtr, int>(el, depth + 1));
						}
						continue;
					}

					// A FIELD OFFSET IS NOT A PROMISE THAT THE BYTES ARE THERE.
					//
					// This is what crashed the game the second time. "Only look at live objects" was the
					// right rule and it was not enough: I then read every field offset of the object's
					// class straight off the instance pointer. A STATIC field's offset is an index into
					// the class's static storage, not into the instance -- apply it to an object and the
					// read lands past the end, on whatever follows, and eventually on an unmapped page.
					// Marshal.ReadIntPtr does not throw there; the process dies.
					//
					// So the extent is established first, from the class's own instance size, and proven
					// readable once. After that every field inside it is safe by construction, and any
					// offset outside it -- which is exactly what a static field looks like -- is skipped
					// instead of dereferenced.
					int size;
					try { size = (int)UnityVersionHandler.Wrap((Il2CppClass*)k).InstanceSize; } catch { continue; }
					if (size < 0x18 || size > 0x20000) continue;
					if (!NativeGuard.IsReadable(obj, size)) continue;

					foreach (IntPtr f in MemberAlign.LiveFields(k))
					{
						uint off; try { off = IL2CPP.il2cpp_field_get_offset(f); } catch { continue; }
						if (off < 0x10 || off + IntPtr.Size > size) continue;
						IntPtr v; try { v = Marshal.ReadIntPtr(obj, (int)off); } catch { continue; }
						if (v == IntPtr.Zero || v == obj || !NativeGuard.IsLiveObject(v)) continue;
						queue.Add(new KeyValuePair<IntPtr, int>(v, depth + 1));
					}
				}

				if (!_huntSaid)
				{
					_huntSaid = true;
					VRChatArchiveModPlugin.Logger.LogInfo("[Preview] " + examined
						+ " objets vivants examines depuis le panneau, aucun ne porte le modele pour l'instant. "
						+ "Ouvre la liste des avatars et clique-en un : la recherche se refait chaque seconde.");
				}
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Preview] HuntModel : " + e.Message); }
			finally { try { if (flag != null) System.IO.File.Delete(flag); } catch { } }
		}

		// Does this class implement that interface? Safe because the class belongs to an object that
		// exists: its interface table is built. Walking the parent chain covers a model whose base class
		// is the one declaring it.
		private static bool Implements(IntPtr klass, IntPtr iface)
		{
			if (!Il2CppRaw.Ready) return false;   // a missing export is a refusal, never a jump to 0
			try
			{
				for (IntPtr k = klass; k != IntPtr.Zero; )
				{
					IntPtr iter = IntPtr.Zero, it;
					int guard = 0;
					while ((it = Il2CppRaw.ClassGetInterfaces(k, ref iter)) != IntPtr.Zero && guard++ < 256)
						if (it == iface) return true;
					IntPtr p; try { p = Il2CppRaw.ClassGetParent(k); } catch { break; }
					if (p == k) break;
					k = p;
				}
			}
			catch { }
			return false;
		}

		// Everything about the concrete model that decides how we build one: what it holds, and which of
		// its members speak ApiAvatar -- the record we already fetch for an archive avatar. ONE class,
		// and a live one, so this cannot repeat the crash.
		private static void DescribeConcrete(IntPtr k)
		{
			try
			{
				IntPtr apiAvatar = IntPtr.Zero;
				try { apiAvatar = Il2CppClassPointerStore<ApiAvatar>.NativeClassPtr; } catch { }

				var fields = new List<string>();
				foreach (IntPtr f in MemberAlign.LiveFields(k))
				{
					string fn = null; try { fn = MemberAlign.LiveFieldName(f); } catch { }
					string tn = FieldTypeName(f);
					IntPtr fc = IntPtr.Zero;
					try { IntPtr tp = MemberAlign.FieldTypePtr(f); if (tp != IntPtr.Zero) fc = IL2CPP.il2cpp_class_from_type(tp); } catch { }
					string mark = (apiAvatar != IntPtr.Zero && fc == apiAvatar) ? "  <== ApiAvatar" : "";
					fields.Add(Readable(fn) + ":" + Readable(tn) + mark);
					if (fields.Count >= 24) break;
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[Preview] champs du modele : " + string.Join(" | ", fields));

				var takers = new List<string>();
				var methods = new List<IntPtr>();
				try { MemberAlign.LiveMethodShapes(k, methods); } catch { }
				foreach (IntPtr m in methods)
				{
					int n; try { n = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m).ParametersCount; } catch { continue; }
					bool touches = false;
					var args = new List<string>();
					for (int i = 0; i < n && i < 8; i++)
					{
						IntPtr pt; try { pt = IL2CPP.il2cpp_method_get_param(m, (uint)i); } catch { break; }
						IntPtr pc = IntPtr.Zero;
						try { if (pt != IntPtr.Zero) pc = IL2CPP.il2cpp_class_from_type(pt); } catch { }
						if (apiAvatar != IntPtr.Zero && pc == apiAvatar) touches = true;
						args.Add(Readable(pc == IntPtr.Zero ? null : ClassName(pc)));
					}
					if (!touches) continue;
					string mn = null;
					try { IntPtr p = IL2CPP.il2cpp_method_get_name(m); if (p != IntPtr.Zero) mn = Marshal.PtrToStringAnsi(p); } catch { }
					// RETURN TYPE AND STATICNESS, because that is what separates a factory from a mutator.
					//
					// Three members take an ApiAvatar. One that RETURNS the model builds a new one -- which is
					// what an archive avatar needs, since ours is in no VRChat list. One returning void mutates
					// the instance it is called on, and rebinding the pane's own live model is a far more
					// invasive thing to do to the game. Printing both tells them apart without calling either.
					string rt = null;
					bool isStatic = false;
					try
					{
						IntPtr r = IL2CPP.il2cpp_method_get_return_type(m);
						if (r != IntPtr.Zero)
						{
							int rk = (int)UnityVersionHandler.Wrap((Il2CppTypeStruct*)r).Type;
							if (rk == 0x01) rt = "void";
							else { IntPtr rc = IL2CPP.il2cpp_class_from_type(r); rt = rc == _concreteClass ? "LE MODELE" : Readable(rc == IntPtr.Zero ? null : ClassName(rc)); }
						}
						isStatic = ((ushort)UnityVersionHandler.Wrap((Il2CppMethodInfo*)m).Flags & 0x0010) != 0;
					}
					catch { }
					// The binder: an INSTANCE method, void, one ApiAvatar. The two static ones return a
					// PerformanceRating -- they score an avatar, they do not describe one.
					if (!isStatic && rt == "void" && n == 1) _bindApi = m;
					takers.Add((isStatic ? "static " : "") + (rt ?? "?") + " " + Readable(mn) + "(" + string.Join(", ", args) + ")");
					if (takers.Count >= 12) break;
				}
				var ctors = new List<string>();
				foreach (IntPtr m in methods)
				{
					string cn = null;
					try { IntPtr q = IL2CPP.il2cpp_method_get_name(m); if (q != IntPtr.Zero) cn = Marshal.PtrToStringAnsi(q); } catch { }
					if (cn != ".ctor") continue;
					try { if (UnityVersionHandler.Wrap((Il2CppMethodInfo*)m).ParametersCount == 0) _modelCtor = m; } catch { }
					int n2; try { n2 = UnityVersionHandler.Wrap((Il2CppMethodInfo*)m).ParametersCount; } catch { continue; }
					var a2 = new List<string>();
					for (int i = 0; i < n2 && i < 8; i++)
					{
						IntPtr pt; try { pt = IL2CPP.il2cpp_method_get_param(m, (uint)i); } catch { break; }
						IntPtr pc = IntPtr.Zero;
						try { if (pt != IntPtr.Zero) pc = IL2CPP.il2cpp_class_from_type(pt); } catch { }
						a2.Add(Readable(pc == IntPtr.Zero ? null : ClassName(pc)));
					}
					ctors.Add(".ctor(" + string.Join(", ", a2) + ")");
					if (ctors.Count >= 8) break;
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[Preview] constructeurs du modele : "
					+ (ctors.Count == 0 ? "aucun" : string.Join(" | ", ctors)));

				VRChatArchiveModPlugin.Logger.LogInfo("[Preview] membres du modele qui prennent un ApiAvatar : "
					+ (takers.Count == 0 ? "aucun" : string.Join(" | ", takers)));
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[Preview] DescribeConcrete : " + e.Message); }
		}

		// ---------------------------------------------------------------- locating the pane

		private static IntPtr _paneClassCache;

		private static bool EnsurePane()
		{
			if (_paneObj != IntPtr.Zero && _paneClassCache != IntPtr.Zero) return true;

			float now; try { now = VaClock.Now; } catch { now = 0f; }
			if (now < _nextTry) return false;
			_nextTry = now + 2f;

			try
			{
				var host = FindHost();
				if (host == null) return false;

				if (_panelObj == IntPtr.Zero)
				{
					foreach (var c in host.GetComponents<Component>())
					{
						if (c == null) continue;
						IntPtr ptr; try { ptr = c.Pointer; } catch { continue; }
						if (ptr == IntPtr.Zero || !NativeGuard.IsLiveObject(ptr)) continue;
						IntPtr k; try { k = IL2CPP.il2cpp_object_get_class(ptr); } catch { continue; }
						if (k == IntPtr.Zero) continue;
						if (!HasFields(k, "_selectedAvatarPanel", "_avatarListView")) continue;
						_panelObj = ptr; _panelClass = k; break;
					}
					if (_panelObj == IntPtr.Zero) return false;
				}

				_paneObj = RefField(_panelObj, _panelClass, "_selectedAvatarPanel");
				if (_paneObj == IntPtr.Zero) return false;
				try { _paneClassCache = IL2CPP.il2cpp_object_get_class(_paneObj); } catch { return false; }
				if (_paneClassCache == IntPtr.Zero) return false;

				if (!_saidPane)
				{
					_saidPane = true;
					int nf = 0; try { nf = MemberAlign.LiveFields(_paneClassCache).Count; } catch { }
					VRChatArchiveModPlugin.Logger.LogInfo("[Probe] panneau de preview localise par ses champs serialises : classe "
						+ Readable(ClassName(_paneClassCache)) + ", " + nf + " champ(s). Aucun proxy touche.");
				}
				return true;
			}
			catch { return false; }
		}

		private static bool _openChecked, _openDisabled;

		private static string OpenFlagPath()
		{
			try { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "va_preview_open.flag"); }
			catch { return null; }
		}

		// One il2cpp call, by pointer. `ctorOf` non-zero means "allocate an instance of this class first
		// and run the method on it", which is how a constructor is called without naming its type.
		private static IntPtr Invoke1(IntPtr method, IntPtr target, IntPtr arg, IntPtr ctorOf)
		{
			try
			{
				IntPtr obj = target;
				if (ctorOf != IntPtr.Zero)
				{
					obj = IL2CPP.il2cpp_object_new(ctorOf);
					if (obj == IntPtr.Zero || !NativeGuard.IsLiveObject(obj)) return IntPtr.Zero;
				}

				IntPtr exc = IntPtr.Zero;
				void** argv = stackalloc void*[1];
				argv[0] = (void*)arg;
				IntPtr ret = IL2CPP.il2cpp_runtime_invoke(method, obj, arg == IntPtr.Zero ? null : argv, ref exc);
				if (exc != IntPtr.Zero)
				{
					string excName = "?";
					try { IntPtr ek = IL2CPP.il2cpp_object_get_class(exc); if (ek != IntPtr.Zero) excName = ClassName(ek); } catch { }
					string mn = "?";
					try { IntPtr p = IL2CPP.il2cpp_method_get_name(method); if (p != IntPtr.Zero) mn = Marshal.PtrToStringAnsi(p); } catch { }
					VRChatArchiveModPlugin.Logger.LogWarning("[Preview] invoke " + Readable(mn) + " sur " + Readable(ClassName(IL2CPP.il2cpp_object_get_class(obj))) + " a leve: " + excName);
					return IntPtr.Zero;
				}
				return ctorOf != IntPtr.Zero ? obj : ret;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[Preview] Invoke1 : " + e.Message);
				return IntPtr.Zero;
			}
		}

		// ---------------------------------------------------------------- primitives (pointers only)

		private static string ClassName(IntPtr klass)
		{
			try
			{
				IntPtr p = IL2CPP.il2cpp_class_get_name(klass);
				return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
			}
			catch { return null; }
		}

		// An obfuscated name is a run of unreadable glyphs and tells us nothing; say so rather than
		// printing mojibake that makes a log unreadable.
		private static string Readable(string s)
		{
			if (string.IsNullOrEmpty(s)) return "?";
			foreach (char ch in s) if (ch > 126) return "<obf>";
			return s;
		}

		private static string FieldTypeName(IntPtr field)
		{
			try
			{
				IntPtr tp = MemberAlign.FieldTypePtr(field);
				if (tp == IntPtr.Zero) return null;
				IntPtr fk = IL2CPP.il2cpp_class_from_type(tp);
				return fk == IntPtr.Zero ? null : ClassName(fk);
			}
			catch { return null; }
		}

		private static bool HasFields(IntPtr klass, params string[] want)
		{
			try
			{
				int hit = 0;
				foreach (IntPtr f in MemberAlign.LiveFields(klass))
				{
					string n; try { n = MemberAlign.LiveFieldName(f); } catch { continue; }
					if (string.IsNullOrEmpty(n)) continue;
					for (int i = 0; i < want.Length; i++) if (n == want[i]) { hit++; break; }
				}
				return hit >= want.Length;
			}
			catch { return false; }
		}

		// A reference field read at its offset. Nothing is cast and no method is called, so a wrong
		// answer is a null rather than a jump into a stranger's code -- and the result is proven live,
		// because a plausible-looking garbage reference is what the GC walks into later, far from here.
		private static IntPtr RefField(IntPtr obj, IntPtr klass, string name)
		{
			try
			{
				foreach (IntPtr f in MemberAlign.LiveFields(klass))
				{
					string n; try { n = MemberAlign.LiveFieldName(f); } catch { continue; }
					if (n != name) continue;
					uint off; try { off = IL2CPP.il2cpp_field_get_offset(f); } catch { return IntPtr.Zero; }
					if (off == 0) return IntPtr.Zero;
					IntPtr v; try { v = Marshal.ReadIntPtr(obj, (int)off); } catch { return IntPtr.Zero; }
					return (v != IntPtr.Zero && NativeGuard.IsLiveObject(v)) ? v : IntPtr.Zero;
				}
			}
			catch { }
			return IntPtr.Zero;
		}

		// Scoped to the menu we are standing in. Resources.FindObjectsOfTypeAll costs hundreds of
		// milliseconds a second, so it is the last resort and rate-limited.
		private static float _nextSweep;
		private static Transform FindHost()
		{
			try
			{
				Transform root = MenuRoot;
				if (root != null)
				{
					for (Transform up = root; up != null; up = up.parent)
					{
						string un; try { un = up.name; } catch { break; }
						if (un == PanelHost) { try { if (up.gameObject.activeInHierarchy) return up; } catch { } }
						root = up;
					}
					foreach (var t in root.GetComponentsInChildren<Transform>(true))
					{
						if (t == null) continue;
						string n; try { n = t.name; } catch { continue; }
						if (n != PanelHost) continue;
						try { if (!t.gameObject.activeInHierarchy) continue; } catch { continue; }
						return t;
					}
				}

				float now; try { now = VaClock.Now; } catch { now = 0f; }
				if (now < _nextSweep) return null;
				_nextSweep = now + 3f;
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null) continue;
					string n; try { n = t.name; } catch { continue; }
					if (n != PanelHost) continue;
					try { if (!t.gameObject.scene.IsValid()) continue; } catch { continue; }
					try { if (!t.gameObject.activeInHierarchy) continue; } catch { continue; }
					return t;
				}
			}
			catch { }
			return null;
		}
	}
}
