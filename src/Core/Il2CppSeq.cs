using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace VRChatArchiveMod.Core
{
	// READ A SEQUENCE OF STRINGS OUT OF WHATEVER SHAPE IL2CPP HANDED US.
	//
	// Udon's public-variable table hands back its symbol names as an IReadOnlyCollection<string>
	// PROXY — an Il2CppInterop object over a native collection. Such a proxy is NOT a managed
	// IEnumerable (the `is System.Collections.IEnumerable` test fails), and its managed type is the
	// interface, not the concrete class, so reflecting Length/Item on it finds nothing either. Both
	// readers the mod used to have came back empty, which is why the variable table never listed a
	// single symbol and the video player's VRCUrl field was never found.
	//
	// The one thing every il2cpp collection honours is its own IEnumerable: TryCast the proxy to
	// Il2CppSystem.Collections.IEnumerable (a cast on the NATIVE class, not the managed wrapper) and
	// walk its enumerator. Each element is checked alive before its pointer is read — a dead proxy
	// read is an access violation no try/catch survives.
	//
	// Order of attempts: a plain managed enumerable (unit tests, older builds that marshal to
	// managed), then the il2cpp enumeration, then the reflected Length/Item fallback for
	// ImmutableArray-style proxies that expose an indexer but no enumerator.
	internal static class Il2CppSeq
	{
		private const int Cap = 400;   // a table this long is a generated one, not a read

		/// <summary>Which reader answered last ("string[]", "List<string>", "enumerator", "indexer", "managed"). For probes.</summary>
		internal static string LastPath = "none";

		internal static List<string> Strings(object seq)
		{
			var outp = new List<string>();
			if (seq == null) return outp;

			// 1) Already managed: nothing to marshal.
			try
			{
				if (seq is System.Collections.IEnumerable en)
				{
					LastPath = "managed";
					foreach (object o in en)
					{
						string s = o as string ?? o?.ToString();
						if (!string.IsNullOrEmpty(s)) outp.Add(s);
						if (outp.Count >= Cap) break;
					}
					if (outp.Count > 0) return outp;
				}
			}
			catch { }

			// 2) The concrete il2cpp shapes that can be INDEXED. A string[] or a List<string> is walked
		//    by index through the interop's own array/list wrappers, which touch nothing but the
		//    element pointers. This comes BEFORE the generic enumerator on purpose: an enumerator
		//    obtained through IEnumerable is a boxed struct driven by interface dispatch, and on a
		//    build whose runtime structs are reshuffled that is the most fragile call in the mod.
		try
		{
			var arr = (seq as Il2CppObjectBase)?.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray>();
			if (arr != null)
			{
				LastPath = "string[]";
				for (int i = 0; i < arr.Length && i < Cap; i++) { string s = arr[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				return outp;
			}
			var list = (seq as Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.List<string>>();
			if (list != null)
			{
				LastPath = "List<string>";
				int n = list.Count;
				for (int i = 0; i < n && i < Cap; i++) { string s = list[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				return outp;
			}
		}
		catch { }

		// 2b) ImmutableArray<string>, read WITHOUT calling it. A script with no exported symbols hands
		//     back a DEFAULT ImmutableArray whose backing `array` is null, and its native get_Length /
		//     ICollection.Count dereference that null: an access violation that killed the game the
		//     moment such a script was selected in the Udon Manager (reported 2026-09-19). The backing
		//     field is read at its offset instead; null simply means "no symbols".
		try
		{
			if (TryImmutableArray(seq, outp, out bool handled) && handled) { LastPath = "ImmutableArray.array"; return outp; }
		}
		catch { }

		// 3) ANY ICollection<string> -- Dictionary.KeyCollection included -- is COPIED into an array
		//    with its own CopyTo and then indexed. This replaces the enumerator walk that was here:
		//    on 2026-09-02 the game died on the first MoveNext() of the boxed struct enumerator that
		//    IEnumerable.GetEnumerator() hands back for a KeyCollection (the last log line was the
		//    probe just before it). CopyTo is one interface call on a normal class object and the
		//    array it fills is read through the interop's own wrapper: no boxed struct, no
		//    interface dispatch per element, nothing an enumerator could get wrong.
		//
		//    THIS PATH MUST NOT END THE SEARCH WHEN IT FINDS NOTHING (2026-09-07).
		//    ImmutableArray<string> -- exactly what IUdonSymbolTable.GetSymbols() hands back on this
		//    build -- DOES satisfy the TryCast below, because the struct implements ICollection<T>.
		//    So this path was entered, its Count came back 0 for all 60 behaviours in a USharpVideo
		//    world, and "return outp" handed back an empty list, retiring the search before step 4 --
		//    the indexer, which is the one written FOR ImmutableArray. The symptom was silent and
		//    total: the injector found no VRCUrl symbol anywhere, concluded the world had no
		//    scriptable player, and fell back to the local-only direct LoadURL. The crash trail said
		//    so on every one of its 60 lines:
		//        "UdonSymbols gave nothing: GetSymbols/ICollection.CopyTo
		//         [ImmutableArray`1/ImmutableArray`1]"
		//    So: commit to this path only if it actually produced names; otherwise fall through. A
		//    genuinely empty collection costs one wasted indexer pass and still returns empty, which
		//    is the right answer anyway.
		try
		{
			var col = (seq as Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.ICollection<string>>();
			if (col != null)
			{
				LastPath = "ICollection.CopyTo";
				int total = col.Count;
				if (total > 0)
				{
					var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(total);
					col.CopyTo(arr, 0);
					int n = total > Cap ? Cap : total;
					for (int i = 0; i < n; i++) { string s = arr[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
				}
				if (outp.Count > 0) return outp;
			}
		}
		catch { }

		// 4) Indexable proxy: Length/Count + Item. Never for an ImmutableArray — its getters are the
		//    ones that crash on a default instance (see 2b).
			try
			{
				LastPath = "indexer";
				var t = seq.GetType();
				if (t.Name.StartsWith("ImmutableArray", StringComparison.Ordinal)) return outp;
				var lenP = t.GetProperty("Length") ?? t.GetProperty("Count");
				var item = t.GetProperty("Item");
				if (lenP?.GetValue(seq) is int len && item != null)
				{
					// ONE ARGS ARRAY, REUSED. This is a reflection call per element, and it became
					// the hot path the moment step 3 was taught to fall through to it — a video
					// injection reads the symbol table of every candidate script, which in a ProTV
					// world is several hundred symbols. Allocating a fresh object[1] for each of them
					// bought nothing: GetValue copies the value out before returning, so the same box
					// can carry every index.
					object[] args = new object[1];
					for (int i = 0; i < len && i < Cap; i++)
					{
						try
						{
							args[0] = i;
							string s = item.GetValue(seq, args) as string;
							if (!string.IsNullOrEmpty(s)) outp.Add(s);
						}
						catch { }
					}
				}
			}
			catch { }
			return outp;
		}

		// INDEXING AN IL2CPP List<T> WITHOUT CALLING get_Item.
		//
		// `list[i]` compiles to List<T>.get_Item, and on VRChat 1903 that method is MIS-BOUND. The mod
		// says so at startup, in as many words:
		//
		//     [MemberAlign] List`1 : le token 0x060035A6 tombe sur une methode de forme 'o5' au lieu
		//                            de 'g5' — appel refuse.
		//
		// 'g5' is "returns the generic parameter, takes an int" -- that is get_Item -- and the token
		// lands on something of shape 'o5' instead. Invoking it jumps into the wrong method and the
		// process dies inside il2cpp_runtime_invoke, an access violation no try/catch can stop. That
		// is what killed the game from ArchiveHijackModule.PickTarget, whose only sin was `live[i]`.
		//
		// The backing store is reachable with no call at all: `_items` and `_size` are FIELDS, and a
		// field read is an offset read repaired by FieldOffsetFix, never a runtime_invoke. Indexing the
		// array wrapper is likewise direct memory.
		//
		// The snapshot is also the safer shape for callers: VRChat rebuilds its category list while you
		// browse, and a managed copy cannot be invalidated mid-loop.
		internal static List<T> Items<T>(Il2CppSystem.Collections.Generic.List<T> list) where T : Il2CppObjectBase
		{
			var outp = new List<T>();
			if (list == null) return outp;

			// VALIDATE EVERY POINTER BEFORE TOUCHING IT — a try/catch CANNOT save this.
			//
			// The first version read `list._items` and then `arr.Length`, and on 1903 that crashed the
			// game inside il2cpp_array_length: `_items` came back pointing at memory that is not a live
			// array, and `.Length` dereferenced it. An access violation in native code is not a .NET
			// exception, so the surrounding try/catch never ran. The reflected/property reads of a
			// generic List on this reshuffled build are simply not trustworthy.
			//
			// So nothing is dereferenced until NativeGuard (VirtualQuery-backed) confirms the pointer is
			// a real il2cpp object: the list itself, then the backing array, then each element. A bad
			// pointer makes the feature return empty; it can no longer end the process.
			try
			{
				IntPtr listPtr;
				try { listPtr = list.Pointer; } catch { return outp; }
				if (!NativeGuard.IsLiveObject(listPtr)) return outp;

				int n;
				try { n = list._size; } catch { return outp; }
				if (n <= 0 || n > 16384) return outp;      // a category list of 16k is corruption, not data

				Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<T> arr;
				try { arr = list._items; } catch { return outp; }
				if (arr == null) return outp;

				IntPtr arrPtr;
				try { arrPtr = arr.Pointer; } catch { return outp; }
				// The crash was here: a garbage array pointer. Proven live before Length or indexing.
				if (!NativeGuard.IsLiveObject(arrPtr)) return outp;

				int cap;
				try { cap = arr.Length; } catch { return outp; }
				if (cap < 0 || cap > 16384) return outp;
				if (n > cap) n = cap;

				for (int i = 0; i < n; i++)
				{
					T item;
					try { item = arr[i]; } catch { continue; }
					if (item == null) continue;
					IntPtr ip;
					try { ip = item.Pointer; } catch { continue; }
					if (ip == IntPtr.Zero || !NativeGuard.IsLiveObject(ip)) continue;
					outp.Add(item);
				}
			}
			catch (Exception e)
			{
				if (!_itemsWarned)
				{
					_itemsWarned = true;
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[Il2CppSeq] lecture de List<T> par _items/_size impossible (" + e.Message
						+ ") — liste rendue vide plutot que d'appeler get_Item, qui tue le process sur ce build.");
				}
			}
			return outp;
		}

		private static bool _itemsWarned;

		// handled = the object IS an ImmutableArray (answer final, even when empty).
		private static bool TryImmutableArray(object seq, List<string> outp, out bool handled)
		{
			handled = false;
			var bo = seq as Il2CppObjectBase;
			if (bo == null) return false;
			IntPtr obj;
			try { obj = bo.Pointer; } catch { return false; }
			if (obj == IntPtr.Zero || !NativeGuard.IsLiveObject(obj)) return false;

			IntPtr klass = IL2CPP.il2cpp_object_get_class(obj);
			if (klass == IntPtr.Zero) return false;
			IntPtr np = IL2CPP.il2cpp_class_get_name(klass);
			string cn = np == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(np);
			if (cn == null || !cn.StartsWith("ImmutableArray", StringComparison.Ordinal)) return false;
			handled = true;

			IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, "array");
			if (field == IntPtr.Zero) return true;
			uint off = IL2CPP.il2cpp_field_get_offset(field);
			if (off < 0x10 || off > 0x100) return true;
			IntPtr arrPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(obj, (int)off);
			if (arrPtr == IntPtr.Zero || !NativeGuard.IsLiveObject(arrPtr)) return true;

			var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(arrPtr);
			int n = arr.Length;
			if (n < 0 || n > 100000) return true;
			for (int i = 0; i < n && i < Cap; i++) { string s = arr[i]; if (!string.IsNullOrEmpty(s)) outp.Add(s); }
			return true;
		}
	}
}
