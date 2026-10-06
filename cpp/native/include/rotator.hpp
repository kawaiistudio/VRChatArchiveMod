#pragma once
#include "module.hpp"

namespace VRCA {

    // PLAYER ROTATOR -- tilt your OWN body past upright, and take the view with it.
    //
    // Two different levers, and that is the whole point:
    //   BODY -> the rig transform, rewritten every frame so VRChat's own write cannot undo it.
    //   VIEW -> the mouse-look pitch CLAMP. Widen it and the mouse itself carries you over.
    //
    // THREE THINGS THAT ARE NOT OPTIONAL, each one learned in the C# module:
    //   * The tilt is a QUATERNION, never two euler angles: past vertical, pitch and roll collapse
    //     into each other and the body starts spinning on its own.
    //   * It pivots about a BONE, not about the transform origin. Writing rotation turns the rig
    //     about the player's feet, so the body swings like a hinged plank and the viewpoint is
    //     carried off sideways instead of staying where the head is.
    //   * GRAVITY IS HELD OFF WHILE TILTED, and only while tilted. VRChat pulls the player down and
    //     stands them back up every frame, so a tilt that is not held against gravity is fought to
    //     a standstill -- which reads as "nothing happens". Upright, standing up is what you want,
    //     so arming the rotator must not silently switch gravity off.
    //
    // Gravity is taken through VRCPlayerApi.SetGravityStrength, never through global Physics
    // gravity: a second writer on the world's value is how one feature records another's zero as
    // "the original" and leaves the world broken for the session.
    class RotatorModule : public Module {
    public:
        // Unity's convention exactly: (x, y, z, w), and a * b meaning "b first, in a's frame".
        struct Quat { float x = 0, y = 0, z = 0, w = 1; };

        RotatorModule();

        void OnUpdate()  override;
        void OnEnable()  override;
        void OnDisable() override;

        // Straight to upside down and straight back -- what a button is actually for.
        void Flip();
        // Back to level WITHOUT disarming, so the keys stay live.
        void ResetUpright();

        float m_turnSpeed  = 90.f;   // degrees per second on the arrow keys
        bool  m_holdGravity = true;

    private:
        void* Rig();
        bool  Pivot(void* rig, float out3[3]);
        void  HoldGravity(bool on);
        void  RestoreBody();

        Quat   m_tilt;                   // away from upright; yaw stays VRChat's
        Quat   m_applied;                // what we last wrote, to tell our rotation from VRChat's
        float  m_yaw = 0.f;              // VRChat's heading, tracked so the tilt composes on top
        bool   m_restorePending = false;
        bool   m_holdProbed = false;
        bool   m_gravityHeld = false;
        float  m_gravityOriginal = 1.f;
        double m_lastTick = 0.0;
    };
}
