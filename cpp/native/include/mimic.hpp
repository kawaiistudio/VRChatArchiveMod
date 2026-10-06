#pragma once
#include "module.hpp"

#include <string>
#include <vector>

namespace VRCA {

    // MIMIC POSE -- wear somebody else's pose.
    //
    // Every humanoid avatar shares one skeleton vocabulary, so a pose carries from one avatar to
    // another bone by bone: after VRChat has animated and IK-solved both rigs, the target's bone
    // rotations are written onto ours.
    //
    // POSITION AND HEADING ARE DELIBERATELY NOT COPIED. You keep standing where you stand and
    // facing where you face; only the body does what theirs does. Copying the root would teleport
    // you onto them, which is a different feature and a far ruder one.
    //
    // WHY VRIK IS SWITCHED OFF while mimicking: VRChat serialises the local avatar's pose from its
    // own IK output, and a rig whose IK keeps solving overwrites our copy before that
    // serialisation runs -- so remote players would still see the ordinary pose and the feature
    // would look local-only. It is switched back on when mimicking stops, and re-disabled after an
    // avatar change brings a fresh one back.
    //
    // MIRROR flips the pose left-to-right by swapping each bone with its opposite side.
    class MimicModule : public Module {
    public:
        MimicModule();
        // NOTHING HAPPENS IN OnUpdate ANY MORE: a bone written before the Animator evaluates is
        // overwritten by it in the same frame, so the copy never reached the screen nor the
        // network. The pose is applied from the late phase, after Unity has animated.
        void OnLateUpdate() override;
        void OnDisable()   override;

        void Start(std::string const& userId, bool mirror);
        void Stop();

    private:
        void* LocalAnimator();
        void* TargetAnimator();
        void  SetIk(bool on);

        std::string m_target;
        bool        m_mirror = false;
        void*       m_localAnim = nullptr;
        void*       m_targetAnim = nullptr;
        std::vector<void*> m_ikOff;     // the IK components we hold off, to put back
        double      m_nextResolve = 0.0;
        int         m_copied = 0;
        bool        m_reported = false;
        bool        m_ikRelogged = false;
    };
}
