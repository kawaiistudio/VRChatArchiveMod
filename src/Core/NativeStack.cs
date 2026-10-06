using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.Runtime;

namespace VRChatArchiveMod.Core
{
	// NATIVE CALL-STACK SYMBOLIZER for il2cpp code, read-only.
	//
	// WHY: a Harmony prefix on Object.Destroy / GameObject.SetActive / Renderer.set_forceRenderingOff
	// tells us THAT VRChat touched a player's objects, but not from WHERE. The only thing that names
	// the caller is the native return-address chain, and on this build every VRChat method is an
	// obfuscated il2cpp function inside GameAssembly.dll with no symbols. What we do have is the
	// interop: every proxy class carries one `NativeMethodInfoPtr_<method>` static field per il2cpp
	// method (a MethodInfo*), and the MethodInfo's methodPointer is the function's start address. So:
	// collect (address, "Class.Method") for the classes we care about, sort them, and map each return
	// address to the nearest preceding method start. Frames outside the table print as
	// GameAssembly+RVA (resolvable offline later).
	//
	// HOW THE WALK WORKS (3.9.78): RtlCaptureStackBackTrace returned ZERO frames from inside a Harmony
	// prefix on this runtime (the JIT'd frames on top carry no OS unwind info the walker accepts), so
	// the walk is done by hand: a frame that RtlLookupFunctionEntry knows (the game's own code,
	// UnityPlayer, ntdll) is unwound with RtlVirtualUnwind; a frame it does not know (JIT'd code, the
	// detour stub) is stepped as a standard RBP frame (push rbp; mov rbp,rsp — what RyuJIT emits);
	// and if that still yields no game frame, the stack is SCANNED for return addresses that sit right
	// after a `call` instruction into GameAssembly (marked "(scan)": plausible, not proven).
	//
	// SAFETY: every memory read goes through NativeGuard.IsReadable; the CONTEXT buffer is ours;
	// nothing is written to any game structure. Building the table touches the proxy classes' static
	// constructors, exactly as any use of those classes does.
	internal static unsafe class NativeStack
	{
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW(string moduleName);
		[DllImport("kernel32.dll")]
		private static extern void RtlCaptureContext(IntPtr contextRecord);
		[DllImport("kernel32.dll")]
		private static extern IntPtr RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, IntPtr historyTable);
		[DllImport("kernel32.dll")]
		private static extern IntPtr RtlVirtualUnwind(uint handlerType, ulong imageBase, ulong controlPc, IntPtr functionEntry, IntPtr contextRecord, out IntPtr handlerData, out ulong establisherFrame, IntPtr contextPointers);

		// x64 CONTEXT: 1232 bytes, 16-byte aligned; Rsp @0x98, Rbp @0xA0, Rip @0xF8.
		private const int CtxSize = 0x4D0, OffRsp = 0x98, OffRbp = 0xA0, OffRip = 0xF8;
		[ThreadStatic] private static IntPtr _ctxRaw;

		private struct Sym { public long Addr; public string Name; }

		private static Sym[] _syms = new Sym[0];
		private static long _gaBase, _gaEnd, _upBase, _upEnd;
		private static bool _built;
		private static int _types, _methods;
		private static readonly object Gate = new object();
		private const string Prefix = "NativeMethodInfoPtr_";

		internal static string Status => _built
			? _methods + " method(s) from " + _types + " class(es); GameAssembly " + (_gaBase != 0 ? "0x" + _gaBase.ToString("X") + " (" + ((_gaEnd - _gaBase) >> 20) + " MB)" : "NOT FOUND")
			: "not built";

		internal static bool InGameAssembly(long a) => _gaBase != 0 && a >= _gaBase && a < _gaEnd;

		// ---------------------------------------------------------------- table

		internal static void Build(IEnumerable<Type> proxyTypes)
		{
			lock (Gate)
			{
				if (_built) return;
				_built = true;
				try
				{
					ModuleRange("GameAssembly.dll", out _gaBase, out _gaEnd);
					ModuleRange("UnityPlayer.dll", out _upBase, out _upEnd);
					var list = new List<Sym>(4096);
					var seen = new HashSet<Type>();
					foreach (Type t in proxyTypes)
					{
						if (t == null) continue;
						for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
						{
							if (!seen.Add(cur)) break;
							if (cur.Namespace != null && cur.Namespace.StartsWith("Il2CppInterop", StringComparison.Ordinal)) break;
							AddType(cur, list);
						}
					}
					list.Sort((a, b) => a.Addr.CompareTo(b.Addr));
					_syms = list.ToArray();
				}
				catch (Exception e)
				{
					try { VRChatArchiveModPlugin.Logger.LogWarning("[NativeStack] table build failed: " + Unwrap.Describe(e)); } catch { }
				}
			}
		}

		private static void ModuleRange(string name, out long b, out long e)
		{
			b = 0; e = 0;
			try
			{
				IntPtr h = GetModuleHandleW(name);
				if (h == IntPtr.Zero) return;
				byte* p = (byte*)h;
				int lfanew = *(int*)(p + 0x3C);
				int size = *(int*)(p + lfanew + 0x50);   // IMAGE_OPTIONAL_HEADER64.SizeOfImage
				if (size <= 0) return;
				b = (long)h; e = b + size;
			}
			catch { b = 0; e = 0; }
		}

		private static void AddType(Type t, List<Sym> list)
		{
			FieldInfo[] fields;
			try { fields = t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly); }
			catch { return; }
			int added = 0;
			string tn = t.Name;
			foreach (var f in fields)
			{
				if (f.FieldType != typeof(IntPtr) || !f.Name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
				IntPtr mi;
				try { mi = (IntPtr)f.GetValue(null); } catch { continue; }
				IntPtr mp = MethodPointerOf(mi);
				if (mp == IntPtr.Zero) continue;
				if (_gaBase != 0 && ((long)mp < _gaBase || (long)mp >= _gaEnd)) continue;   // a stub outside the game image is not a function start we can attribute
				list.Add(new Sym { Addr = (long)mp, Name = tn + "." + f.Name.Substring(Prefix.Length) });
				added++;
			}
			if (added > 0) { _types++; _methods += added; }
		}

		// The native entry point of an il2cpp MethodInfo*, through the corrected struct layout.
		internal static IntPtr MethodPointerOf(IntPtr methodInfo)
		{
			try
			{
				if (methodInfo == IntPtr.Zero || !NativeGuard.IsReadable(methodInfo, 0x58)) return IntPtr.Zero;
				return UnityVersionHandler.Wrap((Il2CppMethodInfo*)methodInfo).MethodPointer;
			}
			catch { return IntPtr.Zero; }
		}

		// "Type.Method" → native address from the table (exact suffix, or the first overload of a real
		// name such as OnDestroy_Public_Void_0). Returns how many table entries share that address:
		// more than one means the linker FOLDED identical function bodies together (ICF) and a detour
		// on it would intercept every method sharing the code — the 3.9.77 crash.
		internal static bool TryFind(string typeName, string method, out IntPtr ptr, out int shared)
		{
			ptr = IntPtr.Zero; shared = 0;
			string exact = typeName + "." + method, pre = exact + "_";
			var s = _syms;
			for (int i = 0; i < s.Length; i++)
			{
				string n = s[i].Name;
				if (n == exact || n.StartsWith(pre, StringComparison.Ordinal)) { ptr = (IntPtr)s[i].Addr; break; }
			}
			if (ptr == IntPtr.Zero) return false;
			long a = (long)ptr;
			for (int i = 0; i < s.Length; i++) if (s[i].Addr == a) shared++;
			return true;
		}

		// Size of the function starting at `fn` from its RUNTIME_FUNCTION (.pdata) entry; 0 when the
		// OS has no entry (a leaf/trivial body, exactly the kind that gets folded) or when `fn` is not
		// the entry's start.
		internal static int FunctionSize(IntPtr fn)
		{
			try
			{
				ulong imageBase;
				IntPtr fe = RtlLookupFunctionEntry((ulong)fn, out imageBase, IntPtr.Zero);
				if (fe == IntPtr.Zero || !NativeGuard.IsReadable(fe, 12, false)) return 0;
				uint begin = *(uint*)fe, end = *(uint*)((byte*)fe + 4);
				if ((ulong)fn != imageBase + begin) return 0;
				return (int)(end - begin);
			}
			catch { return 0; }
		}

		// ---------------------------------------------------------------- walk

		private static IntPtr Ctx()
		{
			if (_ctxRaw == IntPtr.Zero) _ctxRaw = Marshal.AllocHGlobal(CtxSize + 16);
			long a = (long)_ctxRaw;
			return (IntPtr)((a + 15) & ~15L);
		}

		// The current thread's native frames, innermost first, as "Class.Method+0xOFF ← ...".
		// Managed frames (this mod, Harmony, the runtime) are dropped; only the game's own code is
		// shown. Bounded: at most `maxShown` game frames out of at most 96 steps.
		internal static string Capture(int maxSteps = 96, int maxShown = 12)
		{
			try
			{
				IntPtr ctx = Ctx();
				RtlCaptureContext(ctx);
				ulong rip = *(ulong*)((byte*)ctx + OffRip), rsp = *(ulong*)((byte*)ctx + OffRsp), rbp = *(ulong*)((byte*)ctx + OffRbp);
				ulong rsp0 = rsp;
				var frames = new List<long>(16);
				int managedSteps = 0, steps = 0;
				while (steps++ < maxSteps && frames.Count < maxShown)
				{
					if (rip < 0x10000) break;
					bool game = InGameAssembly((long)rip) || (_upBase != 0 && (long)rip >= _upBase && (long)rip < _upEnd);
					if (game) frames.Add((long)rip);
					ulong imageBase;
					IntPtr fe = IntPtr.Zero;
					try { fe = RtlLookupFunctionEntry(rip, out imageBase, IntPtr.Zero); } catch { fe = IntPtr.Zero; imageBase = 0; }
					if (fe != IntPtr.Zero)
					{
						// RtlVirtualUnwind itself reads [rsp+X] / [rbp+X] per the function's unwind codes and cannot
						// be guarded from inside: only unwind while a comfortable window above both is mapped, and
						// never hand it a frame pointer that is not readable (a bogus RBP-chain value).
						if (!NativeGuard.IsReadable((IntPtr)rsp, 0x4000)) break;
						if (!NativeGuard.IsReadable((IntPtr)rbp, 0x4000)) rbp = rsp;
						*(ulong*)((byte*)ctx + OffRip) = rip; *(ulong*)((byte*)ctx + OffRsp) = rsp; *(ulong*)((byte*)ctx + OffRbp) = rbp;
						IntPtr hd; ulong ef;
						try { RtlVirtualUnwind(0, imageBase, rip, fe, ctx, out hd, out ef, IntPtr.Zero); } catch { break; }
						ulong nrip = *(ulong*)((byte*)ctx + OffRip), nrsp = *(ulong*)((byte*)ctx + OffRsp);
						if (nrip == 0 || nrsp <= rsp || nrsp - rsp > 0x100000) break;
						rip = nrip; rsp = nrsp; rbp = *(ulong*)((byte*)ctx + OffRbp);
					}
					else if (!game)
					{
						// JIT'd or detour frame without OS unwind data: step the RBP chain.
						if (rbp <= rsp || rbp - rsp > 0x100000 || !NativeGuard.IsReadable((IntPtr)rbp, 16)) break;
						ulong nrip = *(ulong*)(rbp + 8), nrbp = *(ulong*)rbp;
						rsp = rbp + 16; rbp = nrbp; rip = nrip;
						if (++managedSteps > 48) break;
					}
					else
					{
						// Game code without a .pdata entry = leaf: its return address is at [rsp].
						if (!NativeGuard.IsReadable((IntPtr)rsp, 8)) break;
						rip = *(ulong*)rsp; rsp += 8;
					}
				}
				bool scanned = false;
				if (frames.Count == 0)
				{
					scanned = true;
					Scan(rsp0, frames, maxShown);
				}
				if (frames.Count == 0) return "no native frame (walk: " + steps + " steps, " + managedSteps + " managed; scan: nothing)";
				var sb = new StringBuilder(256);
				for (int i = 0; i < frames.Count; i++) { if (i > 0) sb.Append(" ← "); sb.Append(Symbolize(frames[i])); }
				if (scanned) sb.Append("  (scan: return addresses found on the stack, order = depth, unproven)");
				return sb.ToString();
			}
			catch (Exception e) { return "stack unavailable (" + e.GetType().Name + ")"; }
		}

		// Values on the stack that point into GameAssembly right after a `call` instruction.
		private static void Scan(ulong rsp, List<long> frames, int max)
		{
			try
			{
				const int Span = 48 * 1024;
				for (ulong p = rsp & ~7UL; p < rsp + Span && frames.Count < max; p += 8)
				{
					if ((p & 0xFFF) == 0 && !NativeGuard.IsReadable((IntPtr)p, 8)) break;
					if (p == (rsp & ~7UL) && !NativeGuard.IsReadable((IntPtr)p, 8)) break;
					long v = *(long*)p;
					if (!InGameAssembly(v)) continue;
					if (!NativeGuard.IsReadable((IntPtr)(v - 8), 8, false)) continue;
					byte* c = (byte*)v;
					bool call = c[-5] == 0xE8                                        // call rel32
						|| (c[-6] == 0xFF && (c[-5] & 0x38) == 0x10)                  // call [rip+disp32] / [reg+disp32]
						|| (c[-2] == 0xFF && (c[-1] & 0xF8) == 0xD0)                  // call reg
						|| (c[-3] == 0xFF && (c[-2] & 0x38) == 0x10)                  // call [reg+disp8]
						|| (c[-3] == 0x41 && c[-2] == 0xFF && (c[-1] & 0xF8) == 0xD0); // call r8..r15
					if (!call) continue;
					if (!frames.Contains(v)) frames.Add(v);
				}
			}
			catch { }
		}

		private static string Symbolize(long a)
		{
			if (InGameAssembly(a))
			{
				int idx = Nearest(a);
				if (idx >= 0)
				{
					long off = a - _syms[idx].Addr;
					if (off < 0x8000) return _syms[idx].Name + "+0x" + off.ToString("X");
				}
				return "GameAssembly+0x" + (a - _gaBase).ToString("X");
			}
			if (_upBase != 0 && a >= _upBase && a < _upEnd) return "UnityPlayer+0x" + (a - _upBase).ToString("X");
			return "0x" + a.ToString("X");
		}

		// Greatest table entry whose address is <= a, or -1.
		private static int Nearest(long a)
		{
			var s = _syms;
			int lo = 0, hi = s.Length - 1, best = -1;
			while (lo <= hi)
			{
				int mid = (lo + hi) >> 1;
				if (s[mid].Addr <= a) { best = mid; lo = mid + 1; }
				else hi = mid - 1;
			}
			return best;
		}
	}
}
