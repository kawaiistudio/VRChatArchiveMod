#pragma once
#include "module.hpp"

#include <unordered_map>
#include <vector>

namespace VRCA {

    // INFINITE PORTAL -- the portals YOU drop stop closing, and the wait between drops is gone.
    //
    // THIS IS NOT A PORTAL SPAWNER. VRChat already has one: the native "Create Portal" flow where
    // you pick the destination. This changes two things about the portals that flow creates --
    // the countdown that closes them, and the client-side wait before the next drop. Everything a
    // portal does is unchanged; it just stays.
    //
    // OTHERS SEE THESE. A dropped portal is a networked object, so leaving it open leaves it open
    // for the whole instance. That is the point -- you are inviting people -- but it is stated
    // plainly rather than hidden.
    //
    // NOTHING IS HARDCODED TO AN OFFSET. The countdown lives in native code with no readable
    // field, so the timer is found for what it DOES, measured over successive passes:
    //
    //   a portal keeps its lifetime as a CONSTANT float cap (30.0 on this build) and counts an
    //   ELAPSED float UP toward it; it closes when elapsed >= cap.
    //
    // So the pairing is the proof: a float that RISES by about the time that passed, and a steady
    // float sitting in a plausible lifetime window. The cap is then pushed out of reach. A cap
    // with no rising sibling is just a constant and is left alone.
    class PortalModule : public Module {
    public:
        PortalModule();
        void OnUpdate()  override;
        void OnDisable() override;

    private:
        void ScanPortals();
        void ClearCooldown();

        struct Pinned { void* portal; int capOff; float wasCap; };
        std::vector<Pinned> m_pinned;

        // Previous value of every float slot we are watching, keyed by (object, offset).
        std::unordered_map<unsigned long long, float> m_prev;
        double m_nextPass = 0.0;
        bool   m_loggedLife = false;
        bool   m_loggedNoClass = false;
    };
}
