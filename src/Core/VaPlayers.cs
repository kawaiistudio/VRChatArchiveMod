using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VRChatArchiveMod.Core
{
	// THE PLAYER ROSTER, READ WHOLLY BY RAW METADATA — the interop wrappers for it crash on this build.
	//
	// Every player lookup wants VRC.SDKBase.VRCPlayerApi.AllPlayers. On this VRChat build NONE of the
	// interop paths to it survive: the generated AllPlayers getter, get_AllPlayers resolved fresh by
	// name and invoked (its own body reads a moved backing offset), and -- once you have the list --
	// the generated List<T>._size / _items accessors all take the process down with an uncatchable
	// native access violation (an AccessViolationException is a corrupted-state exception .NET 6 never
	// hands to a catch, so the game just dies).
	//
	// So the roster is read with ZERO interop method calls. The backing field (VRCPlayerApi.sPlayers,
	// a static List<VRCPlayerApi>) is read with il2cpp_field_static_get_value; then the list's _size
	// and _items are read by raw offset (il2cpp_field_get_offset, repaired by FieldOffsetFix) and the
	// backing array is walked -- every pointer proven live by NativeGuard before it is touched. The
	// result is a plain managed List<VRCPlayerApi> snapshot: a drop-in for every caller's .Count / [i],
	// and nothing it does can end the process. This is the same door the native engine uses, from
	// managed code. The per-player members (isLocal, displayName, ...) are VRCPlayerApi's own, which
	// TokenShiftFix anchored (98/99 methods) and MemberAlign realigned, so they are used normally.
	internal static unsafe class VaPlayers
	{
		// The roster read is safe (raw, no interop method calls). Set to false only to force the
		// degraded "no roster" fallback if a future build breaks even the raw field read.
		private const bool EnableRoster = true;

		private static int _state;           // 0 = not probed, 1 = ready, 2 = unavailable
		private static IntPtr _klass;
		private static IntPtr _field;        // the roster static field (sPlayers), read directly, never the getter
		private static bool _logged;

		internal static bool Ready
		{
			get
			{
				if (!EnableRoster)
				{
					if (!_logged)
					{
						_logged = true;
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[VaPlayers] liste des joueurs desactivee sur ce build (accesseurs VRCPlayerApi mal lies — "
							+ "lecture et membres par-joueur crashent) : fonctions joueur degradees, sans crash. "
							+ "Regenerer l'interop/metadata pour ce build, puis EnableRoster=true.");
					}
					return false;
				}
				if (_state == 0) Probe();
				return _state == 1;
			}
		}

		private static void Probe()
		{
			try
			{
				IntPtr k = IntPtr.Zero;
				try { k = Il2CppClassPointerStore<VRC.SDKBase.VRCPlayerApi>.NativeClassPtr; } catch { }
				if (k == IntPtr.Zero || MissingTypeGuard.IsPlaceholder(k)) { Fail("classe VRCPlayerApi introuvable sur ce build"); return; }
				_klass = k;

				// READ THE STATIC FIELD, NOT THE GETTER. Invoking get_AllPlayers still crashes even when
				// resolved by name -- the getter's own body reads its backing field at an offset this
				// build moved. A direct static-field read (il2cpp_field_static_get_value, repaired by
				// FieldOffsetFix) executes no method body: it is just an offset read, validated by
				// NativeGuard. Field is found by name, so the mis-bound interop accessor is never used.
				// The SDK spells it as a plain static field; the backing-field name is a safety net for
				// a build that makes it an auto-property.
				// The roster backing field: on this build the SDK spells it `sPlayers` (a private static
				// List<VRCPlayerApi>, which the AllPlayers property returns). Try the known names first,
				// then fall back to enumerating for the one List-typed field on the class.
				string[] names = { "sPlayers", "AllPlayers", "<AllPlayers>k__BackingField" };
				IntPtr f = IntPtr.Zero;
				foreach (string nm in names)
				{
					try { f = IL2CPP.il2cpp_class_get_field_from_name(k, nm); } catch { f = IntPtr.Zero; }
					if (f != IntPtr.Zero) { _field = f; _state = 1; Ok("champ statique " + nm); return; }
				}

				try
				{
					foreach (IntPtr fld in MemberAlign.LiveFields(k))
					{
						string tn = null;
						try
						{
							IntPtr tp = MemberAlign.FieldTypePtr(fld);
							if (tp != IntPtr.Zero)
							{
								IntPtr fc = IL2CPP.il2cpp_class_from_type(tp);
								if (fc != IntPtr.Zero) { IntPtr np = IL2CPP.il2cpp_class_get_name(fc); if (np != IntPtr.Zero) tn = Marshal.PtrToStringAnsi(np); }
							}
						}
						catch { }
						if (tn != null && tn.StartsWith("List", StringComparison.Ordinal)) { _field = fld; break; }
					}
				}
				catch { }
				if (_field != IntPtr.Zero) { _state = 1; Ok("champ statique de type List (par enumeration)"); return; }

				Fail("champ roster non resolu");
			}
			catch (Exception e) { Fail(e.Message); }
		}

		private static void Ok(string how)
		{
			if (_logged) return;
			_logged = true;
			VRChatArchiveModPlugin.Logger.LogInfo("[VaPlayers] liste des joueurs lue par metadonnees il2cpp (" + how + ") — fonctions joueur actives.");
		}

		private static void Fail(string why)
		{
			if (_logged) return;
			_logged = true;
			VRChatArchiveModPlugin.Logger.LogWarning("[VaPlayers] liste des joueurs indisponible (" + why + ") — fonctions joueur degradees, sans crash.");
		}

		// The raw il2cpp List<VRCPlayerApi> object pointer, or Zero. Invokes the name-resolved getter
		// (correct pointer), or reads the static field, guarding both against a dead return.
		private static IntPtr RawList()
		{
			try
			{
				if (_field != IntPtr.Zero)
				{
					IntPtr outp = IntPtr.Zero;
					IL2CPP.il2cpp_field_static_get_value(_field, &outp);
					return outp;
				}
			}
			catch { }
			return IntPtr.Zero;
		}

		// A managed snapshot of the roster, or null when it cannot be read. Never the il2cpp wrapper
		// type, so callers' .Count / [i] run on a plain List and cannot trip the mis-bound members.
		internal static List<VRC.SDKBase.VRCPlayerApi> All()
		{
			if (!Ready) return null;
			IntPtr ptr = RawList();
			if (ptr == IntPtr.Zero || !NativeGuard.IsLiveObject(ptr)) return null;
			return RawItems(ptr);
		}

		// Read List<VRCPlayerApi> WITHOUT the interop wrapper's _size/_items accessors, which are
		// mis-bound on this build (reading list._items / list._size through the generated List<T>
		// property is the access violation — Il2CppSeq.Items dies there). Instead the two fields are
		// read by raw offset (il2cpp_field_get_offset is repaired by FieldOffsetFix), the same way
		// Il2CppSeq already reads an ImmutableArray's backing field. Every pointer is proven live
		// before it is touched, so corruption yields an empty roster, never a crash.
		private static IntPtr _fSizeOff = IntPtr.Zero, _fItemsOff = IntPtr.Zero;
		private static bool _listOffsetsDone;

		private static List<VRC.SDKBase.VRCPlayerApi> RawItems(IntPtr listPtr)
		{
			var outp = new List<VRC.SDKBase.VRCPlayerApi>();
			try
			{
				IntPtr klass = IL2CPP.il2cpp_object_get_class(listPtr);
				if (klass == IntPtr.Zero) return outp;

				if (!_listOffsetsDone)
				{
					_listOffsetsDone = true;
					IntPtr fSize = IntPtr.Zero, fItems = IntPtr.Zero;
					try { fSize = IL2CPP.il2cpp_class_get_field_from_name(klass, "_size"); } catch { }
					try { fItems = IL2CPP.il2cpp_class_get_field_from_name(klass, "_items"); } catch { }
					if (fSize != IntPtr.Zero) _fSizeOff = (IntPtr)IL2CPP.il2cpp_field_get_offset(fSize);
					if (fItems != IntPtr.Zero) _fItemsOff = (IntPtr)IL2CPP.il2cpp_field_get_offset(fItems);
				}
				int offSize = (int)_fSizeOff, offItems = (int)_fItemsOff;
				if (offSize < 0x10 || offSize > 0x200 || offItems < 0x10 || offItems > 0x200) return outp;

				int n = Marshal.ReadInt32(listPtr, offSize);
				if (n <= 0 || n > 16384) return outp;

				IntPtr arrPtr = Marshal.ReadIntPtr(listPtr, offItems);
				if (arrPtr == IntPtr.Zero || !NativeGuard.IsLiveObject(arrPtr)) return outp;

				var arr = new Il2CppReferenceArray<VRC.SDKBase.VRCPlayerApi>(arrPtr);
				int cap;
				try { cap = arr.Length; } catch { return outp; }
				if (cap < 0 || cap > 16384) return outp;
				if (n > cap) n = cap;

				for (int i = 0; i < n; i++)
				{
					VRC.SDKBase.VRCPlayerApi item;
					try { item = arr[i]; } catch { continue; }
					if (item == null) continue;
					IntPtr ip;
					try { ip = item.Pointer; } catch { continue; }
					if (ip == IntPtr.Zero || !NativeGuard.IsLiveObject(ip)) continue;
					outp.Add(item);
				}
			}
			catch { }
			return outp;
		}
	}
}
