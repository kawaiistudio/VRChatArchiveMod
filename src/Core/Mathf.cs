using System;

namespace VRChatArchiveMod
{
	// ARITHMETIC DOES NOT BELONG ON THE OTHER SIDE OF IL2CPP.
	//
	// The mod made 397 calls to UnityEngine.Mathf, and every one of them was a managed-to-il2cpp
	// crossing to do something .NET does natively -- a max of two floats, an absolute value. That was
	// always pure overhead on paths that run per frame and per player. On the Unity 6 build it became
	// fatal as well: il2cpp INLINES these helpers, so Mathf.Max, Min, Abs and Clamp have no compiled
	// body left to bind to. Il2CppInterop stored its missing-member stub in their place, and invoking
	// that stub on this build does not raise a catchable exception -- it takes the process down with
	// an access violation. The diagnostics module asking for a maximum was enough to kill the game.
	//
	// Nothing here needs the engine. This type sits in the mod's own root namespace, and C# resolves
	// an unqualified name through the enclosing namespaces before it consults a `using`, so every
	// existing `Mathf.` call site in VRChatArchiveMod.* binds here instead -- without a single one of
	// them being edited, and without disturbing anything outside the mod.
	//
	// The semantics are Unity's, not .NET's, wherever the two differ: Repeat, PingPong, DeltaAngle and
	// InverseLerp follow UnityEngine.Mathf exactly, Lerp clamps its interpolant, and RoundToInt keeps
	// the to-even rounding Math.Round performs by default.
	internal static class Mathf
	{
		internal const float PI = 3.14159274f;
		internal const float Deg2Rad = 0.0174532924f;
		internal const float Rad2Deg = 57.29578f;
		internal const float Epsilon = float.Epsilon;

		internal static float Abs(float f) => Math.Abs(f);
		internal static int Abs(int v) => Math.Abs(v);

		internal static float Max(float a, float b) => a > b ? a : b;
		internal static int Max(int a, int b) => a > b ? a : b;
		internal static float Max(params float[] values)
		{
			if (values == null || values.Length == 0) return 0f;
			float m = values[0];
			for (int i = 1; i < values.Length; i++) if (values[i] > m) m = values[i];
			return m;
		}

		internal static float Min(float a, float b) => a < b ? a : b;
		internal static int Min(int a, int b) => a < b ? a : b;
		internal static float Min(params float[] values)
		{
			if (values == null || values.Length == 0) return 0f;
			float m = values[0];
			for (int i = 1; i < values.Length; i++) if (values[i] < m) m = values[i];
			return m;
		}

		internal static float Clamp(float value, float min, float max) => value < min ? min : (value > max ? max : value);
		internal static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
		internal static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

		internal static float Floor(float f) => (float)Math.Floor(f);
		internal static int FloorToInt(float f) => (int)Math.Floor(f);
		internal static int CeilToInt(float f) => (int)Math.Ceiling(f);
		internal static float Round(float f) => (float)Math.Round(f);
		internal static int RoundToInt(float f) => (int)Math.Round(f);

		internal static float Sqrt(float f) => (float)Math.Sqrt(f);
		internal static float Pow(float f, float p) => (float)Math.Pow(f, p);
		internal static float Sin(float f) => (float)Math.Sin(f);
		internal static float Cos(float f) => (float)Math.Cos(f);
		internal static float Atan2(float y, float x) => (float)Math.Atan2(y, x);

		internal static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

		internal static float InverseLerp(float a, float b, float value)
			=> a != b ? Clamp01((value - a) / (b - a)) : 0f;

		// Unity's Repeat never returns `length` itself, and is clamped against float error.
		internal static float Repeat(float t, float length)
			=> Clamp(t - Floor(t / length) * length, 0f, length);

		internal static float PingPong(float t, float length)
		{
			t = Repeat(t, length * 2f);
			return length - Abs(t - length);
		}

		internal static float DeltaAngle(float current, float target)
		{
			float delta = Repeat(target - current, 360f);
			if (delta > 180f) delta -= 360f;
			return delta;
		}

		internal static bool Approximately(float a, float b)
			=> Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
	}
}
