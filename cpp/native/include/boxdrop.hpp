#pragma once
#include "module.hpp"

namespace VRCA {

    // BOX DROP -- the reverse of GoGoLoco.
    //
    // GoGoLoco pins your networked CAPSULE (the box others click, the thing an ESP anchors to) on
    // the ground and floats your AVATAR up with IK: box stays, avatar rises. This does the
    // opposite. Your local player is never touched -- you play exactly where you are -- but the
    // POSITION written into your OUTBOUND pose is offset, so everyone else's copy of your box
    // drops through the map and your nameplate is nowhere near your avatar.
    //
    // THE SEAM. VRChat serialises your pose through the network serializer, which has a method
    // taking (Player, Vector3, Vector3, String, Int32): the raw position/velocity path BEFORE
    // quantisation, which is the clean place to offset.
    //
    // WHY A HOOK AND NOT A TRANSFORM WRITE. The rotator writes the rig's ROTATION locally and lets
    // VRChat serialise it, which works only because the desktop camera is rebuilt from mouse-look.
    // POSITION has no such free ride: moving the rig moves YOU. Intercepting the serialise is the
    // only way to move the sent box without moving yourself.
    //
    // STAGED AND FAIL-OPEN. The seam is found by SIGNATURE, never by a per-build name; the first
    // sends are LOGGED so the real position field is read rather than guessed; and the offset is
    // applied under a guard. If anything is off, the log says so and the original runs untouched:
    // sync is never broken, at worst the box does not move.
    class BoxDropModule : public Module {
    public:
        BoxDropModule();
        void OnUpdate()  override;
        void OnEnable()  override;
        void OnDisable() override;

        static void SetAxis(char axis, float v);

    private:
        int    m_attempts = 0;
        double m_next = 0.0;
    };
}
