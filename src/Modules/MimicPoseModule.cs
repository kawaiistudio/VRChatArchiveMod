using System;
using System.Collections.Generic;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// MIMIC POSE — wear somebody else's pose.
	//
	// Every humanoid avatar shares one skeleton vocabulary (HumanBodyBones), so a pose can be
	// carried from one avatar to another bone by bone: in LateUpdate, after VRChat has animated and
	// IK-solved both rigs, the target's bone rotations are written onto ours. Position and root
	// rotation are deliberately NOT copied — you keep standing where you stand and facing where you
	// face; only the body does what theirs does. Gesture and locomotion parameters are copied too,
	// so hands, fingers and the leg blend follow.
	//
	// THE NETWORK PART ("IK Photon fix"). VRChat serialises the local avatar's pose from its own IK
	// output; a rig whose VRIK keeps solving overwrites our copy before that serialisation runs, so
	// remote players would still see the ordinary IK pose. VRIK is therefore switched off on the
	// local avatar while mimicking and switched back on when it stops (or when the avatar changes
	// under us and comes back with its own fresh VRIK, which is re-disabled on the next refresh).
	//
	// LOCAL AND HARMLESS: nothing is sent to anyone but the pose you already broadcast; the target
	// is never touched. The pose is carried in MUSCLE SPACE (HumanPoseHandler): Unity's humanoid
	// muscle values are rig-independent, so the copy looks right whatever avatar either of you
	// wears. Copying bone rotations directly only matched when both wore the same rig -- that is
	// the first version, kept as the fallback for a build whose interop refuses HumanPose.
	public class MimicPoseModule : IModule
	{
		public override string Name => "MimicPose";

		public static string TargetUid { get; private set; } = "";
		public static string TargetName { get; private set; } = "";
		public static bool Active => !string.IsNullOrEmpty(TargetUid);
		public static string Status = "";

		// MIRROR: their left is your right. In muscle space that is an index swap between the
		// Left/Right muscle pairs plus a sign flip on the muscles that ARE a left-right choice
		// (spine, chest, neck, head, jaw "Left-Right"). Built once from HumanTrait.MuscleName,
		// Unity's own list, so it follows whatever muscle layout this Unity build has.
		public static bool Mirror { get; private set; }
		public static void SetMirror(bool on)
		{
			Mirror = on;
			Status = Active ? (on ? "mirroring " : "mimicking ") + TargetName : (on ? "mirror on" : "mirror off");
		}
		private static int[] _mirrorIndex;     // muscle i takes its value from muscle _mirrorIndex[i]
		private static float[] _mirrorSign;    // and multiplies it by this
		private static float[] _muscleScratch;

		private static bool BuildMirrorMap(int count)
		{
			try
			{
				var names = HumanTrait.MuscleName;
				if (names == null || names.Length != count) return false;
				var idx = new int[count]; var sign = new float[count];
				var byName = new Dictionary<string, int>(StringComparer.Ordinal);
				for (int i = 0; i < count; i++) byName[names[i] ?? ""] = i;
				for (int i = 0; i < count; i++)
				{
					string n = names[i] ?? "";
					idx[i] = i; sign[i] = 1f;
					string partner = null;
					if (n.StartsWith("Left ", StringComparison.Ordinal)) partner = "Right " + n.Substring(5);
					else if (n.StartsWith("Right ", StringComparison.Ordinal)) partner = "Left " + n.Substring(6);
					else if (n.Contains("Left-Right")) sign[i] = -1f;   // Spine/Chest/UpperChest/Neck/Head/Jaw Left-Right
					if (partner != null && byName.TryGetValue(partner, out int j)) idx[i] = j;
				}
				_mirrorIndex = idx; _mirrorSign = sign; _muscleScratch = new float[count];
				VRChatArchiveModPlugin.Logger.LogInfo("[MimicPose] mirror map built for " + count + " muscle(s).");
				return true;
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MimicPose] mirror map failed (" + Short(e.Message) + "); mirror ignored.");
				return false;
			}
		}

		// Mirrors the muscle array in place: out[i] = in[map[i]] * sign[i].
		private static void MirrorMuscles(HumanPose pose)
		{
			var m = pose.muscles;
			if (m == null) return;
			int n = m.Length;
			if (_mirrorIndex == null || _mirrorIndex.Length != n) { if (!BuildMirrorMap(n)) return; }
			for (int i = 0; i < n; i++) _muscleScratch[i] = m[i];
			for (int i = 0; i < n; i++) m[i] = _muscleScratch[_mirrorIndex[i]] * _mirrorSign[i];
		}

		private static Animator _local, _target;
		private static readonly List<Behaviour> _disabledIk = new List<Behaviour>();
		private static float _nextResolve;
		private static int _copiedFrames;
		private static bool _reenableLogged;

		// Every humanoid bone except the enum's terminator. Hips are included for ROTATION only —
		// the root position stays ours (see the file header).
		private static readonly HumanBodyBones[] Bones = BuildBoneList();

		private static HumanBodyBones[] BuildBoneList()
		{
			var list = new List<HumanBodyBones>();
			foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
				if (b != HumanBodyBones.LastBone) list.Add(b);
			return list.ToArray();
		}

		// VRChat's own animator parameters worth mirroring: hands and fingers, and the locomotion
		// blend that keeps the legs of a remote copy consistent with the pose.
		private static readonly string[] IntParams = { "GestureLeft", "GestureRight", "TrackingType", "VRMode" };
		private static readonly string[] FloatParams = { "GestureLeftWeight", "GestureRightWeight", "VelocityX", "VelocityY", "VelocityZ", "Upright", "AngularY" };
		private static readonly string[] BoolParams = { "Grounded", "Seated", "AFK", "InStation" };

		// Parameter presence, cached per animator instance: reading animator.parameters allocates
		// an array of proxies, which is not something to do 60 times a second.
		private static int _paramsForLocal = -1, _paramsForTarget = -1;
		private static HashSet<string> _localParams = new HashSet<string>(), _targetParams = new HashSet<string>();

		public static void Toggle(VaTagsModule.PlayerEntry e)
		{
			if (e == null) { Status = "no such player"; return; }
			if (Active && string.Equals(e.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) { Stop("stopped"); return; }
			Start(e);
		}

		public static void Start(VaTagsModule.PlayerEntry e)
		{
			if (e == null || e.IsLocal) { Status = "pick somebody else"; return; }
			if (Active) RestoreIk();
			TargetUid = e.UserId ?? "";
			TargetName = e.Name ?? "";
			_local = _target = null;
			_nextResolve = 0f;
			_copiedFrames = 0;
			ResetProbe();
			Status = "mimicking " + TargetName;
			VRChatArchiveModPlugin.Logger.LogInfo("[MimicPose] mimicking " + TargetName + " (" + TargetUid + ")");
		}

		public static void Stop(string why)
		{
			if (!Active) return;
			RestoreIk();
			DropHandlers();
			VRChatArchiveModPlugin.Logger.LogInfo("[MimicPose] " + why + " (" + _copiedFrames + " frame(s) copied from " + TargetName + ", muscles=" + !_humanPoseFailed + ")");
			TargetUid = ""; TargetName = "";
			_local = _target = null;
			Status = why;
		}

		public override void OnSceneLoaded(int buildIndex) { if (Active) Stop("world changed"); }

		// WHO STILL WRITES AFTER US. The copy is written in LateUpdate; with the Animator and IK
		// paused, nothing should move those bones again before the next frame. So the rotations we
		// wrote are remembered, and at the start of the next frame compared with what is there: any
		// bone that moved was rewritten after the copy, by something still running. Measured over the
		// first 120 frames of each mimic and logged once, so "the hands still don't follow" comes with
		// the list of bones being taken back and by how much.
		private static readonly HumanBodyBones[] ProbeBones =
		{
			HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
			HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexProximal,
			HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, HumanBodyBones.RightIndexProximal,
			HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
		};
		private static readonly Quaternion[] _probeWritten = new Quaternion[ProbeBones.Length];
		private static readonly bool[] _probeHas = new bool[ProbeBones.Length];
		private static readonly float[] _probeSum = new float[ProbeBones.Length];
		private static bool _probeArmed, _probeDone;
		private static int _probeFrames;
		private const int ProbeFrames = 120;

		private static void ResetProbe()
		{
			_probeArmed = false; _probeDone = false; _probeFrames = 0;
			for (int i = 0; i < ProbeBones.Length; i++) { _probeSum[i] = 0f; _probeHas[i] = false; }
		}

		private static void SnapshotProbe()
		{
			if (_probeDone || _local == null) return;
			for (int i = 0; i < ProbeBones.Length; i++)
			{
				Transform t = null;
				try { t = _local.GetBoneTransform(ProbeBones[i]); } catch { }
				_probeHas[i] = t != null;
				if (t != null) _probeWritten[i] = t.localRotation;
			}
			_probeArmed = true;
		}

		public override void OnUpdate()
		{
			if (!Active || !_probeArmed || _probeDone || _local == null) return;
			try
			{
				for (int i = 0; i < ProbeBones.Length; i++)
				{
					if (!_probeHas[i]) continue;
					var t = _local.GetBoneTransform(ProbeBones[i]);
					if (t != null) _probeSum[i] += Quaternion.Angle(_probeWritten[i], t.localRotation);
				}
				_probeArmed = false;
				if (++_probeFrames < ProbeFrames) return;
				_probeDone = true;
				var sb = new System.Text.StringBuilder();
				for (int i = 0; i < ProbeBones.Length; i++)
				{
					if (!_probeHas[i]) continue;
					float avg = _probeSum[i] / _probeFrames;
					if (avg >= 0.5f) sb.Append(ProbeBones[i]).Append(' ').Append(avg.ToString("0.0")).Append("° ");
				}
				VRChatArchiveModPlugin.Logger.LogInfo(sb.Length == 0
					? "[MimicPose] nothing rewrites the copied pose after us (" + _probeFrames + " frames measured) - the pose on your avatar is exactly the copy."
					: "[MimicPose] still rewritten after the copy (average per frame over " + _probeFrames + " frames): " + sb.ToString().Trim());
			}
			catch { _probeDone = true; }
		}

		public override void OnLateUpdate()
		{
			if (!Active) return;
			try
			{
				float now = VaClock.Now;
				if (now >= _nextResolve || _local == null || _target == null)
				{
					_nextResolve = now + 2f;   // avatars change; re-find both rigs on a slow tick
					if (!Resolve()) return;
				}

				// KEEP THEM PAUSED. VRChat switches its own IK back on (calibration, seat, avatar
				// reset); one flipped back on silently undoes the copy. Cheap: a handful of bools.
				for (int k = 0; k < _disabledIk.Count; k++)
				{
					try
					{
						var c = _disabledIk[k];
						if (c != null && c.enabled)
						{
							c.enabled = false;
							if (!_reenableLogged) { _reenableLogged = true; VRChatArchiveModPlugin.Logger.LogInfo("[MimicPose] VRChat switched " + MenuCard.Il2CppNameOf(c) + " back on; paused it again."); }
						}
					}
					catch { }
				}

				// MUSCLE SPACE FIRST. A humanoid pose expressed as Unity muscle values is independent
				// of the rig: HumanPoseHandler reads the target's pose as muscles and writes those
				// muscles onto OUR avatar through its own humanoid description, so a T-posed source
				// on an A-posed avatar still lands right. Copying bone rotations directly (the
				// fallback below) only looks right when both wear the same rig -- which is what the
				// owner noticed on 2026-09-02. Body position/rotation stay ours (we keep our place).
				if (!_humanPoseFailed && CopyHumanPose()) { /* retargeted */ }
				else
				{
					// Bone by bone. GetBoneTransform is Unity's own humanoid map, so a bone missing on
					// either rig (no toes, no jaw) is simply skipped rather than guessed.
					var bones = Bones;
					for (int i = 0; i < bones.Length; i++)
					{
						Transform src, dst;
						// Mirrored: read the OPPOSITE side's bone and flip the rotation across the
						// bone's lateral plane. Approximate, like the whole fallback.
						try { src = _target.GetBoneTransform(Mirror ? Opposite(bones[i]) : bones[i]); dst = _local.GetBoneTransform(bones[i]); }
						catch { continue; }
						if (src == null || dst == null) continue;
						var q = src.localRotation;
						dst.localRotation = Mirror ? new Quaternion(q.x, -q.y, -q.z, q.w) : q;
					}
				}

				CopyParams();
				_copiedFrames++;
				SnapshotProbe();
			}
			catch (Exception e)
			{
				// The target leaving destroys their rig under us; that is the normal end of a mimic.
				Stop("stopped — " + Short(e.Message));
			}
		}

		private static bool Resolve()
		{
			VaTagsModule.PlayerEntry e = null;
			try
			{
				foreach (var p in VaTagsModule.Roster)
					if (p != null && string.Equals(p.UserId, TargetUid, StringComparison.OrdinalIgnoreCase)) { e = p; break; }
			}
			catch { }
			if (e == null || e.Transform == null) { Stop("they left"); return false; }

			Animator target = null, local = null;
			try { target = e.Transform.GetComponentInChildren<Animator>(true); } catch { }
			try
			{
				var lt = PlayerRef.LocalTransform();
				if (lt != null) local = lt.GetComponentInChildren<Animator>(true);
			}
			catch { }
			if (target == null || local == null) { Status = "waiting for both avatars to load"; return false; }
			bool humanT = false, humanL = false;
			try { humanT = target.isHuman; humanL = local.isHuman; } catch { }
			if (!humanT || !humanL) { Status = humanL ? "their avatar is not humanoid" : "your avatar is not humanoid"; return false; }

			if (!ReferenceEquals(local, _local) || (_local != null && local.GetInstanceID() != _local.GetInstanceID()))
			{
				// A new local rig (avatar change): its fresh VRIK must be silenced like the first one.
				RestoreIk();
				_local = local;
				DisableIk(local);
				_paramsForLocal = -1;
			}
			_target = target;
			Status = "mimicking " + TargetName;
			return true;
		}

		// VRIK lives in RootMotion.FinalIK inside VRChat's own assemblies; found by CLASS NAME so a
		// build that moves or renames the assembly still finds it, and disabled through the plain
		// Behaviour.enabled every Unity component has. Remembered so Stop can put back exactly what
		// it switched off.
		// EVERYTHING THAT REWRITES THE RIG AFTER US. VRIK is the obvious one, but VRChat's local avatar
		// also carries FinalIK grounders and leg / limb solvers (GrounderVRIK, GrounderIK, LimbIK, the
		// VRIK root controller) that plant the FEET and bend the KNEES in LateUpdate -- after our copy.
		// With only VRIK paused the upper body followed the target and the legs stayed our own, which is
		// what "just the top half" looked like. Matched by il2cpp class name; each one is remembered and
		// put back by RestoreIk. VRChat's own controllers (VRCVrIkController, tracking) are NOT touched:
		// they feed the head and hands and the network, and the pose we write must still be sent.
		private static bool IsLegOrBodyIk(string n)
		{
			switch (n)
			{
				case "VRIK": case "VRIK_Fix": case "IKSolverVR":
				case "GrounderVRIK": case "GrounderIK": case "GrounderFBBIK": case "GrounderBipedIK":
				case "VRIKRootController": case "VRIKLODController":
				case "LimbIK": case "LegIK": case "FullBodyBipedIK": case "BipedIK":
				// PoseLocalUpdate re-applies the avatar's pose every LateUpdate AFTER our copy, so the
				// bones we set (and the ESP skeleton reads) diverge from what actually renders and is
				// sent. Pausing it makes our copied pose the final one -- a true 1:1, locally and on the
				// network. Restored on Stop like the rest.
				case "PoseLocalUpdate":
					return true;
				default:
					return false;
			}
		}

		// THE RIG, ONCE, IN THE LOG. Every IK / tracking / sync-looking component on the local avatar and
		// its player root, so the next "the legs still do not follow" report names the component that
		// rewrote them instead of guessing. Printed on the first pause of each rig, never per frame.
		private static void LogRig(Transform root)
		{
			try
			{
				Transform top = root;
				for (int g = 0; top.parent != null && g < 8; g++) top = top.parent;
				var comps = top.GetComponentsInChildren<Behaviour>(true);
				if (comps == null) return;
				var seen = new HashSet<string>(StringComparer.Ordinal);
				var sb = new System.Text.StringBuilder();
				for (int i = 0; i < comps.Length; i++)
				{
					var c = comps[i];
					if (c == null) continue;
					string n;
					try { n = MenuCard.Il2CppNameOf(c); } catch { continue; }
					if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
					if (n.IndexOf("IK", StringComparison.Ordinal) < 0 && n.IndexOf("Ik", StringComparison.Ordinal) < 0
					 && n.IndexOf("Grounder", StringComparison.Ordinal) < 0 && n.IndexOf("Tracking", StringComparison.Ordinal) < 0
					 && n.IndexOf("Pose", StringComparison.Ordinal) < 0 && n.IndexOf("Sync", StringComparison.Ordinal) < 0
					 && n.IndexOf("Calibrat", StringComparison.Ordinal) < 0) continue;
					bool on = false; try { on = c.enabled; } catch { }
					sb.Append(n).Append(on ? "(on) " : "(off) ");
				}
				VRChatArchiveModPlugin.Logger.LogInfo("[MimicPose] rig components: " + sb);
			}
			catch { }
		}

		private static void DisableIk(Animator local)
		{
			try
			{
				// THE PLAYER ROOT, NOT THE AVATAR. This used to search local.transform (the avatar) only,
				// while LogRig searched from the player root. PoseLocalUpdate lives on the player object,
				// above the avatar, so the log listed it as "(on)" and the pause never reached it: every
				// frame it re-applied the avatar's own pose over the copy, and 2026-09-23 showed
				// "local IK paused (1 component(s))" with PoseLocalUpdate still running. Same root as
				// LogRig now, so what the log names is exactly what gets paused.
				var root = local.transform;
				Transform top = root;
				try { for (int g = 0; top.parent != null && g < 8; g++) top = top.parent; } catch { top = root; }
				var comps = top.GetComponentsInChildren<Behaviour>(true);
				if (comps == null) return;
				LogRig(root);
				for (int i = 0; i < comps.Length; i++)
				{
					var c = comps[i];
					if (c == null) continue;
					string n;
					try { n = MenuCard.Il2CppNameOf(c); } catch { continue; }
					if (!IsLegOrBodyIk(n)) continue;
					try { if (c.enabled) { c.enabled = false; _disabledIk.Add(c); } } catch { }
				}
				// AND OUR OWN ANIMATOR. With IK paused the copy was still only "trying" (owner,
				// 2026-09-23: legs, arms and hands half-followed): the local Animator keeps playing our
				// locomotion, idle and hand-gesture layers every frame, and the copy is fighting it for
				// the same bones - fingers most of all, since a gesture layer owns them outright.
				// Paused, the copied pose is the ONLY thing that moves the avatar, down to the fingers.
				// Put back with the rest on stop.
				try { if (local.enabled) { local.enabled = false; _disabledIk.Add(local); } } catch { }
				_reenableLogged = false;
				if (_disabledIk.Count > 0)
				{
					var names = new System.Text.StringBuilder();
					foreach (var c in _disabledIk) { try { names.Append(MenuCard.Il2CppNameOf(c)).Append(' '); } catch { } }
					VRChatArchiveModPlugin.Logger.LogInfo("[MimicPose] local IK paused (" + _disabledIk.Count + " component(s): " + names.ToString().Trim() + ") so the copied pose is what gets sent.");
				}
			}
			catch { }
		}

		private static void RestoreIk()
		{
			for (int i = 0; i < _disabledIk.Count; i++)
			{
				try { var c = _disabledIk[i]; if (c != null) c.enabled = true; } catch { }
			}
			_disabledIk.Clear();
		}

		private static void CopyParams()
		{
			try
			{
				if (_paramsForLocal != _local.GetInstanceID()) { _localParams = ParamNames(_local); _paramsForLocal = _local.GetInstanceID(); }
				if (_paramsForTarget != _target.GetInstanceID()) { _targetParams = ParamNames(_target); _paramsForTarget = _target.GetInstanceID(); }

				for (int i = 0; i < IntParams.Length; i++)
				{
					string p = IntParams[i];
					if (_localParams.Contains(p) && _targetParams.Contains(p)) _local.SetInteger(p, _target.GetInteger(p));
				}
				for (int i = 0; i < FloatParams.Length; i++)
				{
					string p = FloatParams[i];
					if (_localParams.Contains(p) && _targetParams.Contains(p)) _local.SetFloat(p, _target.GetFloat(p));
				}
				for (int i = 0; i < BoolParams.Length; i++)
				{
					string p = BoolParams[i];
					if (_localParams.Contains(p) && _targetParams.Contains(p)) _local.SetBool(p, _target.GetBool(p));
				}
			}
			catch { }
		}

		private static HashSet<string> ParamNames(Animator a)
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				var ps = a.parameters;
				if (ps == null) return set;
				for (int i = 0; i < ps.Length; i++)
				{
					var p = ps[i];
					if (p == null) continue;
					string n = null;
					try { n = p.name; } catch { }
					if (!string.IsNullOrEmpty(n)) set.Add(n);
				}
			}
			catch { }
			return set;
		}

		// One HumanPoseHandler per rig, rebuilt when the animator instance changes (avatar swap),
		// disposed with it. Building one per frame would allocate a humanoid solver 60 times a second.
		private static HumanPoseHandler _hLocal, _hTarget;
		private static int _hLocalFor = -1, _hTargetFor = -1;
		private static bool _humanPoseFailed;

		private static bool CopyHumanPose()
		{
			try
			{
				int lid = _local.GetInstanceID(), tid = _target.GetInstanceID();
				if (_hLocal == null || _hLocalFor != lid)
				{
					try { _hLocal?.Dispose(); } catch { }
					_hLocal = new HumanPoseHandler(_local.avatar, _local.transform); _hLocalFor = lid;
				}
				if (_hTarget == null || _hTargetFor != tid)
				{
					try { _hTarget?.Dispose(); } catch { }
					_hTarget = new HumanPoseHandler(_target.avatar, _target.transform); _hTargetFor = tid;
				}

				HumanPose theirs = new HumanPose();
				_hTarget.GetHumanPose(ref theirs);
				HumanPose mine = new HumanPose();
				_hLocal.GetHumanPose(ref mine);
				// Their muscles, our body: the pose without the place.
				// FULL BODY: their hips too. bodyPosition / bodyRotation are the hips relative to the avatar
				// ROOT (normalised by human scale), so copying them keeps us standing where we stand and
				// facing where our root faces while crouching, leaning, sitting and jumping follow the
				// target. With ours kept instead, bent legs hung from standing-height hips -- the floating
				// lower half the owner saw. Only a broken (NaN) source falls back to our own hips.
				if (float.IsNaN(theirs.bodyPosition.x) || float.IsInfinity(theirs.bodyPosition.x)) { theirs.bodyPosition = mine.bodyPosition; theirs.bodyRotation = mine.bodyRotation; }
				if (Mirror) MirrorMuscles(theirs);
				_hLocal.SetHumanPose(ref theirs);
				return true;
			}
			catch (Exception e)
			{
				// Once: the interop shape of HumanPose can differ between builds. The bone copy takes
				// over for the rest of the session, and the log says why.
				_humanPoseFailed = true;
				VRChatArchiveModPlugin.Logger.LogWarning("[MimicPose] HumanPoseHandler path failed (" + Short(e.Message) + "); falling back to bone rotations.");
				return false;
			}
		}

		private static void DropHandlers()
		{
			try { _hLocal?.Dispose(); } catch { }
			try { _hTarget?.Dispose(); } catch { }
			_hLocal = null; _hTarget = null; _hLocalFor = _hTargetFor = -1;
		}

		// Left<->Right partner of a humanoid bone, by enum name; bones with no side map to themselves.
		private static readonly Dictionary<HumanBodyBones, HumanBodyBones> _opposite = new Dictionary<HumanBodyBones, HumanBodyBones>();
		private static HumanBodyBones Opposite(HumanBodyBones b)
		{
			if (_opposite.Count == 0)
			{
				foreach (HumanBodyBones x in Enum.GetValues(typeof(HumanBodyBones)))
				{
					string n = x.ToString();
					string p = n.StartsWith("Left") ? "Right" + n.Substring(4) : n.StartsWith("Right") ? "Left" + n.Substring(5) : null;
					_opposite[x] = p != null && Enum.TryParse(p, out HumanBodyBones o) ? o : x;
				}
			}
			return _opposite.TryGetValue(b, out var r) ? r : b;
		}

		private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 80 ? s.Substring(0, 79) + "…" : s);
	}
}
