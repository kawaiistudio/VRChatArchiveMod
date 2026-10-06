#pragma once
#include "module.hpp"

namespace VRCA {

    // CLICK-TELEPORT -- hold right mouse, tap left, and you stand where you were aiming.
    //
    // A raycast down the camera's forward (the crosshair) finds the first solid surface and drops
    // the local player just above it. On by default, as the owner wants: it costs nothing until the
    // two-button combo is pressed, and the combo is deliberate enough not to fire by accident.
    //
    // GUARDS. Only when the game window is focused AND the cursor is LOCKED -- that is desktop
    // VRChat's "I am looking around, not in a menu" state, so clicking a menu button never warps
    // you, and a click in another app while alt-tabbed is ignored.
    class ClickTpModule : public Module {
    public:
        ClickTpModule();
        void OnUpdate() override;

        bool  m_on          = true;     // owner: enabled by default in the mod
        float m_maxDistance = 120.f;

    private:
        bool m_leftWas = false;
    };
}
