using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
    // BOX DROP - the reverse of GoGoLoco.
    //
    // GoGoLoco pins your networked CAPSULE (the box others click, the ESP anchor) on the ground with a
    // VRCStation and floats your AVATAR up with IK: box stays, avatar rises. This does the opposite.
    // Your local player is never touched - you play exactly where you are - but the POSITION written
    // into your OUTBOUND pose is offset (down, under the floor). Everyone else's copy of your box drops
    // through the map, so you become unclickable and your ESP box is nowhere near your avatar.
    //
    // THE SEAM. VRChat serialises your pose through the FlatBufferNetworkSerializer, which has a private
    // method taking (Player, Vector3, Vector3, String, Int32) - the raw position/velocity path BEFORE
    // quantisation, the clean place to offset. Confirmed present in libs/interop.
    //
    // WHY A HOOK AND NOT A TRANSFORM WRITE. The Player Rotator writes the rig's ROTATION locally and
    // lets VRChat serialise it - that works only because the desktop camera is rebuilt from mouse-look.
    // POSITION has no such free ride: moving the rig moves YOU. The only way to move the sent box
    // without moving yourself is to intercept the serialise itself.
    //
    // STAGED and FAIL-OPEN. This resolves the seam by SIGNATURE (never the per-build obfuscated names),
    // logs what flows through it the first few sends (so the exact position field is read, not guessed),
    // and offsets the candidate under a full guard. If anything is off the log says so and the original
    // runs untouched - sync is never broken, at worst the box does not move yet.
    public class BoxDropModule : IModule
    {
        public override string Name => "BoxDrop";

        public static bool Active { get; private set; }
        public static string Status = "off";
        // Offset added to the OUTBOUND position, in metres. Y negative = box goes down / under the floor.
        public static Vector3 Offset = new Vector3(0f, -6f, 0f);

        private static bool _hooked;
        private static int _attempts;
        private static int _reconLeft = 8;
        private static long _mutated;
        private static string _seam = "not resolved";

        public static void Toggle() { Set(!Active); }

        public static void Set(bool on)
        {
            if (on == Active) { Sync(); return; }
            Active = on;
            try { if (ModConfig.BoxDropEnabled != null) ModConfig.BoxDropEnabled.Value = on; } catch { }
            if (on) _reconLeft = 8;
            Sync();
            VRChatArchiveModPlugin.Logger.LogInfo("[BoxDrop] " + Status);
            Toast.Show(on ? "Box Drop ON - " + Status : "Box Drop OFF - box back on you");
        }

        public static void SetAxis(char axis, float v)
        {
            if (axis == 'x') Offset.x = v;
            else if (axis == 'y') Offset.y = v;
            else if (axis == 'z') Offset.z = v;
            try
            {
                if (axis == 'x' && ModConfig.BoxDropX != null) ModConfig.BoxDropX.Value = Offset.x;
                if (axis == 'y' && ModConfig.BoxDropY != null) ModConfig.BoxDropY.Value = Offset.y;
                if (axis == 'z' && ModConfig.BoxDropZ != null) ModConfig.BoxDropZ.Value = Offset.z;
            }
            catch { }
            Sync();
        }

        private static void Sync()
        {
            Status = (Active ? "on" : "off")
                + " - offset (" + Offset.x.ToString("0.#") + ", " + Offset.y.ToString("0.#") + ", " + Offset.z.ToString("0.#") + ")"
                + " - seam: " + _seam
                + (_mutated > 0 ? " - " + _mutated + " sends offset" : "");
        }

        public override void OnInitialize()
        {
            try
            {
                Offset = new Vector3(
                    ModConfig.BoxDropX != null ? ModConfig.BoxDropX.Value : 0f,
                    ModConfig.BoxDropY != null ? ModConfig.BoxDropY.Value : -6f,
                    ModConfig.BoxDropZ != null ? ModConfig.BoxDropZ.Value : 0f);
                Active = ModConfig.BoxDropEnabled != null && ModConfig.BoxDropEnabled.Value;
            }
            catch { }
            Sync();
        }

        public override void OnUpdate()
        {
            // In-game hotkeys so this is testable without the client UI: RightShift+G toggles,
            // RightShift+KeypadPlus/Minus nudges the Y offset by a metre (down is more negative).
            try
            {
                if (Input.GetKey(KeyCode.RightShift))
                {
                    if (Input.GetKeyDown(KeyCode.G)) Toggle();
                    if (Input.GetKeyDown(KeyCode.KeypadPlus)) SetAxis('y', Offset.y + 1f);
                    if (Input.GetKeyDown(KeyCode.KeypadMinus)) SetAxis('y', Offset.y - 1f);
                }
            }
            catch { }

            // ZERO COST WHILE OFF (2026-09-13). TryHook() walks every loaded assembly
            // (AppDomain.GetAssemblies + GetMethods) looking for the serialiser: up to 30 full
            // reflection sweeps that used to run whether or not the feature was ever switched on.
            // Nothing here is needed until the box actually has to move.
            //
            // The Harmony prefix itself is deliberately NOT unpatched on OFF: un-patching a method
            // in the pose-serialiser hot path is exactly the class of operation this mod has crashed
            // on before (a hook on a merged/inlined method). SendPrefix opens with `if (!Active)
            // return;`, so once installed it is inert — the cost of an un-taken branch — and the
            // toggle stays honest without touching Harmony at runtime.
            if (!Active) return;
            if (_hooked || _attempts >= 30) return;
            if (Time.frameCount % 60 != 0) return;
            _attempts++;
            TryHook();
        }

        private static void TryHook()
        {
            try
            {
                Type ser = FindType("VRC.Networking.FlatBufferNetworkSerializer") ?? FindType("FlatBufferNetworkSerializer");
                if (ser == null) { _seam = "FlatBufferNetworkSerializer type not found yet"; return; }

                MethodInfo target = null;
                foreach (var m in ser.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 5) continue;
                    if (ps[0].ParameterType.Name.IndexOf("Player", StringComparison.Ordinal) < 0) continue;
                    if (ps[1].ParameterType.Name != "Vector3" || ps[2].ParameterType.Name != "Vector3") continue;
                    if (ps[3].ParameterType.Name != "String") continue;
                    if (ps[4].ParameterType.Name != "Int32") continue;
                    target = m; break;
                }
                if (target == null) { _seam = "no (Player, Vector3, Vector3, String, Int32) method on the serializer"; return; }

                var pre = new HarmonyMethod(typeof(BoxDropModule).GetMethod(nameof(SendPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                VRChatArchiveModPlugin.HarmonyInstance.Patch(target, prefix: pre);
                _hooked = true;
                _seam = ser.Name + "." + target.Name + " (Player, Vector3, Vector3, String, Int32)";
                Sync();
                VRChatArchiveModPlugin.Logger.LogInfo("[BoxDrop] hooked " + _seam + ". Recon on: the first sends are logged so the position field is confirmed, not guessed.");
            }
            catch (Exception e) { _seam = "hook threw: " + e.Message; }
        }

        // __1 is the first Vector3 (candidate position), __2 the second (candidate velocity). Recon-first:
        // log both a few times so the real position field is read. Then, while active, add the offset to
        // __1 - the local transform is never touched, only the value about to be sent.
        private static void SendPrefix(Il2CppSystem.Object __0, ref Vector3 __1, ref Vector3 __2, string __3, int __4)
        {
            try
            {
                if (_reconLeft > 0)
                {
                    _reconLeft--;
                    VRChatArchiveModPlugin.Logger.LogInfo(
                        "[BoxDrop][recon] v1=(" + __1.x.ToString("0.##") + "," + __1.y.ToString("0.##") + "," + __1.z.ToString("0.##")
                        + ") v2=(" + __2.x.ToString("0.##") + "," + __2.y.ToString("0.##") + "," + __2.z.ToString("0.##")
                        + ") s='" + (__3 ?? "") + "' i=" + __4
                        + "  - v1 is the field to offset if it tracks your world position.");
                }
                if (!Active) return;
                __1 = new Vector3(__1.x + Offset.x, __1.y + Offset.y, __1.z + Offset.z);
                _mutated++;
                if (_mutated == 1 || _mutated % 200 == 0) Sync();
            }
            catch { }
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(name, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        public override void OnSceneLoaded(int buildIndex)
        {
            if (Active) _reconLeft = 8;
        }
    }
}
