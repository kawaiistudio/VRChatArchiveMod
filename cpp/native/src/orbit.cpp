#include "orbit.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "log.hpp"

#include <cmath>

namespace VRCA {

OrbitModule::OrbitModule()
    : Module("Orbit", "orbit a player, or sit on them")
{
    // The client's per-player buttons. Each sends the target's user id as the value; an empty
    // value, or the same target again, means stop -- which is what Toggle already does.
    Actions::Register("orbit", [](std::string const& v, void* u) {
        static_cast<OrbitModule*>(u)->Toggle(Mode::Orbit, v);
    }, this);
    Actions::Register("sit", [](std::string const& v, void* u) {
        static_cast<OrbitModule*>(u)->Toggle(Mode::Sit, v);
    }, this);
    Actions::Register("orbitStop", [](std::string const&, void* u) {
        static_cast<OrbitModule*>(u)->Toggle(Mode::Off, "");
    }, this);

    // WHAT THE CLIENT SHOWS. The PLAYERS page marks the row whose user id equals orbitTarget, so
    // the id goes out as-is -- not the name, which is not what it compares against.
    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<OrbitModule*>(u);
        char const* mode = m->m_mode == Mode::Orbit ? "orbit"
                         : m->m_mode == Mode::Sit   ? "sit" : "off";
        out += ",\"orbitMode\":"   + Json::Str(mode);
        out += ",\"orbitTarget\":" + Json::Str(m->m_mode == Mode::Off ? std::string() : m->m_target);
    }, this);
}

void OrbitModule::Toggle(Mode mode, std::string const& userId) {
    // Same target, same mode -> off. Anything else switches to the new target/mode.
    if (mode == Mode::Off || (m_mode == mode && m_target == userId && !userId.empty())) {
        m_mode = Mode::Off;
        m_target.clear();
        SetEnabled(false);
        Log::Info("[Orbit] stopped.");
        return;
    }
    m_mode   = mode;
    m_target = userId;
    m_angle  = 0.f;
    SetEnabled(true);
    Log::Writef("Info", "[Orbit] %s on '%s'.", mode == Mode::Orbit ? "orbite" : "assis",
                userId.c_str());
}

void OrbitModule::OnDisable() {
    m_mode = Mode::Off;
    m_target.clear();
}

void OrbitModule::OnUpdate() {
    if (m_mode == Mode::Off || m_target.empty()) return;

    void* local = Player::LocalApi();
    if (!local) return;

    // Resolve the target afresh each frame by user id: pointers do not survive an avatar change.
    Player::Vec3 tp{};
    bool found = false;
    for (auto const& p : Player::All()) {
        if (p.isLocal || p.userId != m_target) continue;
        tp = p.pos;
        found = true;
        break;
    }
    if (!found) {
        // They left (or their account data is not readable yet). Stop rather than fly to origin.
        Log::Writef("Info", "[Orbit] target '%s' gone -- stopping.", m_target.c_str());
        Toggle(Mode::Off, "");
        return;
    }

    double now = Engine::Time();
    float dt = (m_lastTick > 0.0) ? static_cast<float>(now - m_lastTick) : 0.016f;
    if (dt > 0.1f) dt = 0.1f;      // a hitch must not fling us across the map
    m_lastTick = now;

    Player::Vec3 dst;
    if (m_mode == Mode::Orbit) {
        m_angle += m_speed * dt;
        if (m_angle >= 360.f) m_angle -= 360.f;
        float rad = m_angle * 3.14159265f / 180.f;
        dst = { tp.x + std::cos(rad) * m_radius,
                tp.y + m_height,
                tp.z + std::sin(rad) * m_radius };
    } else {
        dst = { tp.x, tp.y + m_sitTop, tp.z };
    }
    Player::TeleportTo(local, dst);
}

}
