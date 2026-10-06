using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// PLAYER STATE PROBE — a discovery pass, not a feature. It dumps, once per remote player (and
	// again when their avatar changes), everything that could carry the states we still cannot read:
	// mic mute, tracking type, immobilize, not-loaded / not-responding, ping/stability. The point is
	// to read the REAL field and parameter names off THIS VRChat build so the actual indicators can be
	// written from data instead of guessed — the same probe-first path BlockedByProbe and PushProbe
	// took. Read-only, throttled to one player per tick so reflecting a pile of components never
	// spikes a frame.
	//
	// What it logs per player:
	//   * every component type on the player root  — reveals USpeaker / tracking / network components
	//   * every ANIMATOR parameter with its value  — reveals every state VRChat drives (AFK, Seated,
	//     InStation, VRMode, and whatever else is there: gesture, grounded, upright, immobilize…)
	//   * the primitive fields/props of any component whose type name looks voice / tracking / network
	//     related — where mic mute, tracking type and ping most likely live
	// Send me a few of these [StateProbe] blocks and I turn them into the real columns.
	public class PlayerStateProbeModule : IModule
	{
		public override string Name => "StateProbe";

		private const float TickInterval = 0.5f;     // one player per tick
		private float _nextTick;
		private int _cursor;

		// player transform id -> avatar animator id we last dumped, so we re-dump on an avatar change.
		private readonly Dictionary<int, int> _dumped = new Dictionary<int, int>();

		private static readonly string[] InterestingType =
		{
			"speaker", "voice", "audio", "tracking", "ik", "vrik", "net", "ping", "player",
			"immobil", "station", "locomotion", "descriptor", "avatar", "pipeline", "usharp",
		};

		public override void OnSceneLoaded(int buildIndex) { _dumped.Clear(); _cursor = 0; }

		public override void OnUpdate()
		{
			try
			{
				// DEBUG-ONLY (2026-09-13). This is dev scaffolding — it dumps every player's
				// components and animator parameters so I can turn them into real columns — and it
				// was running for everyone, floodng the log (dozens of lines per player) and walking
				// every player's component list on a timer. Gated behind the same Debug flag the
				// other diagnostics use, so it stays available when I need it and costs nothing (not
				// even the timer read) for a normal user.
				if (!DiagnosticsModule.Debug) return;

				if (VaClock.Now < _nextTick) return;
				_nextTick = VaClock.Now + TickInterval;

				var roster = VaTagsModule.Roster;
				if (roster == null) return;
				int n;
				try { n = roster.Count; } catch { return; }
				if (n == 0) return;

				// Round-robin one player per tick.
				_cursor = (_cursor + 1) % n;
				VaTagsModule.PlayerEntry e;
				try { e = roster[_cursor]; } catch { return; }
				if (e == null || e.IsLocal) return;

				var t = e.Transform;
				if (t == null || !NativeGuard.Alive(t)) return;
				int tid;
				try { tid = t.GetInstanceID(); } catch { return; }

				Animator anim = null;
				try { anim = t.GetComponentInChildren<Animator>(true); } catch { }
				int animId = 0;
				try { if (anim != null && NativeGuard.Alive(anim)) animId = anim.GetInstanceID(); } catch { }

				int last;
				if (_dumped.TryGetValue(tid, out last) && last == animId) return;   // already dumped this avatar
				_dumped[tid] = animId;

				Dump(e, t, anim);
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogError("[StateProbe] update threw: " + ex);
			}
		}

		private static void Dump(VaTagsModule.PlayerEntry e, Transform t, Animator anim)
		{
			string who = "?";
			try { who = e.Name ?? "?"; } catch { }
			VRChatArchiveModPlugin.Logger.LogInfo("[StateProbe] ===== " + who + " =====");

			// 1) Components on the player ROOT — the discovery goldmine.
			try
			{
				var comps = t.GetComponents<Component>();
				if (comps != null)
				{
					var sb = new StringBuilder();
					int cap = 0;
					for (int i = 0; i < comps.Length && cap < 40; i++)
					{
						try { if (comps[i] != null) { sb.Append(comps[i].GetType().Name); sb.Append(", "); cap++; } }
						catch { }
					}
					VRChatArchiveModPlugin.Logger.LogInfo("[StateProbe]   root components: " + sb);
				}
			}
			catch { }

			// 2) EVERY animator parameter with its value — the full state surface VRChat drives.
			try
			{
				if (anim != null && NativeGuard.Alive(anim))
				{
					var ps = anim.parameters;
					if (ps != null)
					{
						var sb = new StringBuilder();
						for (int i = 0; i < ps.Length; i++)
						{
							try
							{
								var pr = ps[i];
								if (pr == null) continue;
								string nm = pr.name ?? "";
								string val;
								switch (pr.type)
								{
									case AnimatorControllerParameterType.Bool: val = anim.GetBool(pr.nameHash).ToString(); break;
									case AnimatorControllerParameterType.Int: val = anim.GetInteger(pr.nameHash).ToString(); break;
									case AnimatorControllerParameterType.Float: val = anim.GetFloat(pr.nameHash).ToString("0.##"); break;
									default: val = "(trigger)"; break;
								}
								sb.Append(nm).Append('=').Append(val).Append("  ");
							}
							catch { }
						}
						VRChatArchiveModPlugin.Logger.LogInfo("[StateProbe]   anim params: " + sb);
					}
				}
				else VRChatArchiveModPlugin.Logger.LogInfo("[StateProbe]   anim: (no animator / avatar not loaded)");
			}
			catch { }

			// 3) Interesting components anywhere under the player, with their primitive fields/props —
			//    where mic mute, tracking type, ping most likely live. One line per component, capped.
			try
			{
				var all = t.GetComponentsInChildren<Component>(true);
				if (all != null)
				{
					int logged = 0;
					var seenType = new HashSet<string>();
					for (int i = 0; i < all.Length && logged < 16; i++)
					{
						Component comp;
						try { comp = all[i]; } catch { continue; }
						if (comp == null) continue;
						string tn;
						try { tn = comp.GetType().Name; } catch { continue; }
						string low = tn.ToLowerInvariant();
						bool interesting = false;
						for (int k = 0; k < InterestingType.Length; k++)
							if (low.Contains(InterestingType[k])) { interesting = true; break; }
						if (!interesting) continue;
						if (!seenType.Add(tn)) continue;    // one instance per type is enough for discovery

						string dump = ReflectPrimitives(comp);
						if (dump.Length > 0)
						{
							VRChatArchiveModPlugin.Logger.LogInfo("[StateProbe]   " + tn + ": " + dump);
							logged++;
						}
					}
				}
			}
			catch { }
		}

		// Read only VALUE-TYPE fields/props (bool/int/float/enum/byte) — reference types (strings,
		// objects) can hand an il2cpp getter a bad pointer and take the process down, and their values
		// are not what we are after here anyway. Every read guarded, output capped.
		private static string ReflectPrimitives(object comp)
		{
			var sb = new StringBuilder();
			try
			{
				Type ty = comp.GetType();

				PropertyInfo[] props = null;
				try { props = ty.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
				catch { props = null; }
				if (props != null)
				{
					for (int i = 0; i < props.Length && sb.Length < 400; i++)
					{
						var p = props[i];
						try
						{
							if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
							if (!IsPrimitiveish(p.PropertyType)) continue;
							object v = p.GetValue(comp, null);
							sb.Append(p.Name).Append('=').Append(v).Append("  ");
						}
						catch { }
					}
				}

				FieldInfo[] fields = null;
				try { fields = ty.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
				catch { fields = null; }
				if (fields != null)
				{
					for (int i = 0; i < fields.Length && sb.Length < 800; i++)
					{
						var f = fields[i];
						try
						{
							if (!IsPrimitiveish(f.FieldType)) continue;
							object v = f.GetValue(comp);
							sb.Append(f.Name).Append('=').Append(v).Append("  ");
						}
						catch { }
					}
				}
			}
			catch { }
			return sb.ToString();
		}

		private static bool IsPrimitiveish(Type ty)
		{
			if (ty == null) return false;
			if (ty.IsEnum) return true;
			return ty == typeof(bool) || ty == typeof(int) || ty == typeof(uint)
				|| ty == typeof(float) || ty == typeof(double) || ty == typeof(byte)
				|| ty == typeof(short) || ty == typeof(long);
		}
	}
}
