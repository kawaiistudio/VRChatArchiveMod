#include "pickups.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "unity.hpp"
#include "worldobjects.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <cmath>

namespace VRCA {

namespace {
    constexpr char const* kPickup = "VRC.SDKBase.VRC_Pickup";

    bool Key(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    // VRC_Pickup's three locks are FIELDS, not properties, on this SDK: reading them through a
    // getter that does not exist would quietly return nothing and the unlock would do nothing.
    int FieldOff(char const* name) {
        void* k = Il2::FindClass(kPickup);
        if (!k) return -1;
        void* it = nullptr;
        while (void* f = Il2::NextField(k, &it)) {
            if (Il2::FieldIsStatic(f)) continue;
            char const* fn = Il2::FieldName(f);
            if (fn && std::strcmp(fn, name) == 0) return Il2::FieldOffsetOf(f);
        }
        return -1;
    }
    int OffPickupable() { static int o = FieldOff("pickupable");    return o; }
    int OffProximity()  { static int o = FieldOff("proximity");     return o; }
    int OffTheft()      { static int o = FieldOff("DisallowTheft"); return o; }

    bool  RdBool (void* o, int off)            { return off > 0 && *reinterpret_cast<unsigned char*>(static_cast<char*>(o) + off) != 0; }
    void  WrBool (void* o, int off, bool v)    { if (off > 0) *reinterpret_cast<unsigned char*>(static_cast<char*>(o) + off) = v ? 1 : 0; }
    float RdFloat(void* o, int off)            { return off > 0 ? *reinterpret_cast<float*>(static_cast<char*>(o) + off) : 0.f; }
    void  WrFloat(void* o, int off, float v)   { if (off > 0) *reinterpret_cast<float*>(static_cast<char*>(o) + off) = v; }

    bool PickupIsHeld(void* pickup) {
        static void* m = nullptr;
        static bool looked = false;
        if (!looked) { looked = true; if (void* k = Il2::FindClass(kPickup)) m = Il2::FindMethodIn(k, "get_IsHeld", 0); }
        if (!m) return false;
        void* r = Il2::Invoke(m, pickup, nullptr);
        return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10) != 0;
    }

    std::vector<void*> AllPickups() {
        std::vector<void*> out;
        static void* findAll = Il2::FindMethod("UnityEngine.Object", "FindObjectsOfType", 1);
        void* k = Il2::FindClass(kPickup);
        if (!findAll || !k) return out;
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!cgt || !tgo) return out;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* ty = reinterpret_cast<CGT>(cgt)(k);
        void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
        if (!sysType) return out;
        void* a[1] = { sysType };
        void* arr = Il2::Invoke(findAll, nullptr, a);
        int n = Il2::ArrayLength(arr);
        for (int i = 0; i < n; ++i) if (void* p = Il2::ArrayAt(arr, i)) out.push_back(p);
        return out;
    }

    bool CameraRay(float origin[3], float dir[3]) {
        static void* mainCam = Il2::FindMethod("UnityEngine.Camera", "get_main", 0);
        static void* getTr   = Il2::FindMethod("UnityEngine.Component", "get_transform", 0);
        static void* getFwd  = Il2::FindMethod("UnityEngine.Transform", "get_forward", 0);
        if (!mainCam || !getTr || !getFwd) return false;
        void* cam = Il2::Invoke(mainCam, nullptr, nullptr);
        if (!Unity::IsAlive(cam)) return false;
        void* tr = Il2::Invoke(getTr, cam, nullptr);
        if (!tr || !Unity::GetPosition(tr, origin)) return false;
        void* f = Il2::Invoke(getFwd, tr, nullptr);
        if (!f) return false;
        auto const* ff = reinterpret_cast<float const*>(static_cast<char*>(f) + 0x10);
        dir[0] = ff[0]; dir[1] = ff[1]; dir[2] = ff[2];
        return true;
    }
}

// ============================================================================= FORCE PICKUP

ForcePickupModule::ForcePickupModule()
    : Module("Force Pickup", "make pickupable the objects the world has locked")
{
    Config::Float("Force Pickup", "Proximity",
                  "distance at which the game offers the pickup", &m_proximity, 1.f, 1000.f);
    Config::Bool ("Force Pickup", "AllowTheft",
                  "also allow taking what another player is holding", &m_allowTheft);

    Actions::Register("forcePickup", [](std::string const&, void* u) {
        auto* m = static_cast<ForcePickupModule*>(u);
        m->SetEnabled(!m->Enabled());
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<ForcePickupModule*>(u);
        out += ",\"forcePickup\":"      + Json::Bool(m->Enabled());
        out += ",\"forcePickupCount\":" + Json::Int(static_cast<long long>(m->m_unlocked.size()));
    }, this);
}

void ForcePickupModule::OnDisable() {
    int n = 0;
    for (auto const& o : m_unlocked) {
        if (!Unity::IsAlive(o.pickup)) continue;
        WrBool (o.pickup, OffPickupable(), o.pickupable);
        WrFloat(o.pickup, OffProximity(),  o.proximity);
        WrBool (o.pickup, OffTheft(),      o.disallowTheft);
        ++n;
    }
    m_unlocked.clear();
    m_known.clear();
    m_next = 0.0;
    if (n) Log::Writef("Info", "[ForcePickup] off -- %d objet(s) reverrouille(s) comme avant.", n);
}

void ForcePickupModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_next) return;
    m_next = now + 2.0;    // re-swept so pickups spawned later are unlocked too

    if (OffPickupable() <= 0) {
        Log::Warn("[ForcePickup] VRC_Pickup does not expose 'pickupable' on this build -- inactive.");
        SetEnabled(false);
        return;
    }

    // Drop anything that died rather than keeping a dangling pointer in the undo ledger.
    for (auto it = m_unlocked.begin(); it != m_unlocked.end(); ) {
        if (Unity::IsAlive(it->pickup)) { ++it; continue; }
        m_known.erase(it->pickup);
        it = m_unlocked.erase(it);
    }

    int added = 0;
    for (void* p : AllPickups()) {
        if (!Unity::IsAlive(p)) continue;
        if (!m_known.insert(p).second) continue;

        Unlocked u{ p, RdBool(p, OffPickupable()), RdFloat(p, OffProximity()), RdBool(p, OffTheft()) };
        bool changed = false;
        if (!u.pickupable)                { WrBool (p, OffPickupable(), true);        changed = true; }
        if (u.proximity < m_proximity)    { WrFloat(p, OffProximity(),  m_proximity); changed = true; }
        if (m_allowTheft && u.disallowTheft) { WrBool(p, OffTheft(),    false);       changed = true; }
        if (!changed) continue;            // nothing was locked: not ours to put back later

        m_unlocked.push_back(u);
        ++added;
    }
    if (added > 0)
        Log::Writef("Info", "[ForcePickup] %zu objet(s) deverrouille(s) (+%d).", m_unlocked.size(), added);
}

// ============================================================================== FORCE GRAB

ForceGrabModule::ForceGrabModule()
    : Module("Force Grab", "aim at an object and pull it into your hands (middle click)")
{
    Config::Float("Force Grab", "Reach",    "aim ray reach", &m_reach, 1.f, 200.f);
    Config::Float("Force Grab", "Distance", "carry distance in front of the camera", &m_distance, 0.2f, 5.f);

    Actions::Register("forceGrab", [](std::string const&, void* u) {
        auto* m = static_cast<ForceGrabModule*>(u);
        if (!m->Enabled()) m->SetEnabled(true);
        m->OnUpdate();                 // the client's button is the press itself
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<ForceGrabModule*>(u);
        out += ",\"forceGrab\":"     + Json::Bool(m->Enabled());
        out += ",\"forceGrabName\":" + Json::Str(m->m_held ? m->m_heldName : std::string());
    }, this);
}

void ForceGrabModule::Drop() {
    if (m_body && Unity::IsAlive(m_body)) {
        static void* setKin = Il2::FindMethod("UnityEngine.Rigidbody", "set_isKinematic", 1);
        if (setKin) { bool v = m_wasKinematic; void* a[1] = { &v }; Il2::Invoke(setKin, m_body, a); }
    }
    if (!m_heldName.empty()) Log::Writef("Info", "[ForceGrab] lache '%s'.", m_heldName.c_str());
    m_held = m_body = nullptr;
    m_heldName.clear();
}

void ForceGrabModule::OnDisable() { Drop(); }

// AIM BY ANGLE, NOT BY A PHYSICS RAY. A raycast would need a layer mask matched to each world's
// own setup, and would miss anything whose collider is a trigger. Picking the candidate closest to
// the line of sight finds what the player is plainly looking at, and only ever among objects the
// shared collector already judged to be loose props.
void ForceGrabModule::Take() {
    float o[3], d[3];
    if (!CameraRay(o, d)) return;

    void* best = nullptr;
    float bestScore = 0.9f;        // at least ~25 degrees off-axis is not "aimed at"
    std::string bestName;

    for (auto const& f : WorldObjects::Collect(o, m_reach, 200, false)) {
        if (!Unity::IsAlive(f.transform)) continue;

        // NOTHING HELD BY SOMEONE ELSE. This is the one guard that matters.
        void* pick = Unity::GetComponent(Unity::GameObjectOf(f.transform), kPickup);
        if (Unity::IsAlive(pick) && PickupIsHeld(pick)) continue;

        float p[3];
        if (!Unity::GetPosition(f.transform, p)) continue;
        float vx = p[0] - o[0], vy = p[1] - o[1], vz = p[2] - o[2];
        float len = std::sqrt(vx * vx + vy * vy + vz * vz);
        if (len < 0.01f) continue;
        float dot = (vx * d[0] + vy * d[1] + vz * d[2]) / len;
        if (dot <= bestScore) continue;
        bestScore = dot;
        best = f.transform;
        bestName = Unity::Name(Unity::GameObjectOf(f.transform));
    }

    if (!best) { Log::Info("[ForceGrab] nothing aimed at in range."); return; }

    m_held = best;
    m_heldName = bestName;
    m_body = Unity::GetComponent(Unity::GameObjectOf(best), "UnityEngine.Rigidbody");
    if (!Unity::IsAlive(m_body)) m_body = nullptr;
    if (m_body) {
        static void* getKin = Il2::FindMethod("UnityEngine.Rigidbody", "get_isKinematic", 0);
        static void* setKin = Il2::FindMethod("UnityEngine.Rigidbody", "set_isKinematic", 1);
        if (getKin) {
            void* r = Il2::Invoke(getKin, m_body, nullptr);
            m_wasKinematic = r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10) != 0;
        }
        if (setKin) { bool v = true; void* a[1] = { &v }; Il2::Invoke(setKin, m_body, a); }
    }
    Log::Writef("Info", "[ForceGrab] pris '%s'.", m_heldName.c_str());
}

void ForceGrabModule::OnUpdate() {
    // Middle mouse takes and lets go, so it never fights VRChat's own grab on the usual buttons.
    bool press = Key(VK_MBUTTON);
    if (press && !m_pressWas) { if (m_held) Drop(); else Take(); }
    m_pressWas = press;

    if (!m_held) return;
    if (!Unity::IsAlive(m_held)) { m_held = m_body = nullptr; m_heldName.clear(); return; }

    float o[3], d[3];
    if (!CameraRay(o, d)) return;
    float p[3] = { o[0] + d[0] * m_distance, o[1] + d[1] * m_distance, o[2] + d[2] * m_distance };
    Unity::SetPosition(m_held, p);
}

}
