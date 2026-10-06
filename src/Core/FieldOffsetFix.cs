using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace VRChatArchiveMod.Core
{
	// THE ONE BUG BEHIND BOTH CRASHES.
	//
	// Every il2cpp field access is `*(T*)(objectPointer + fieldOffset)`, and Il2CppInterop gets that
	// offset from its own reimplementation of il2cpp_field_get_offset, which reads it at
	// FieldInfo+0x08 — a constant measured against some older VRChat build. On this build 0x08 holds
	// the metadata TOKEN. So every field read returns a value from address object+0x04000770-ish:
	// unmapped memory, an access violation, and .NET ends the process rather than raising something
	// catchable.
	//
	// Measured, not guessed: System.Delegate's four leading fields report tokens 0x04000770..3 while
	// FieldInfo+0x18 holds 0x10, 0x18, 0x20, 0x28 — the textbook layout of a delegate. Same story for
	// VRC.Player.field_Private_APIUser_0.
	//
	// This looked for a long time like several unrelated failures, because only `field_*` members go
	// through it. A `prop_*` member is a method call and resolves fine, so most of the mod worked and
	// the few things that touch fields directly — the delegate bridge, the local player's APIUser —
	// died in ways that each looked like their own bug.
	//
	// The slot is FOUND, not assumed, and the patch is not installed unless all four known fields
	// agree. A wrong answer here would corrupt every field access in the process, so "probably right"
	// is not good enough.
	internal static class FieldOffsetFix
	{
		private static int _slot = -1;

		// STATIC fields are a SECOND, separate bug. Instance access goes through il2cpp_field_get_offset
		// (patched above) and the native il2cpp_field_get_value; but this build's il2cpp has no usable
		// native static-field export, so Il2CppInterop ships a MANAGED reimplementation of
		// il2cpp_field_static_get_value / _set_value that reads the FieldInfo struct inline — class at
		// +0x00, offset at +0x08, type at +0x10 — a layout that predates the +0x18 discovery. On this
		// build +0x08 is the token, so every static read throws "The IL2CPP static field offset is
		// invalid." The mod (prop_* everywhere) never hit it; UnityExplorer, which reads Unity static
		// fields constantly, does nothing else. Same repair, extended: find the real class/type/offset
		// slots, then reimplement the two functions off them — but ONLY if all three verify.
		private static int _classSlot = -1, _typeSlot = -1;
		internal static bool StaticVerified => _classSlot >= 0 && _typeSlot >= 0;

		// True once the slot is confirmed and the patch is live. Other code keys off this rather than
		// assuming the repair happened.
		internal static bool Verified => _slot >= 0;
		internal static int Slot => _slot;

		// System.Delegate's layout is fixed by il2cpp itself: object header, then these.
		private static readonly (string Name, uint Offset)[] Known =
		{
			("method_ptr", 0x10), ("invoke_impl", 0x18), ("m_target", 0x20), ("method", 0x28),
		};

		internal static unsafe void Install()
		{
			try
			{
				IntPtr klass = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Delegate");
				if (klass == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[FieldOffsetFix] System.Delegate not found — cannot verify, leaving field offsets alone.");
					return;
				}

				var fields = new IntPtr[Known.Length];
				for (int i = 0; i < Known.Length; i++)
				{
					fields[i] = IL2CPP.il2cpp_class_get_field_from_name(klass, Known[i].Name);
					if (fields[i] == IntPtr.Zero || !NativeGuard.IsReadable(fields[i], 0x30))
					{
						VRChatArchiveModPlugin.Logger.LogWarning(
							"[FieldOffsetFix] cannot read FieldInfo for " + Known[i].Name + " — leaving field offsets alone.");
						return;
					}
				}

				// The slot that gives the right answer for ALL four, and only that slot.
				int found = -1;
				for (int slot = 0; slot <= 0x28; slot += 4)
				{
					bool all = true;
					for (int i = 0; i < Known.Length && all; i++)
						all = *(uint*)((byte*)fields[i] + slot) == Known[i].Offset;
					if (!all) continue;
					if (found >= 0)
					{
						VRChatArchiveModFallback("[FieldOffsetFix] ambiguous: slots 0x" + found.ToString("X")
							+ " and 0x" + slot.ToString("X") + " both fit. Refusing to guess.");
						return;
					}
					found = slot;
				}

				if (found < 0)
				{
					VRChatArchiveModFallback("[FieldOffsetFix] no FieldInfo slot holds the expected offsets. "
						+ "The layout changed again; field access stays broken rather than made worse.");
					return;
				}

				uint stock = *(uint*)((byte*)fields[0] + 0x08);
				if (found == 0x08)
				{
					VRChatArchiveModPlugin.Logger.LogInfo(
						"[FieldOffsetFix] field offsets already correct (slot 0x08) — nothing to patch.");
					_slot = found;
					return;
				}

				MethodInfo target = typeof(IL2CPP).GetMethod("il2cpp_field_get_offset",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
					null, new[] { typeof(IntPtr) }, null);
				if (target == null)
				{
					VRChatArchiveModFallback("[FieldOffsetFix] IL2CPP.il2cpp_field_get_offset not found.");
					return;
				}

				_slot = found;
				VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
					typeof(FieldOffsetFix).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));

				VRChatArchiveModPlugin.Logger.LogInfo(
					"[FieldOffsetFix] field offsets repaired: read from FieldInfo+0x" + found.ToString("X")
					+ ", not +0x08 (which holds the metadata token 0x" + stock.ToString("X")
					+ "). All four System.Delegate fields agree.");

				// The offset slot is proven; now repair static-field access off the same FieldInfo.
				TryInstallStaticFix(fields, klass);
			}
			catch (Exception e)
			{
				_slot = -1;
				VRChatArchiveModPlugin.Logger.LogError("[FieldOffsetFix] install failed: " + Unwrap.Describe(e));
			}
		}

		// Finds where the FieldInfo really keeps the declaring CLASS and the field TYPE, verified the
		// same way the offset was: a value that matches known ground truth on ALL four System.Delegate
		// fields. The class slot must equal System.Delegate's own class pointer; the type slot must
		// point at something whose il2cpp type-enum byte (type+0x0A) is a real enum value. Only when
		// both are found — and distinct from each other and the offset slot — are the static-field
		// functions reimplemented. If anything is unclear it installs nothing: static reads stay broken
		// (exactly as now) rather than risk a wrong pointer, and the mod is untouched.
		private static unsafe void TryInstallStaticFix(IntPtr[] fields, IntPtr klass)
		{
			try
			{
				int classSlot = -1;
				for (int slot = 0; slot <= 0x28; slot += 8)
				{
					bool all = true;
					foreach (var f in fields) if (*(IntPtr*)((byte*)f + slot) != klass) { all = false; break; }
					if (all) { classSlot = slot; break; }
				}

				int typeSlot = -1;
				for (int slot = 0; slot <= 0x28; slot += 8)
				{
					if (slot == classSlot || slot == _slot) continue;
					bool all = true;
					foreach (var f in fields)
					{
						IntPtr t = *(IntPtr*)((byte*)f + slot);
						if (t == IntPtr.Zero || !NativeGuard.IsReadable(t, 0x10)) { all = false; break; }
						byte e = *((byte*)t + 0x0A);   // Il2CppTypeEnum lives at type+0x0A
						if (e < 0x01 || e > 0x21) { all = false; break; }
					}
					if (all) { typeSlot = slot; break; }
				}

				if (classSlot < 0 || typeSlot < 0)
				{
					VRChatArchiveModPlugin.Logger.LogWarning(
						"[FieldOffsetFix] static-field layout not verified (class=" + classSlot + ", type=" + typeSlot
						+ ") — static reads left as-is. The mod is unaffected; UnityExplorer static fields stay broken.");
					return;
				}

				_classSlot = classSlot; _typeSlot = typeSlot;
				PatchStatic("il2cpp_field_static_get_value", nameof(StaticGetPrefix));
				PatchStatic("il2cpp_field_static_set_value", nameof(StaticSetPrefix));
				VRChatArchiveModPlugin.Logger.LogInfo(
					"[FieldOffsetFix] static-field access repaired: class@0x" + classSlot.ToString("X")
					+ ", type@0x" + typeSlot.ToString("X") + ", offset@0x" + _slot.ToString("X")
					+ ". UnityExplorer static reads should work now.");
			}
			catch (Exception e)
			{
				_classSlot = -1; _typeSlot = -1;
				VRChatArchiveModPlugin.Logger.LogWarning("[FieldOffsetFix] static-field fix skipped: " + Unwrap.Describe(e));
			}
		}

		private static void PatchStatic(string method, string prefix)
		{
			MethodInfo target = typeof(IL2CPP).GetMethod(method,
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				null, new[] { typeof(IntPtr), typeof(void).MakePointerType() }, null);
			if (target == null) throw new MissingMethodException("IL2CPP." + method + " not found");
			VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: new HarmonyMethod(
				typeof(FieldOffsetFix).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
		}

		// il2cpp type-enum byte -> value size in the static block. Ports Il2CppInterop's own table.
		private static unsafe nuint SizeOfType(IntPtr type)
		{
			if (type == IntPtr.Zero) return (nuint)IntPtr.Size;
			switch (*((byte*)type + 0x0A))
			{
				case 2: case 4: case 5: return 1u;
				case 3: case 6: case 7: return 2u;
				case 8: case 9: case 12: return 4u;
				case 10: case 11: case 13: return 8u;
				default: return (nuint)IntPtr.Size;
			}
		}

		// Reference-typed static fields need the GC write barrier; value types are a plain copy.
		private static unsafe bool IsRefType(IntPtr type)
		{
			if (type == IntPtr.Zero) return false;
			byte b = *((byte*)type + 0x0A);
			switch (b) { case 14: case 18: case 20: case 28: case 29: return true; }
			if (b != 21) return false;
			IntPtr k = IL2CPP.il2cpp_class_from_type(type);
			return k != IntPtr.Zero && !IL2CPP.il2cpp_class_is_valuetype(k);
		}

		// The static block lives at Il2CppClass+0xB8 on this build (the value the stock resolver uses).
		private const int StaticFieldsSlot = 0xB8;

		// Reimplementations that mirror the stock resolver exactly, changing ONLY where the class,
		// offset and type are read from the FieldInfo. Return false to replace the broken original.
		private static unsafe bool StaticGetPrefix(IntPtr field, void* value)
		{
			if (field == IntPtr.Zero || value == null) return false;
			IntPtr klass = *(IntPtr*)((byte*)field + _classSlot);
			int offset = *(int*)((byte*)field + _slot);
			IntPtr type = *(IntPtr*)((byte*)field + _typeSlot);
			if (klass == IntPtr.Zero || offset < 0) { *(IntPtr*)value = IntPtr.Zero; return false; }
			IL2CPP.il2cpp_runtime_class_init(klass);
			byte* data = *(byte**)((byte*)klass + StaticFieldsSlot);
			if (data == null) { *(IntPtr*)value = IntPtr.Zero; return false; }
			nuint size = SizeOfType(type);
			if ((uint)offset > 0x100000u || size > (nuint)((nint)0x100000 - offset)) { *(IntPtr*)value = IntPtr.Zero; return false; }
			Buffer.MemoryCopy(data + offset, value, size, size);
			return false;
		}

		private static unsafe bool StaticSetPrefix(IntPtr field, void* value)
		{
			if (field == IntPtr.Zero || value == null) return false;
			IntPtr klass = *(IntPtr*)((byte*)field + _classSlot);
			int offset = *(int*)((byte*)field + _slot);
			IntPtr type = *(IntPtr*)((byte*)field + _typeSlot);
			if (klass == IntPtr.Zero || offset < 0) return false;
			IL2CPP.il2cpp_runtime_class_init(klass);
			byte* data = *(byte**)((byte*)klass + StaticFieldsSlot);
			if (data == null) return false;
			nuint size = SizeOfType(type);
			if ((uint)offset > 0x100000u || size > (nuint)((nint)0x100000 - offset)) return false;
			byte* dst = data + offset;
			if (IsRefType(type)) IL2CPP.il2cpp_gc_wbarrier_set_field(IntPtr.Zero, (IntPtr)dst, *(IntPtr*)value);
			else Buffer.MemoryCopy(value, dst, size, size);
			return false;
		}

		private static void VRChatArchiveModFallback(string msg)
			=> VRChatArchiveModPlugin.Logger.LogWarning(msg);

		// A FIELD THAT DOES NOT EXIST MUST NOT READ OFFSET 0.
		//
		// Offset 0 of an il2cpp object is its CLASS POINTER. Handing that back as the value of a
		// missing reference field produces a reference to something that is not an object, and the
		// process dies later — in the garbage collector, inside coreclr, at a moment with no relation
		// to the read. That is the access violation that kept ending the run once renamed classes
		// became castable again: the class is recovered, its 1886 field names are not, and every read
		// fell through to offset 0.
		//
		// The object header is `+0 klass, +8 monitor`, and the monitor slot is null on any object that
		// is not currently locked. A missing field reads there instead: a reference field yields null,
		// a numeric field yields zero, and every caller already handles both. No real field lives at
		// that offset, so nothing valid is affected.
		private const uint MissingFieldOffset = 8;

		// Runs for every field access in the process, so it does exactly one load and nothing else.
		// The pointer comes from il2cpp itself; validating it here would cost a syscall per access.
		private static unsafe bool Prefix(IntPtr field, ref uint __result)
		{
			__result = field == IntPtr.Zero ? MissingFieldOffset : *(uint*)((byte*)field + _slot);
			return false;
		}
	}
}
