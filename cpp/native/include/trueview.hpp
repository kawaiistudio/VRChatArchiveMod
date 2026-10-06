#pragma once
#include "module.hpp"

// TRUEVIEW -- a remote player keeps looking like themselves when VRChat swaps their avatar for the
// fallback robot (ForwardDirection/AvatarProxy).
//
// Port of the C# mod's TrueViewModule (3.9.256+) to the native engine, for build 25686233:
//   * the robot is switched off the moment it appears;
//   * a CULLED avatar (VRChat keeps it, forces its renderers off, stops its Animator and shows the
//     robot) is drawn and animated again;
//   * while the real avatar is healthy, ONE sleeping copy of it is cached under ForwardDirection,
//     and shown when VRChat empties the real one -- but only while the copy's materials are alive
//     (a swap unloads the bundle; a copy with dead shaders draws magenta and is dropped instead);
//   * the shown copy is posed every frame from the player's own IK targets -- the "Dynamic IK Rig"
//     of TrueView Standalone 1.4.2 -- so it walks, turns, reaches and sits with the player;
//   * a replaced player stays clickable (SelectRegion) and audible.
//
// NOT ported: nameplates (no GameObject left on this build -- GPU-instanced) and the re-download of
// the real avatar through VRCAvatarManager (its classes are renamed; to recover separately).
//
// Always on (owner decision 2026-09-24, "actif point final"): enabled at construction, no menu card,
// no hotkey. OnDisable still restores everything it changed, for a clean unload.
namespace VRCA {

    class TrueViewModule : public Module {
    public:
        TrueViewModule();
        void OnUpdate()  override;
        void OnDisable() override;
    };
}
