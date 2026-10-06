using System.Diagnostics;

namespace VRChatArchiveMod
{
	// A CLOCK THAT DOES NOT CROSS INTO IL2CPP.
	//
	// UnityEngine.Time's value members are il2cpp properties, and on this VRChat build (Unity 6)
	// TokenShiftFix rebinds the UnityEngine.Time methods; several of those native pointers do not
	// survive that pass intact, so il2cpp_runtime_invoke jumps into one and ends the whole process
	// with an access violation -- which a managed try/catch can never see (an AccessViolationException
	// is a corrupted-state exception the CLR refuses to hand to a catch). That is reached from the
	// first module OnUpdate of the first frame (Time.realtimeSinceStartup, then VaClock.Delta),
	// which is exactly why "the mod loads then the game dies" a second or two in. NativeGuard already
	// moved OFF the il2cpp clock for the same reason; this is the same move for every other caller.
	//
	// Which Time members are poisoned is per-method luck of the token shift: Time.frameCount binds
	// correctly on this build and is left on il2cpp (the pump relies on it), but the SECONDS and DELTA
	// members are routed here instead. A monotonic managed stopwatch needs no il2cpp and cannot be
	// mis-bound; the delta is measured between pump frames (FramePump calls Tick once a frame). Every
	// consumer uses these for relative intervals (now - last >= period) or rate*dt integration, both
	// of which are preserved as long as the whole mod reads one source -- which it now does.
	internal static class VaClock
	{
		private static readonly Stopwatch _sw = Stopwatch.StartNew();

		// Seconds since the mod loaded — a drop-in for VaClock.Now / realtimeSinceStartup / unscaledTime.
		public static float Now => (float)_sw.Elapsed.TotalSeconds;
		public static double NowDouble => _sw.Elapsed.TotalSeconds;

		// VRChat's physics step. The real VaClock.FixedDelta is the default 0.02 s on this build and
		// a constant is exactly what the fixed-step accumulator wants.
		public const float FixedDelta = 0.02f;

		// Seconds since the previous frame — a drop-in for VaClock.Delta and friends. Measured, not
		// invoked: FramePump.Tick() is called once per frame; clamped so a load hitch cannot hand out
		// a multi-second dt that makes integrators jump.
		private static float _delta = 1f / 60f;
		private static float _lastTickNow = -1f;
		public static float Delta => _delta;

		public static void Tick()
		{
			float n = Now;
			if (_lastTickNow >= 0f)
			{
				float d = n - _lastTickNow;
				if (d > 0f && d < 0.5f) _delta = d;   // ignore the first huge gap and any stall
			}
			_lastTickNow = n;
		}
	}
}
