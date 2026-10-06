#include "objects.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"
#include "unity.hpp"
#include "worldobjects.hpp"

#include <algorithm>
#include <cmath>

namespace VRCA {

namespace {
    constexpr float kDeg2Rad = 3.14159265358979f / 180.f;

    void* RigidbodyOf(void* transform) {
        void* go = Unity::GameObjectOf(transform);
        void* b = Unity::GetComponent(go, "UnityEngine.Rigidbody");
        if (Unity::IsAlive(b)) return b;
        b = Unity::GetComponentInChildren(go, "UnityEngine.Rigidbody", false);
        return Unity::IsAlive(b) ? b : nullptr;
    }

    bool GetBool0(void* obj, char const* cls, char const* getter) {
        static void* cached = nullptr; (void)cached;
        void* m = Il2::FindMethod(cls, getter, 0);
        if (!obj || !m) return false;
        void* r = Il2::Invoke(m, obj, nullptr);
        return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10) != 0;
    }
    void SetBool1(void* obj, char const* cls, char const* setter, bool v) {
        void* m = Il2::FindMethod(cls, setter, 1);
        if (!obj || !m) return;
        bool b = v; void* a[1] = { &b };
        Il2::Invoke(m, obj, a);
    }
    int GetInt0(void* obj, char const* cls, char const* getter) {
        void* m = Il2::FindMethod(cls, getter, 0);
        if (!obj || !m) return 0;
        void* r = Il2::Invoke(m, obj, nullptr);
        return r ? *reinterpret_cast<int*>(static_cast<char*>(r) + 0x10) : 0;
    }
    void SetInt1(void* obj, char const* cls, char const* setter, int v) {
        void* m = Il2::FindMethod(cls, setter, 1);
        if (!obj || !m) return;
        int i = v; void* a[1] = { &i };
        Il2::Invoke(m, obj, a);
    }

    // MOVE THROUGH THE BODY when there is one: MovePosition on an interpolated kinematic body is
    // the moving-platform path, and it is the body's velocity that remote clients dead-reckon.
    void MoveTo(HeldObject const& h, float const p[3]) {
        static void* movePos = Il2::FindMethod("UnityEngine.Rigidbody", "MovePosition", 1);
        if (h.body && movePos) {
            float v[3] = { p[0], p[1], p[2] };
            void* a[1] = { v };
            Il2::Invoke(movePos, h.body, a);
            return;
        }
        Unity::SetPosition(h.transform, p);
    }

    bool GetRot(void* transform, float out4[4]) {
        static void* get = Il2::FindMethod("UnityEngine.Transform", "get_rotation", 0);
        if (!transform || !get) return false;
        void* boxed = Il2::Invoke(get, transform, nullptr);
        if (!boxed) return false;
        auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
        out4[0] = f[0]; out4[1] = f[1]; out4[2] = f[2]; out4[3] = f[3];
        return true;
    }
    void SetRot(void* transform, float const in4[4]) {
        static void* set = Il2::FindMethod("UnityEngine.Transform", "set_rotation", 1);
        if (!transform || !set) return;
        float v[4] = { in4[0], in4[1], in4[2], in4[3] };
        void* a[1] = { v };
        Il2::Invoke(set, transform, a);
    }

    // TAKE the object: freeze its body so physics stops fighting us, and record every value we
    // touched so Release() can undo exactly this and nothing more.
    HeldObject Take(void* transform, bool forceBody) {
        HeldObject h;
        h.transform  = transform;
        h.gameObject = Unity::GameObjectOf(transform);
        Unity::GetPosition(transform, h.pos);
        GetRot(transform, h.rot);

        h.body = RigidbodyOf(transform);
        if (!h.body && forceBody) {
            // A prop with no body is not a physics sweep: moving its transform pushes nothing, so a
            // platform made of them would not carry anyone. Give it one, and take it away again.
            h.body = Unity::AddComponent(h.gameObject, "UnityEngine.Rigidbody");
            h.addedBody = Unity::IsAlive(h.body);
            if (!h.addedBody) h.body = nullptr;
        }
        if (h.body) {
            h.wasKinematic     = GetBool0(h.body, "UnityEngine.Rigidbody", "get_isKinematic");
            h.wasInterpolation = GetInt0(h.body, "UnityEngine.Rigidbody", "get_interpolation");
            h.wasGravity       = GetBool0(h.body, "UnityEngine.Rigidbody", "get_useGravity");
            SetBool1(h.body, "UnityEngine.Rigidbody", "set_isKinematic", true);
            SetInt1 (h.body, "UnityEngine.Rigidbody", "set_interpolation", 1);  // Interpolate
            SetBool1(h.body, "UnityEngine.Rigidbody", "set_useGravity", false);
        }
        return h;
    }

    void Release(HeldObject const& h) {
        if (!Unity::IsAlive(h.transform)) return;
        if (h.body && Unity::IsAlive(h.body)) {
            SetBool1(h.body, "UnityEngine.Rigidbody", "set_useGravity", h.wasGravity);
            SetInt1 (h.body, "UnityEngine.Rigidbody", "set_interpolation", h.wasInterpolation);
            SetBool1(h.body, "UnityEngine.Rigidbody", "set_isKinematic", h.wasKinematic);
            if (h.addedBody) Unity::Destroy(h.body);
        }
        Unity::SetPosition(h.transform, h.pos);
        SetRot(h.transform, h.rot);
    }

    void ReleaseAll(std::vector<HeldObject>& held) {
        for (auto const& h : held) Release(h);
        held.clear();
    }

    // The player a per-player button named, by user id, resolved afresh: a pointer does not survive
    // an avatar change.
    bool PositionOf(std::string const& userId, float out3[3]) {
        for (auto const& p : Player::All()) {
            if (p.userId != userId) continue;
            out3[0] = p.pos.x; out3[1] = p.pos.y; out3[2] = p.pos.z;
            return true;
        }
        return false;
    }
}

// ============================================================================== OBJECT ORBIT

ObjectOrbitModule::ObjectOrbitModule()
    : Module("Object Orbit", "spin the world's loose objects around a player")
{
    Config::Float("Object Orbit", "Radius", "ring radius",        &m_radius, 0.5f, 20.f);
    Config::Float("Object Orbit", "Height", "height above the feet", &m_height, -5.f, 10.f);
    Config::Float("Object Orbit", "Speed",  "degrees per second",       &m_speed, 0.f, 720.f);
    Config::Float("Object Orbit", "Range",  "pickup radius in metres", &m_range, 2.f, 200.f);
    Config::Int  ("Object Orbit", "Count",  "object count -- 0 = all", &m_count, 0, 400);
    Config::Bool ("Object Orbit", "Spin",   "also spin the objects on themselves", &m_spin);

    Actions::Register("objectOrbit", [](std::string const& v, void* u) {
        static_cast<ObjectOrbitModule*>(u)->Start(v);
    }, this);
    Actions::Register("objectOrbitStop", [](std::string const&, void* u) {
        static_cast<ObjectOrbitModule*>(u)->SetEnabled(false);
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<ObjectOrbitModule*>(u);
        // The PLAYERS page matches this against the row's NAME, not its id.
        auto nameOf = [](std::string const& uid) {
            if (uid.empty()) return std::string();
            for (auto const& p : Player::All()) if (p.userId == uid) return p.name;
            return std::string();
        };
        bool on = m->Enabled();
        out += ",\"objectOrbit\":"       + Json::Bool(on);
        out += ",\"objectOrbitCenter\":" + Json::Str(on ? nameOf(m->m_target) : std::string());
        out += ",\"objectOrbitCount\":"  + Json::Int(on ? static_cast<long long>(m->m_held.size()) : 0);
    }, this);
}

void ObjectOrbitModule::Start(std::string const& userId) {
    Stop(nullptr);
    float c[3];
    if (!PositionOf(userId, c)) {
        Log::Writef("Warning", "[ObjectOrbit] player '%s' not found.", userId.c_str());
        ScreenUI::Toast("Object orbit: player not found");
        return;
    }
    int want = m_count > 0 ? m_count : 400;
    auto found = WorldObjects::Collect(c, m_range, want, false);
    if (found.empty()) {
        Log::Writef("Warning", "[ObjectOrbit] no loose object within %d m.",
                    static_cast<int>(m_range));
        ScreenUI::Toast("No loose objects in range");
        return;
    }
    m_held.reserve(found.size());
    for (size_t i = 0; i < found.size(); ++i) {
        HeldObject h = Take(found[i].transform, false);
        h.phase = (360.f / static_cast<float>(found.size())) * static_cast<float>(i);
        // Spread over a few heights so it reads as a swarm, not a flat disc.
        h.tilt  = (static_cast<float>(i % 5) - 2.f) * 0.45f;
        m_held.push_back(h);
    }
    m_target = userId;
    m_angle = 0.f;
    SetEnabled(true);
    Log::Writef("Info", "[ObjectOrbit] %zu object(s) ringed around '%s'.",
                m_held.size(), userId.c_str());
}

void ObjectOrbitModule::Stop(char const* why) {
    if (!m_held.empty() && why)
        Log::Writef("Info", "[ObjectOrbit] %s -- %zu objet(s) remis.", why, m_held.size());
    ReleaseAll(m_held);
    m_target.clear();
}

void ObjectOrbitModule::OnDisable() { Stop("stopped"); }

void ObjectOrbitModule::OnUpdate() {
    if (m_held.empty()) { SetEnabled(false); return; }

    float c[3];
    if (!PositionOf(m_target, c)) { Stop("the centre is gone"); SetEnabled(false); return; }

    double now = Engine::Time();
    float dt = (m_lastTick > 0.0) ? static_cast<float>(now - m_lastTick) : 0.016f;
    if (dt > 0.1f) dt = 0.1f;
    m_lastTick = now;

    m_angle += m_speed * dt;
    if (m_angle >= 360.f) m_angle -= 360.f;
    float r = (std::max)(0.5f, m_radius);

    for (size_t i = m_held.size(); i-- > 0; ) {
        HeldObject const& h = m_held[i];
        // A held object can be destroyed under us at any moment -- a pickup despawning, a world
        // unloading a chunk -- and writing a freed transform is an access violation, not an
        // exception. Liveness is proven every frame.
        if (!Unity::IsAlive(h.transform)) {
            m_held.erase(m_held.begin() + static_cast<long long>(i));
            continue;
        }
        float rad = (m_angle + h.phase) * kDeg2Rad;
        float p[3] = { c[0] + std::cos(rad) * r, c[1] + m_height + h.tilt, c[2] + std::sin(rad) * r };
        MoveTo(h, p);
    }

    if (m_held.empty()) { Log::Info("[ObjectOrbit] no live object left -- stopping."); SetEnabled(false); }
}

// ============================================================================== ELEVATOR

ElevatorModule::ElevatorModule()
    : Module("Elevator", "gather the world's objects into a platform under a player and raise it")
{
    Config::Float("Elevator", "ClimbSpeed", "metres per second",            &m_climbSpeed, 0.1f, 40.f);
    Config::Float("Elevator", "MaxHeight",  "maximum height -- 0 = no limit", &m_maxHeight, 0.f, 200.f);
    Config::Float("Elevator", "Radius",     "platform radius",        &m_radius, 0.f, 5.f);
    Config::Float("Elevator", "Range",      "pickup radius in metres",  &m_range, 2.f, 200.f);
    Config::Int  ("Elevator", "Count",      "object count -- 0 = all",   &m_count, 0, 400);
    Config::Bool ("Elevator", "AutoCorrect",
                  "follow the rider sideways so they cannot step off",
                  &m_autoCorrect);

    Actions::Register("elevatorPlayer", [](std::string const& v, void* u) {
        static_cast<ElevatorModule*>(u)->Start(v);
    }, this);
    Actions::Register("elevatorPlayerStop", [](std::string const&, void* u) {
        static_cast<ElevatorModule*>(u)->SetEnabled(false);
    }, this);
    Actions::Register("elevatorHeight", [](std::string const& v, void* u) {
        auto* m = static_cast<ElevatorModule*>(u);
        m->m_maxHeight = static_cast<float>(atof(v.c_str()));
    }, this);
    Actions::Register("elevatorSpeed", [](std::string const& v, void* u) {
        auto* m = static_cast<ElevatorModule*>(u);
        float f = static_cast<float>(atof(v.c_str()));
        if (f > 0.f) m->m_climbSpeed = f;
    }, this);
    Actions::Register("elevatorAutoCorrect", [](std::string const& v, void* u) {
        auto* m = static_cast<ElevatorModule*>(u);
        m->m_autoCorrect = (v == "True" || v == "true" || v == "1");
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<ElevatorModule*>(u);
        // The PLAYERS page matches this against the row's NAME, not its id.
        auto nameOf = [](std::string const& uid) {
            if (uid.empty()) return std::string();
            for (auto const& p : Player::All()) if (p.userId == uid) return p.name;
            return std::string();
        };
        bool on = m->Enabled();
        out += ",\"elevator\":"            + Json::Bool(on);
        out += ",\"elevatorCenter\":"      + Json::Str(on ? nameOf(m->m_target) : std::string());
        out += ",\"elevatorCount\":"       + Json::Int(on ? static_cast<long long>(m->m_held.size()) : 0);
        out += ",\"elevatorAutoCorrect\":" + Json::Bool(m->m_autoCorrect);
    }, this);
}

void ElevatorModule::Start(std::string const& userId) {
    Stop(nullptr);
    float c[3];
    if (!PositionOf(userId, c)) {
        Log::Writef("Warning", "[Elevator] player '%s' not found.", userId.c_str());
        ScreenUI::Toast("Elevator: player not found");
        return;
    }
    int want = m_count > 0 ? m_count : 400;
    auto found = WorldObjects::Collect(c, m_range, want, false);
    if (found.empty()) {
        Log::Writef("Warning", "[Elevator] no loose object within %d m to form a floor.",
                    static_cast<int>(m_range));
        ScreenUI::Toast("No objects to build the platform");
        return;
    }

    // A TINY FLAT DISC, not a wide one and not a single stacked point. Wide left gaps you fell
    // through; one point is not flat enough to stand on. Sunflower packing at the SAME height
    // gives a compact, level pad of overlapping colliders right under the feet.
    m_held.reserve(found.size());
    for (size_t i = 0; i < found.size(); ++i) {
        HeldObject h = Take(found[i].transform, true);   // force a body: a platform must push
        float rr = m_radius <= 0.f ? 0.f
                 : m_radius * std::sqrt((static_cast<float>(i) + 0.5f) / static_cast<float>(found.size()));
        float ang = static_cast<float>(i) * 2.399963f;   // golden angle
        h.ox = std::cos(ang) * rr;
        h.oz = std::sin(ang) * rr;
        m_held.push_back(h);
    }
    m_target = userId;
    m_lift = 0.f;
    m_base[0] = c[0]; m_base[1] = c[1]; m_base[2] = c[2];
    SetEnabled(true);
    Log::Writef("Info", "[Elevator] platform of %zu object(s) under '%s'.",
                m_held.size(), userId.c_str());
    // SAID ON SCREEN, because "nothing happened" and "it built a pad of three objects you cannot
    // stand on" look identical from the client's button.
    ScreenUI::Toast("Elevator: platform of " + std::to_string(m_held.size()) + " objet(s)");
}

void ElevatorModule::Stop(char const* why) {
    if (!m_held.empty() && why)
        Log::Writef("Info", "[Elevator] %s -- %zu objet(s) remis.", why, m_held.size());
    ReleaseAll(m_held);
    m_target.clear();
    m_lift = 0.f;
}

void ElevatorModule::OnDisable() { Stop("stopped"); }

void ElevatorModule::OnUpdate() {
    if (m_held.empty()) { SetEnabled(false); return; }

    float c[3];
    bool haveRider = PositionOf(m_target, c);
    if (!haveRider) { Stop("the rider is gone"); SetEnabled(false); return; }

    double now = Engine::Time();
    float dt = (m_lastTick > 0.0) ? static_cast<float>(now - m_lastTick) : 0.016f;
    if (dt > 0.1f) dt = 0.1f;
    m_lastTick = now;

    m_lift += m_climbSpeed * dt;
    if (m_maxHeight > 0.f && m_lift > m_maxHeight) m_lift = m_maxHeight;

    // FOLLOW THE RIDER SIDEWAYS. Without this they simply walk off the pad and it rises empty.
    // The pad still climbs from the floor it started on, so the ride stays a straight line up.
    float cx = m_autoCorrect ? c[0] : m_base[0];
    float cz = m_autoCorrect ? c[2] : m_base[2];
    float y  = m_base[1] + m_lift;

    for (size_t i = m_held.size(); i-- > 0; ) {
        HeldObject const& h = m_held[i];
        if (!Unity::IsAlive(h.transform)) {
            m_held.erase(m_held.begin() + static_cast<long long>(i));
            continue;
        }
        float p[3] = { cx + h.ox, y, cz + h.oz };
        MoveTo(h, p);
    }

    if (m_held.empty()) {
        Log::Info("[Elevator] no live object left -- stopping.");
        ScreenUI::Toast("Elevator: the world reclaimed its objects");
        SetEnabled(false);
    }
}

// ============================================================================== FLOAT OBJECTS

FloatObjectsModule::FloatObjectsModule()
    : Module("Float Objects", "remove gravity from every pickupable object in the world")
{
    Actions::Register("floatObjects", [](std::string const&, void* u) {
        auto* m = static_cast<FloatObjectsModule*>(u);
        m->SetEnabled(!m->Enabled());
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<FloatObjectsModule*>(u);
        out += ",\"floatObjects\":"      + Json::Bool(m->Enabled());
        out += ",\"floatObjectsCount\":" + Json::Int(static_cast<long long>(m->m_bodies.size()));
    }, this);
}

void FloatObjectsModule::OnDisable() {
    int restored = 0;
    for (auto const& f : m_bodies) {
        if (!Unity::IsAlive(f.body)) continue;
        SetBool1(f.body, "UnityEngine.Rigidbody", "set_useGravity", f.wasGravity);
        ++restored;
    }
    m_bodies.clear();
    m_next = 0.0;
    if (restored) Log::Writef("Info", "[FloatObjects] off -- %d objet(s) retombent.", restored);
}

void FloatObjectsModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_next) return;
    m_next = now + 2.0;   // re-swept, so pickups that appear later float too

    // Drop anything that died rather than carrying a dangling pointer into the next sweep.
    m_bodies.erase(std::remove_if(m_bodies.begin(), m_bodies.end(),
                                  [](Floated const& f) { return !Unity::IsAlive(f.body); }),
                   m_bodies.end());

    float origin[3] = { 0, 0, 0 };
    if (void* api = Player::LocalApi()) {
        Player::Vec3 p = Player::Position(api);
        origin[0] = p.x; origin[1] = p.y; origin[2] = p.z;
    }

    int added = 0;
    for (auto const& f : WorldObjects::Collect(origin, m_range, 2000, false)) {
        void* body = RigidbodyOf(f.transform);
        if (!body) continue;
        bool known = false;
        for (auto const& x : m_bodies) if (x.body == body) { known = true; break; }
        if (known) continue;
        bool was = GetBool0(body, "UnityEngine.Rigidbody", "get_useGravity");
        if (!was) continue;                   // already weightless: not ours to restore later
        m_bodies.push_back({ body, was });
        SetBool1(body, "UnityEngine.Rigidbody", "set_useGravity", false);
        ++added;
    }
    if (added > 0)
        Log::Writef("Info", "[FloatObjects] %zu objet(s) en apesanteur (+%d).", m_bodies.size(), added);
}

}
