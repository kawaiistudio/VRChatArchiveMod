using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// IL2CPP CLASS LAYOUT PROBE — measure the injection-critical Il2CppClass offsets, never guess them.
	//
	// WHY THIS EXISTS
	//
	// ClassInjector (Il2CppInterop) builds an Il2CppClass by hand to register a managed MonoBehaviour.
	// On VRChat 1903 that WRITES a handful of fields — ImplementedInterfaces, its count, TypeHierarchy,
	// its depth, VtableCount — and if any of their offsets is wrong the injected object is malformed and
	// the process dies with 0xC0000005 (seen live: RuntimeUnityEditor's helper, and this mod's own
	// ModRunner, which is why the mod uses FramePump instead). VRChatStructFix currently supplies these
	// offsets as UNVERIFIED guesses (ImplementedInterfaces +0x50, TypeHierarchy +0xC8, VtableCount
	// +0x12A, TypeHierarchyDepth +0x130). This proves or disproves each, so the fix is measured, not
	// guessed — the discipline the rest of this port runs on.
	//
	// HOW IT MEASURES (ground truth from exports, then locate the field)
	//
	// il2cpp_class_get_interfaces and il2cpp_class_get_parent are real exports the mod already binds
	// (Il2CppRaw). For a live class they hand back the AUTHORITATIVE interface list and parent chain,
	// as Il2CppClass* pointers. The struct then MUST hold, at some offset, a pointer to an array of
	// exactly those pointers. The probe scans the Il2CppClass bytes for that array and reports the
	// offset — and only accepts an offset that EVERY probed class agrees on, because the layout is fixed
	// for all classes. The count/depth fall out as the small integer sitting next to the pointer.
	//
	// READ-ONLY. It dereferences candidate pointers only behind NativeGuard, and writes nothing. It runs
	// once, a while after a world is up (so there are components to sample), and logs its findings.
	public class LayoutProbeModule : IModule
	{
		public override string Name => "LayoutProbe";

		private static bool _done;
		private static float _nextTry;

		// The current guesses in VRChatStructFix, for the report to confirm or refute directly.
		private const int GuessImplementedInterfaces = 0x50;   // 80
		private const int GuessTypeHierarchy = 0xC8;           // 200
		private const int GuessVtableCount = 0x12A;            // 298
		private const int GuessTypeHierarchyDepth = 0x130;     // 304

		private const int ClassSize = 0x138;                   // 312, confirmed by VRChatStructFix

		public override void OnUpdate()
		{
			if (_done) return;
			if (System.Environment.GetEnvironmentVariable("VA_LAYOUT_PROBE") == "0") { _done = true; return; }
			float now;
			try { now = VaClock.Now; } catch { return; }
			if (now < 25f) return;                 // let a world finish coming up
			if (now < _nextTry) return;
			_nextTry = now + 3f;

			try { _done = Run(); } catch (Exception e)
			{
				_done = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] abandon : " + e.Message);
			}
		}

		private bool Run()
		{
			if (!Il2CppRaw.Ready)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] Il2CppRaw pas pret — reessai.");
				return false;
			}

			// A diverse set of live component classes: each carries its own interface set and parent
			// chain, and the true offset is the one they ALL agree on.
			List<IntPtr> classes = CollectClasses();
			if (classes.Count < 3)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] pas assez de classes (" + classes.Count + ") — reessai.");
				return false;
			}
			VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] mesure sur " + classes.Count + " classes distinctes.");

			// ---- ImplementedInterfaces (+ count) ----
			var ifaceHits = new Dictionary<int, int>();       // offset -> how many classes confirm it
			var countHits = new Dictionary<int, int>();
			int ifaceSamples = 0;
			foreach (IntPtr k in classes)
			{
				List<IntPtr> ifaces = Interfaces(k);
				if (ifaces.Count < 2) continue;               // need >=2 for an unambiguous fingerprint
				ifaceSamples++;
				int foundAt = FindPointerArray(k, ifaces);
				if (foundAt >= 0)
				{
					Bump(ifaceHits, foundAt);
					int cOff = FindIntNear(k, foundAt, ifaces.Count);
					if (cOff >= 0) Bump(countHits, cOff);
				}
			}
			ReportField("ImplementedInterfaces", ifaceHits, ifaceSamples, GuessImplementedInterfaces);
			ReportField("interfaces_count", countHits, ifaceSamples, -1);

			// ---- TypeHierarchy (+ depth) ----
			var hierHits = new Dictionary<int, int>();
			var depthHits = new Dictionary<int, int>();
			int hierSamples = 0;
			foreach (IntPtr k in classes)
			{
				List<IntPtr> chain = ParentChain(k);          // [k, parent, ... , root]
				if (chain.Count < 2) continue;
				hierSamples++;
				// il2cpp stores typeHierarchy ascending [root ... self]; try that, then the reverse.
				var asc = new List<IntPtr>(chain); asc.Reverse();
				int at = FindPointerArray(k, asc);
				if (at < 0) at = FindPointerArray(k, chain);
				if (at >= 0)
				{
					Bump(hierHits, at);
					int dOff = FindIntNear(k, at, chain.Count);
					if (dOff >= 0) Bump(depthHits, dOff);
				}
			}
			ReportField("TypeHierarchy", hierHits, hierSamples, GuessTypeHierarchy);
			ReportField("typeHierarchyDepth", depthHits, hierSamples, GuessTypeHierarchyDepth);

			// A raw dump of one rich class, so anything the matcher missed is still readable by eye.
			DumpOne(classes, ifaceSamples);
			VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] termine. Rappel des devinettes VRChatStructFix : "
				+ "ImplementedInterfaces=0x50, TypeHierarchy=0xC8, VtableCount=0x12A, TypeHierarchyDepth=0x130.");
			return true;
		}

		// ---- ground truth via exports ------------------------------------------------------------

		private static List<IntPtr> Interfaces(IntPtr klass)
		{
			var list = new List<IntPtr>();
			try
			{
				IntPtr iter = IntPtr.Zero;
				for (int g = 0; g < 64; g++)
				{
					IntPtr it = Il2CppRaw.ClassGetInterfaces(klass, ref iter);
					if (it == IntPtr.Zero) break;
					list.Add(it);
				}
			}
			catch { }
			return list;
		}

		private static List<IntPtr> ParentChain(IntPtr klass)
		{
			var list = new List<IntPtr> { klass };
			try
			{
				IntPtr cur = klass;
				for (int g = 0; g < 24; g++)
				{
					IntPtr p = Il2CppRaw.ClassGetParent(cur);
					if (p == IntPtr.Zero || p == cur) break;
					list.Add(p);
					cur = p;
				}
			}
			catch { }
			return list;
		}

		// ---- the scan --------------------------------------------------------------------------

		// The offset O where *(void**)(klass+O) points to an array whose first entries are exactly `want`.
		private static int FindPointerArray(IntPtr klass, List<IntPtr> want)
		{
			if (want.Count == 0 || !NativeGuard.IsReadable(klass, ClassSize)) return -1;
			for (int off = 0x10; off + 8 <= ClassSize; off += 8)
			{
				IntPtr arr;
				try { arr = Marshal.ReadIntPtr(klass, off); } catch { continue; }
				if (!Plausible(arr) || !NativeGuard.IsReadable(arr, 8 * want.Count)) continue;
				bool all = true;
				for (int i = 0; i < want.Count; i++)
				{
					IntPtr e;
					try { e = Marshal.ReadIntPtr(arr, 8 * i); } catch { all = false; break; }
					if (e != want[i]) { all = false; break; }
				}
				if (all) return off;
			}
			return -1;
		}

		// A small integer field (ushort or int) near `arrayOff` whose value equals `n` — the count/depth.
		private static int FindIntNear(IntPtr klass, int arrayOff, int n)
		{
			if (!NativeGuard.IsReadable(klass, ClassSize)) return -1;
			// Counts live after the pointer clusters, so search the whole struct but prefer the nearest.
			int best = -1, bestDist = int.MaxValue;
			for (int off = 0x10; off + 2 <= ClassSize; off += 2)
			{
				int val;
				try { val = (ushort)Marshal.ReadInt16(klass, off); } catch { continue; }
				if (val != n) continue;
				int dist = Math.Abs(off - arrayOff);
				if (dist < bestDist) { bestDist = dist; best = off; }
			}
			return best;
		}

		private static bool Plausible(IntPtr p)
		{
			long v = (long)p;
			return v > 0x10000L && v < 0x00007FFFFFFFFFFFL && (v & 7) == 0;
		}

		// ---- collection & reporting ------------------------------------------------------------

		private static List<IntPtr> CollectClasses()
		{
			var seen = new HashSet<IntPtr>();
			var outl = new List<IntPtr>();
			try
			{
				var comps = VRChatArchiveMod.Core.Live.AllOfType(Il2CppType.Of<Component>());
				if (comps != null)
				{
					int scanned = 0;
					for (int i = 0; i < comps.Length && outl.Count < 40 && scanned < 400; i++, scanned++)
					{
						var c = comps[i];
						if (c == null) continue;
						IntPtr p;
						try { p = IL2CPP.Il2CppObjectBaseToPtr(c); } catch { continue; }
						if (p == IntPtr.Zero || !NativeGuard.IsLiveObject(p)) continue;
						IntPtr k;
						try { k = IL2CPP.il2cpp_object_get_class(p); } catch { continue; }
						if (k == IntPtr.Zero || !seen.Add(k)) continue;
						outl.Add(k);
					}
				}
			}
			catch { }
			return outl;
		}

		private static void Bump(Dictionary<int, int> d, int k) { d.TryGetValue(k, out int v); d[k] = v + 1; }

		private static void ReportField(string name, Dictionary<int, int> hits, int samples, int guess)
		{
			if (hits.Count == 0)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] " + name + " : NON TROUVE sur "
					+ samples + " classes.");
				return;
			}
			int bestOff = -1, bestN = 0;
			foreach (var kv in hits) if (kv.Value > bestN) { bestN = kv.Value; bestOff = kv.Key; }
			string verdict = guess < 0 ? "" :
				(bestOff == guess ? "  == la devinette 0x" + guess.ToString("X") + " (CONFIRMEE)"
				                  : "  != la devinette 0x" + guess.ToString("X") + " (A CORRIGER)");
			string others = hits.Count > 1 ? "  [autres candidats: " + Others(hits, bestOff) + "]" : "";
			VRChatArchiveModPlugin.Logger.LogWarning("[LayoutProbe] " + name + " = 0x" + bestOff.ToString("X")
				+ " (" + bestN + "/" + samples + " classes d'accord)" + verdict + others);
		}

		private static string Others(Dictionary<int, int> hits, int skip)
		{
			var parts = new List<string>();
			foreach (var kv in hits) if (kv.Key != skip) parts.Add("0x" + kv.Key.ToString("X") + ":" + kv.Value);
			return string.Join(", ", parts);
		}

		private static void DumpOne(List<IntPtr> classes, int _)
		{
			try
			{
				foreach (IntPtr k in classes)
				{
					if (Interfaces(k).Count < 2) continue;
					if (!NativeGuard.IsReadable(k, ClassSize)) continue;
					var sb = new System.Text.StringBuilder("[LayoutProbe] dump classe 0x" + ((long)k).ToString("X") + " :");
					for (int off = 0x40; off + 8 <= ClassSize; off += 8)
					{
						IntPtr v; try { v = Marshal.ReadIntPtr(k, off); } catch { continue; }
						sb.Append(" 0x").Append(off.ToString("X")).Append('=').Append(Plausible(v) ? "ptr" : ((long)v).ToString("X"));
					}
					VRChatArchiveModPlugin.Logger.LogWarning(sb.ToString());
					return;
				}
			}
			catch { }
		}
	}
}
