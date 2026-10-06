#pragma once
#include "module.hpp"
#include "player.hpp"

#include <string>

namespace VRCA {

    // ORBIT / SIT -- ride another player.
    //
    // Orbit circles the target at a fixed radius and height, always facing them; Sit parks you on
    // top of them. Both are driven by writing the local player's position every frame (TeleportTo),
    // because VRChat's character controller fights a velocity push but lands exactly where a
    // teleport asks.
    //
    // The target is remembered by USER ID, not by pointer: a player's objects are rebuilt on avatar
    // change, and a stale pointer would either do nothing or follow the wrong body.
    class OrbitModule : public Module {
    public:
        OrbitModule();

        void OnUpdate()  override;
        void OnDisable() override;

        enum class Mode { Off, Orbit, Sit };

        // Switching the same target off, or to another mode, is the same call -- that is how the
        // client's toggle behaves.
        void Toggle(Mode mode, std::string const& userId);

        float m_radius = 1.6f;
        float m_height = 0.4f;
        float m_speed  = 60.f;   // degrees per second
        float m_sitTop = 1.4f;   // how far above their feet "sitting" puts you

    private:
        Mode        m_mode = Mode::Off;
        std::string m_target;
        float       m_angle = 0.f;
        double      m_lastTick = 0.0;
    };
}
