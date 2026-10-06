using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace VRChatArchiveMod.Core
{
	// AN ACCESS VIOLATION IS NOT AN EXCEPTION YOU CAN CATCH.
	//
	// Every il2cpp field read is, underneath, `*(void**)(objectPointer + fieldOffset)`. Hand it a
	// proxy whose native object was never valid and the read lands on unmapped memory: .NET 6 does
	// not surface that as a catchable exception, it prints "Fatal error. AccessViolationException"
	// and ends the process. So the try/catch around our reflection kit is powerless against it —
	// the only defence is to not do the read.
	//
	// VRChat hands out exactly such a proxy at startup. `VRC.Player.prop_Player_0` is readable from
	// the first frame, long before the local player is spawned, and what comes back then is not a
	// live object. VaTags asks for the local user id as soon as the tag database is loaded, which is
	// immediately, so it read that proxy and took the game down.
	//
	// This asks Windows whether the memory is actually there before anyone touches it. VirtualQuery
	// never faults, whatever it is given.
	internal static class NativeGuard
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct MemoryBasicInformation
		{
			public IntPtr BaseAddress;
			public IntPtr AllocationBase;
			public uint AllocationProtect;
			public IntPtr RegionSize;
			public uint State;
			public uint Protect;
			public uint Type;
		}

		[DllImport("kernel32.dll")]
		private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

		private const uint MemCommit = 0x1000;
		private const uint PageGuard = 0x100;

		// Whitelist rather than "not PAGE_NOACCESS": the protection constants are distinct values,
		// not bit flags, so masking for one of them quietly matches the wrong pages.
		private static bool ProtectionAllowsRead(uint protect)
		{
			switch (protect & 0xFF & ~(PageGuard | 0x200u /*NOCACHE*/ | 0x400u /*WRITECOMBINE*/))
			{
				case 0x02: // PAGE_READONLY
				case 0x04: // PAGE_READWRITE
				case 0x08: // PAGE_WRITECOPY
				case 0x20: // PAGE_EXECUTE_READ
				case 0x40: // PAGE_EXECUTE_READWRITE
				case 0x80: // PAGE_EXECUTE_WRITECOPY
					return true;
				default:
					return false;   // PAGE_NOACCESS, PAGE_EXECUTE, anything unexpected
			}
		}

		// requireAligned is for OBJECT pointers, which are always 8-aligned — the check is what
		// rejects a stale value that happens to land on a mapped page. C strings are not aligned,
		// so a caller reading a name has to turn it off or every name comes back null.
		// THE REGION CACHE, AND WHY IT IS NOT A WEAKENING OF THE GUARD.
		//
		// Measured in production on 2026-09-18: VirtualQuery was costing ~1.5 ms A CALL in this
		// process. NativeGuard.Alive does two of them, the ESP called it once per player per frame,
		// and that ALONE was 304.7 ms of the ESP's 307 ms/s -- half the entire mod. Splitting the
		// timing proved it beyond doubt: resolving the handle took 0.1 ms, the VirtualQuery pair 31.6.
		// VRChat maps thousands of regions (4465 counted), and every query walks that structure.
		//
		// VaTags, HighlightEsp, PlayerStates and Radar all call through here too, so the tax was paid
		// across the whole mod.
		//
		// What is cached is the REGION the kernel described, not a verdict about an object. A pointer
		// landing inside a region already known to be committed and readable gets the same answer
		// VirtualQuery would give, computed from the same numbers -- so the check keeps its meaning.
		// The entries expire so a region that is genuinely unmapped cannot be trusted for long, and
		// only sizeable regions are kept: il2cpp's heaps are stable for the session, while a small
		// transient mapping is exactly the kind that can vanish.
		//
		// THE CLOCK HERE IS MANAGED, AND THAT IS NOT AN OPTIMISATION -- IT IS THE ONLY SAFE ONE.
		//
		// This used to read VaClock.Now -- an il2cpp call. That inverted the
		// dependency: the component whose whole job is to decide whether il2cpp memory is safe to touch
		// could only answer if il2cpp was ALREADY healthy. And its first callers are MemberAlign and
		// ObfuscatedClassFinder, running inside Load(), which is precisely when it is not: they are the
		// code that decides which method pointers are trustworthy, so none of them is trustworthy yet.
		// UnityEngine.Time is one of the core types TokenShiftFix rebinds, and on the Unity 6 build the
		// clock's pointer did not survive that pass intact -- il2cpp_runtime_invoke jumped into it and
		// took the whole process down with an access violation during plugin load. The try/catch around
		// it bought nothing: AccessViolationException is a corrupted-state exception, so the CLR never
		// offers it to a catch -- the process simply dies.
		// Whatever broke the clock on this particular build, the guard must not have been asking: it is
		// the floor everything else stands on, so it cannot stand on anything.
		// A tick count cannot fail, cannot be mis-bound, needs no il2cpp, and costs a fraction of a
		// crossing on a path this hot. Milliseconds are ample for a one-second TTL.
		private struct RegionNote { public long Base, End; public uint Protect; public long At; }
		private const int RegionSlots = 24;
		private const long RegionTtl = 1000;   // ms
		private static readonly RegionNote[] _regions = new RegionNote[RegionSlots];
		private static int _regionNext;

		internal static unsafe bool IsReadable(IntPtr p, int bytes = 8, bool requireAligned = true)
		{
			long addr = (long)p;
			if (addr <= 0) return false;
			if (addr < 0x10000) return false;      // the reserved low range: never a real object
			if (requireAligned && (addr & 7) != 0) return false;

			long nowR = Environment.TickCount64;
			for (int i = 0; i < RegionSlots; i++)
			{
				ref RegionNote r = ref _regions[i];
				if (r.End == 0 || nowR - r.At > RegionTtl) continue;
				if (addr < r.Base || addr + bytes > r.End) continue;
				return true;   // known committed + readable, and the read fits inside it
			}

			MemoryBasicInformation mbi;
			if (VirtualQuery(p, out mbi, (IntPtr)sizeof(MemoryBasicInformation)) == IntPtr.Zero) return false;
			if (mbi.State != MemCommit) return false;
			if ((mbi.Protect & PageGuard) != 0) return false;       // touching it would raise
			if (!ProtectionAllowsRead(mbi.Protect)) return false;
			// The read must not run past the end of the region it was queried in.
			long rBase = (long)mbi.BaseAddress, rEnd = rBase + (long)mbi.RegionSize;
			if ((long)mbi.RegionSize >= 0x10000)
			{
				_regions[_regionNext] = new RegionNote { Base = rBase, End = rEnd, Protect = mbi.Protect, At = nowR };
				_regionNext = (_regionNext + 1) % RegionSlots;
			}
			return addr + bytes <= rEnd;
		}

		// IS THERE ACTUAL CODE AT THIS ADDRESS?
		//
		// VRChat's il2cpp build STRIPS methods it decided nobody calls, and what is left behind is a
		// complete, healthy-looking MethodInfo whose entry point is null. il2cpp_runtime_invoke jumps
		// straight to it, so "the method resolved" and "the method can be called" are different facts --
		// and the difference is an access violation, which is not catchable. Thread.MemoryBarrier and
		// Photon's EventData.get_Sender both died exactly there.
		internal static unsafe bool IsExecutable(IntPtr p)
		{
			long addr = (long)p;
			if (addr < 0x10000) return false;
			MemoryBasicInformation mbi;
			if (VirtualQuery(p, out mbi, (IntPtr)sizeof(MemoryBasicInformation)) == IntPtr.Zero) return false;
			if (mbi.State != MemCommit) return false;
			if ((mbi.Protect & PageGuard) != 0) return false;
			switch (mbi.Protect & 0xFF & ~(PageGuard | 0x200u | 0x400u))
			{
				case 0x10: // PAGE_EXECUTE
				case 0x20: // PAGE_EXECUTE_READ
				case 0x40: // PAGE_EXECUTE_READWRITE
				case 0x80: // PAGE_EXECUTE_WRITECOPY
					return true;
				default:
					return false;
			}
		}

		// An il2cpp object begins with a pointer to its Il2CppClass. Garbage that happens to sit on a
		// committed page still fails this, because the first eight bytes have to be a pointer into
		// committed memory as well.
		internal static unsafe bool IsLiveObject(IntPtr p)
		{
			if (!IsReadable(p, 16)) return false;
			IntPtr klass = *(IntPtr*)p;
			return IsReadable(klass, 16);
		}

		// The one call sites use. A plain managed object needs no guard and passes straight through;
		// only il2cpp proxies are checked.
		internal static bool Alive(object o)
		{
			if (o == null) return false;
			Il2CppObjectBase b = o as Il2CppObjectBase;
			if (b == null) return true;
			IntPtr p;
			try { p = b.Pointer; }
			catch { return false; }   // ObjectCollectedException: the handle is already gone
			return IsLiveObject(p);
		}

		// Convenience for the "give me it or null" shape.
		internal static T OrNull<T>(T o) where T : class => Alive(o) ? o : null;
	}
}
