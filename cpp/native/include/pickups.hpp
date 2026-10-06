#pragma once
#include "module.hpp"

#include <string>
#include <unordered_set>
#include <vector>

namespace VRCA {

    // FORCE PICKUP -- make a world's LOCKED pickups grabbable again, so you pick them up with your
    // own hands the way the world intended.
    //
    // THIS IS NOT FORCE GRAB, AND THE DIFFERENCE IS THE WHOLE POINT. Force Grab drags an object in
    // front of the camera and drives its transform: the object moves, but you are never HOLDING
    // it, so its script never runs its pickup logic -- a gun never fires, a tool never works, a key
    // never opens anything.
    //
    // A pickup is locked in one of three ordinary ways, all plain fields on VRC_Pickup:
    //   pickupable = false     grabbing was switched off outright
    //   proximity  = tiny      you must be unrealistically close before VRChat offers the grab
    //   DisallowTheft = true   it cannot be taken while somebody else holds it
    //
    // So this flips those fields and moves nothing. After that VRChat's own pickup system does the
    // work, with the world's Udon running exactly as it does for anyone else. Every value is
    // REMEMBERED and put back, so a world is never left permanently altered.
    //
    // DisallowTheft is only relaxed on request: taking something out of another player's hands is
    // a different act from unlocking a prop nobody is holding.
    class ForcePickupModule : public Module {
    public:
        ForcePickupModule();
        void OnUpdate()  override;
        void OnDisable() override;

        float m_proximity = 100.f;   // what a normal world uses
        bool  m_allowTheft = false;

    private:
        struct Unlocked {
            void* pickup;
            bool  pickupable;
            float proximity;
            bool  disallowTheft;
        };
        std::vector<Unlocked>     m_unlocked;
        std::unordered_set<void*> m_known;
        double                    m_next = 0.0;
    };

    // FORCE GRAB -- aim at an object, take it, carry it, drop it.
    //
    // This removes the WALK, not the permission: a VRC_Pickup is something the author marked
    // grabbable by anyone. VRCSDKBase exposes only getters -- IsHeld, currentPlayer -- and no
    // "grab this", because the real hold lives inside the game's own pickup system. So while it is
    // taken the object is carried in front of the camera every frame and let go on a second press.
    //
    // ONE GUARD, AND IT IS THE ONE THAT MATTERS: nothing held by somebody else. That is the whole
    // difference between fetching a prop and yanking it out of another player's hands.
    class ForceGrabModule : public Module {
    public:
        ForceGrabModule();
        void OnUpdate()  override;
        void OnDisable() override;

        float m_reach    = 30.f;    // how far the aim ray looks
        float m_distance = 0.75f;   // how far in front of the camera it is carried

    private:
        void Take();
        void Drop();

        void* m_held = nullptr;      // the Transform being carried
        void* m_body = nullptr;      // its Rigidbody, if it has one
        bool  m_wasKinematic = false;
        bool  m_pressWas = false;
        std::string m_heldName;
    };
}
