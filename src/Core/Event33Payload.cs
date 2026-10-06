using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// EVENT 33 PAYLOAD DECODER — reads a Photon `Dictionary<byte, object>` (or `<string, object>`)
	// STRAIGHT OUT OF MEMORY, without ever building an Il2CppInterop wrapper for a value.
	//
	// WHY NOT THE WRAPPER: mod 3.9.73 enumerated this dictionary through the interop
	// (IDictionaryEnumerator.Value) and VRChat died on the first event 33 with an uncatchable access
	// violation — the interface dispatch on the boxed struct enumerator hands the value-type method a
	// boxed `this`. Nothing in that path can be guarded from managed code, so this file does not use
	// it. It uses only:
	//   * il2cpp_class_get_field_from_name + il2cpp_field_get_offset (repaired by FieldOffsetFix) to
	//     find the BCL fields `entries`/`count` of the dictionary and `hashCode`/`next`/`key`/`value`
	//     of its Entry struct — mscorlib names are not obfuscated on this build;
	//   * il2cpp_object_get_class / il2cpp_class_get_name for the value's class;
	//   * two Il2CppClass offsets pinned by disassembly of this exact binary: element_class @+0x40 and
	//     element_size @+0x104 (the exported getters for those are mis-mapped in the shipped interop,
	//     so the raw read is the SAFER option here);
	//   * raw reads, every one of them behind NativeGuard.IsReadable, so a wrong assumption produces
	//     garbage text, never a fault.
	// Nothing is written, nothing is invoked on the game's side.
	internal static unsafe class Event33Payload
	{
		private sealed class Layout
		{
			public bool Ok; public string Err;
			public int Entries, Count;                 // on the dictionary
			public int HashCode, Next, Key, Value;     // inside Entry
		}

		private static readonly Dictionary<IntPtr, Layout> Layouts = new Dictionary<IntPtr, Layout>();
		private const int MaxEntries = 32;
		private const int MaxDepth = 2;
		private const int MaxOut = 900;

		internal static string Describe(IntPtr dict)
		{
			try
			{
				bool verified = false;
				try { verified = FieldOffsetFix.Verified; } catch { }
				if (!verified) return "decode skipped (FieldOffsetFix not verified: field offsets unreliable)";
				var sb = new StringBuilder(256);
				DescribeDict(dict, 0, sb);
				if (sb.Length > MaxOut) { sb.Length = MaxOut; sb.Append("…"); }
				return sb.ToString();
			}
			catch (Exception e) { return "decode failed (" + e.GetType().Name + ": " + e.Message + ")"; }
		}

		private static Layout Resolve(IntPtr klass)
		{
			Layout l;
			if (Layouts.TryGetValue(klass, out l)) return l;
			l = new Layout();
			try
			{
				IntPtr fEntries = Field(klass, "entries", "_entries");
				IntPtr fCount = Field(klass, "count", "_count");
				if (fEntries == IntPtr.Zero || fCount == IntPtr.Zero) { l.Err = "no entries/count field on " + ClassName(klass); Layouts[klass] = l; return l; }
				l.Entries = (int)IL2CPP.il2cpp_field_get_offset(fEntries);
				l.Count = (int)IL2CPP.il2cpp_field_get_offset(fCount);
				if (l.Entries < 0x10 || l.Entries > 0x200 || l.Count < 0x10 || l.Count > 0x200) { l.Err = "implausible offsets entries@" + l.Entries + " count@" + l.Count; Layouts[klass] = l; return l; }
				l.Ok = true;   // the Entry half is resolved from the live array, below
			}
			catch (Exception e) { l.Err = e.GetType().Name; }
			Layouts[klass] = l;
			return l;
		}

		private static IntPtr Field(IntPtr klass, string a, string b)
		{
			IntPtr f = IntPtr.Zero;
			try { f = IL2CPP.il2cpp_class_get_field_from_name(klass, a); } catch { }
			if (f == IntPtr.Zero) { try { f = IL2CPP.il2cpp_class_get_field_from_name(klass, b); } catch { } }
			return f;
		}

		private static string ClassName(IntPtr klass)
		{
			try
			{
				if (!NativeGuard.IsReadable(klass, 0x138)) return "?";
				IntPtr n = IL2CPP.il2cpp_class_get_name(klass);
				if (!NativeGuard.IsReadable(n, 1, false)) return "?";
				string s = Marshal.PtrToStringUTF8(n) ?? "?";
				if (s.Length > 48) s = s.Substring(0, 48);
				for (int i = 0; i < s.Length; i++) if (s[i] > 127) return "obf";
				return s;
			}
			catch { return "?"; }
		}

		private static void DescribeDict(IntPtr dict, int depth, StringBuilder sb)
		{
			if (!NativeGuard.IsLiveObject(dict)) { sb.Append("<not a live object>"); return; }
			IntPtr klass = *(IntPtr*)dict;
			Layout l = Resolve(klass);
			if (!l.Ok) { sb.Append("<").Append(l.Err).Append('>'); return; }

			if (!NativeGuard.IsReadable(dict, Math.Max(l.Entries, l.Count) + 8)) { sb.Append("<dict body unreadable>"); return; }
			int count = *(int*)((byte*)dict + l.Count);
			IntPtr arr = *(IntPtr*)((byte*)dict + l.Entries);
			sb.Append("{n=").Append(count);
			if (count <= 0) { sb.Append('}'); return; }
			if (!NativeGuard.IsLiveObject(arr) || !NativeGuard.IsReadable(arr, 0x20)) { sb.Append(" entries unreadable}"); return; }

			IntPtr arrClass = *(IntPtr*)arr;
			if (!NativeGuard.IsReadable(arrClass, 0x138)) { sb.Append(" array class unreadable}"); return; }
			IntPtr elemClass = *(IntPtr*)((byte*)arrClass + 0x40);      // Il2CppClass.element_class (pinned)
			int elemSize = *(int*)((byte*)arrClass + 0x104);              // Il2CppClass.element_size (pinned)
			long length = *(long*)((byte*)arr + 0x18);                    // il2cpp array max_length
			if (elemSize < 12 || elemSize > 64 || length <= 0 || length > 1 << 20 || !NativeGuard.IsReadable(elemClass, 0x138)) { sb.Append(" implausible entry array (elemSize=").Append(elemSize).Append(" len=").Append(length).Append(")}"); return; }

			// Entry field offsets, resolved once per Entry class (the array's element class).
			if (l.Value == 0)
			{
				IntPtr fh = Field(elemClass, "hashCode", "_hashCode"), fn = Field(elemClass, "next", "_next"), fk = Field(elemClass, "key", "_key"), fv = Field(elemClass, "value", "_value");
				if (fh == IntPtr.Zero || fn == IntPtr.Zero || fk == IntPtr.Zero || fv == IntPtr.Zero) { sb.Append(" Entry fields not found}"); return; }
				int oh = (int)IL2CPP.il2cpp_field_get_offset(fh), on = (int)IL2CPP.il2cpp_field_get_offset(fn), ok = (int)IL2CPP.il2cpp_field_get_offset(fk), ov = (int)IL2CPP.il2cpp_field_get_offset(fv);
				// Value-type field offsets in il2cpp include the 16-byte object header; array elements do not.
				oh -= 0x10; on -= 0x10; ok -= 0x10; ov -= 0x10;
				if (oh < 0 || on < 0 || ok < 0 || ov < 0 || oh + 4 > elemSize || on + 4 > elemSize || ok + 8 > elemSize || ov + 8 > elemSize) { sb.Append(" implausible Entry layout}"); return; }
				l.HashCode = oh; l.Next = on; l.Key = ok; l.Value = ov;
			}

			int limit = (int)Math.Min(Math.Min(count, length), MaxEntries);
			byte* data = (byte*)arr + 0x20;
			if (!NativeGuard.IsReadable((IntPtr)data, limit * elemSize, false)) { sb.Append(" entries unreadable}"); return; }
			int shown = 0;
			for (int i = 0; i < limit; i++)
			{
				byte* e = data + i * elemSize;
				int hash = *(int*)(e + l.HashCode);
				int next = *(int*)(e + l.Next);
				if (next < -1 || hash == -1) continue;   // a freed slot, in either BCL layout
				sb.Append(shown == 0 ? " " : ", ");
				long k = *(long*)(e + l.Key);
				DescribeKey(k, sb);
				sb.Append('=');
				DescribeValue(*(IntPtr*)(e + l.Value), depth, sb);
				shown++;
				if (sb.Length > MaxOut) break;
			}
			if (count > limit) sb.Append(" …");
			sb.Append('}');
		}

		private static void DescribeKey(long k, StringBuilder sb)
		{
			// A string key is an object pointer; a byte/int key is a small integer with zero padding.
			if ((k & ~0xFFFFL) == 0) { sb.Append(k); return; }
			IntPtr p = (IntPtr)k;
			if (NativeGuard.IsLiveObject(p))
			{
				string cn = ClassName(*(IntPtr*)p);
				string s;
				if (cn == "String" && Il2CppStr.TryRead(p, out s)) { sb.Append('"').Append(s).Append('"'); return; }
				sb.Append('<').Append(cn).Append('>'); return;
			}
			sb.Append("0x").Append(k.ToString("X"));
		}

		private static void DescribeValue(IntPtr v, int depth, StringBuilder sb)
		{
			if (v == IntPtr.Zero) { sb.Append("null"); return; }
			if (!NativeGuard.IsLiveObject(v)) { sb.Append("<bad ptr>"); return; }
			IntPtr klass = *(IntPtr*)v;
			string cn = ClassName(klass);
			byte* body = (byte*)v + 0x10;   // boxed primitives keep their value right after the object header
			bool body8 = NativeGuard.IsReadable(v, 0x18);
			switch (cn)
			{
				case "String": { string s; if (Il2CppStr.TryRead(v, out s)) sb.Append('"').Append(s.Length > 96 ? s.Substring(0, 96) + "…" : s).Append('"'); else sb.Append("<string unreadable>"); return; }
				case "Boolean": if (body8) sb.Append(*body != 0 ? "true" : "false"); return;
				case "Byte": if (body8) sb.Append("(byte)").Append(*body); return;
				case "SByte": if (body8) sb.Append("(sbyte)").Append(*(sbyte*)body); return;
				case "Int16": if (body8) sb.Append("(short)").Append(*(short*)body); return;
				case "UInt16": if (body8) sb.Append("(ushort)").Append(*(ushort*)body); return;
				case "Int32": if (body8) sb.Append(*(int*)body); return;
				case "UInt32": if (body8) sb.Append("(uint)").Append(*(uint*)body); return;
				case "Int64": if (body8) sb.Append("(long)").Append(*(long*)body); return;
				case "UInt64": if (body8) sb.Append("(ulong)").Append(*(ulong*)body); return;
				case "Single": if (body8) sb.Append(*(float*)body).Append('f'); return;
				case "Double": if (body8) sb.Append(*(double*)body).Append('d'); return;
				case "Char": if (body8) sb.Append('\'').Append(*(char*)body).Append('\''); return;
				case "Dictionary`2":
					if (depth + 1 >= MaxDepth) { sb.Append("<Dictionary`2>"); return; }
					DescribeDict(v, depth + 1, sb); return;
			}
			if (cn.EndsWith("[]", StringComparison.Ordinal))
			{
				if (!NativeGuard.IsReadable(v, 0x20)) { sb.Append('<').Append(cn).Append('>'); return; }
				long len = *(long*)((byte*)v + 0x18);
				sb.Append(cn).Append('[').Append(len).Append(']');
				if (len <= 0 || len > 4096) return;
				byte* d = (byte*)v + 0x20;
				string en = cn.Substring(0, cn.Length - 2);
				int n = (int)Math.Min(len, 8);
				if (en == "String" && NativeGuard.IsReadable((IntPtr)d, n * 8, false))
				{
					sb.Append('{');
					for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); IntPtr sp = *(IntPtr*)(d + i * 8); string s; if (sp == IntPtr.Zero) sb.Append("null"); else if (NativeGuard.IsLiveObject(sp) && Il2CppStr.TryRead(sp, out s)) sb.Append('"').Append(s).Append('"'); else sb.Append('?'); }
					if (len > n) sb.Append(",…");
					sb.Append('}');
				}
				else if (en == "Byte" && NativeGuard.IsReadable((IntPtr)d, (int)Math.Min(len, 32), false))
				{
					sb.Append("{hex ");
					int m = (int)Math.Min(len, 32);
					for (int i = 0; i < m; i++) sb.Append(d[i].ToString("X2")).Append(i + 1 < m ? " " : "");
					if (len > m) sb.Append(" …");
					sb.Append('}');
				}
				else if (en == "Int32" && NativeGuard.IsReadable((IntPtr)d, n * 4, false))
				{
					sb.Append('{');
					for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(*(int*)(d + i * 4)); }
					if (len > n) sb.Append(",…");
					sb.Append('}');
				}
				return;
			}
			sb.Append('<').Append(cn).Append('>');
		}
	}
}
