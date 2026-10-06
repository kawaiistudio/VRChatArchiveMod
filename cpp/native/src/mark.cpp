#include "mark.hpp"
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
#include <array>
#include <cmath>
#include <cstring>

namespace VRCA {

namespace {
    constexpr float kPi = 3.14159265358979f;

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

    // The camera's right and up, so a shape faces the person who placed it rather than the world
    // axes -- a heart drawn on the world's XY plane is edge-on from most places you stand.
    bool FacingAxes(float right[3], float up[3]) {
        static void* mainCam = Il2::FindMethod("UnityEngine.Camera", "get_main", 0);
        static void* getTr   = Il2::FindMethod("UnityEngine.Component", "get_transform", 0);
        static void* getRt   = Il2::FindMethod("UnityEngine.Transform", "get_right", 0);
        if (!mainCam || !getTr || !getRt) return false;
        void* cam = Il2::Invoke(mainCam, nullptr, nullptr);
        if (!Unity::IsAlive(cam)) return false;
        void* tr = Il2::Invoke(getTr, cam, nullptr);
        void* r  = tr ? Il2::Invoke(getRt, tr, nullptr) : nullptr;
        if (!r) return false;
        auto const* rr = reinterpret_cast<float const*>(static_cast<char*>(r) + 0x10);
        right[0] = rr[0]; right[1] = 0.f; right[2] = rr[2];     // kept level: a tilted head must
        float len = std::sqrt(right[0] * right[0] + right[2] * right[2]);
        if (len < 0.01f) { right[0] = 1.f; right[2] = 0.f; }    // not tip the whole picture over
        else { right[0] /= len; right[2] /= len; }
        up[0] = 0.f; up[1] = 1.f; up[2] = 0.f;
        return true;
    }

    // Physics.Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance).
    // RaycastHit is a by-value struct, so the out parameter is a buffer we own. Its first three
    // floats are the point -- which is all the mark needs.
    bool RayHit(float const o[3], float const d[3], float maxDist, float outPoint[3]) {
        static void* m = Il2::FindMethod("UnityEngine.Physics", "Raycast", 4);
        if (!m) return false;
        float origin[3] = { o[0], o[1], o[2] };
        float dir[3]    = { d[0], d[1], d[2] };
        unsigned char hit[128]{};           // RaycastHit is well under this on every build
        float dist = maxDist;
        void* a[4] = { origin, dir, hit, &dist };
        void* r = Il2::Invoke(m, nullptr, a);
        if (!r || !*reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10)) return false;
        auto const* f = reinterpret_cast<float const*>(hit);
        outPoint[0] = f[0]; outPoint[1] = f[1]; outPoint[2] = f[2];
        return true;
    }

    void* MakeSphere() {
        static void* create = Il2::FindMethod("UnityEngine.GameObject", "CreatePrimitive", 1);
        if (!create) return nullptr;
        int sphere = 0;                      // PrimitiveType.Sphere
        void* a[1] = { &sphere };
        void* go = Il2::Invoke(create, nullptr, a);
        if (!Unity::IsAlive(go)) return nullptr;

        Unity::SetName(go, "VRCA_Mark");
        // No collider: the anchor must never block a step, a pickup or a world trigger.
        if (void* col = Unity::GetComponent(go, "UnityEngine.Collider")) Unity::Destroy(col);
        static void* setScale = Il2::FindMethod("UnityEngine.Transform", "set_localScale", 1);
        if (void* tr = Unity::Transform(go); tr && setScale) {
            float s[3] = { 0.12f, 0.12f, 0.12f };
            void* sa[1] = { s };
            Il2::Invoke(setScale, tr, sa);
        }
        return go;
    }

    // The shapes, exactly as the C# module draws them.
    std::vector<std::array<float, 3>> ShapePoints(std::string const& kind, int n, float size,
                                                  float const right[3], float const up[3]) {
        std::vector<std::array<float, 3>> pts;
        if (n <= 0) return pts;
        pts.reserve(static_cast<size_t>(n));
        float R = size * 0.5f;
        float fwd[3] = { up[1] * right[2] - up[2] * right[1],
                         up[2] * right[0] - up[0] * right[2],
                         up[0] * right[1] - up[1] * right[0] };
        auto at = [&](float a, float b, float c) {
            return std::array<float, 3>{ right[0] * a + up[0] * b + fwd[0] * c,
                                         right[1] * a + up[1] * b + fwd[1] * c,
                                         right[2] * a + up[2] * b + fwd[2] * c };
        };

        if (kind == "line") {
            for (int i = 0; i < n; ++i)
                pts.push_back(at((static_cast<float>(i) / (std::max)(1, n - 1) - 0.5f) * size, 0.3f, 0.f));
        } else if (kind == "wall") {
            int cols = (std::max)(1, static_cast<int>(std::ceil(std::sqrt(n * 1.6f))));
            for (int i = 0; i < n; ++i) {
                int c = i % cols, r = i / cols;
                pts.push_back(at((static_cast<float>(c) / (std::max)(1, cols - 1) - 0.5f) * size,
                                 0.2f + r * (size / (std::max)(1, cols)) * 0.9f, 0.f));
            }
        } else if (kind == "heart") {
            for (int i = 0; i < n; ++i) {
                float t = static_cast<float>(i) / n * kPi * 2.f;
                float st = std::sin(t);
                float x = 16.f * st * st * st;
                float y = 13.f * std::cos(t) - 5.f * std::cos(2 * t) - 2.f * std::cos(3 * t) - std::cos(4 * t);
                pts.push_back(at(x / 17.f * R, 0.9f + y / 17.f * R, 0.f));
            }
        } else if (kind == "spiral") {
            for (int i = 0; i < n; ++i) {
                float t = static_cast<float>(i) / n, a = t * kPi * 6.f;
                pts.push_back(at(std::cos(a) * R * t, 0.1f + t * size, std::sin(a) * R * t));
            }
        } else if (kind == "cube") {
            int side = (std::max)(2, static_cast<int>(std::ceil(std::pow(static_cast<float>(n), 1.f / 3.f))));
            float s = size / (std::max)(1, side - 1);
            for (int i = 0; i < n; ++i) {
                int x = i % side, y = (i / side) % side, z = i / (side * side);
                pts.push_back(at((x - (side - 1) * 0.5f) * s, 0.15f + y * s, (z - (side - 1) * 0.5f) * s));
            }
        } else if (kind == "star") {
            for (int i = 0; i < n; ++i) {
                float t = static_cast<float>(i) / n * kPi * 2.f;
                float rr = R * (0.55f + 0.45f * std::fabs(std::cos(t * 2.5f)));
                pts.push_back(at(std::cos(t) * rr, R + std::sin(t) * rr, 0.f));
            }
        } else {                                   // "ring", and anything unrecognised
            for (int i = 0; i < n; ++i) {
                float a = static_cast<float>(i) / n * kPi * 2.f;
                pts.push_back(at(std::cos(a) * R, 0.6f, std::sin(a) * R));
            }
        }
        return pts;
    }
}

MarkModule::MarkModule()
    : Module("Mark", "an anchor placed at your aim: teleport objects to it, spin them, or form shapes")
{
    Config::Float("Mark", "Range",   "pickup radius in metres", &m_range, 2.f, 200.f);
    Config::Float("Mark", "ArtSize", "shape size -- 0 = auto", &m_artSize, 0.f, 50.f);
    Config::Int  ("Mark", "Count",   "object count -- 0 = all",   &m_count, 0, 800);

    Actions::Register("markPut",   [](std::string const&, void* u) { static_cast<MarkModule*>(u)->Put(); }, this);
    Actions::Register("markClear", [](std::string const&, void* u) { static_cast<MarkModule*>(u)->Clear(); }, this);
    Actions::Register("markTp",    [](std::string const&, void* u) { static_cast<MarkModule*>(u)->TeleportObjects(); }, this);
    Actions::Register("markOrbit", [](std::string const&, void* u) { static_cast<MarkModule*>(u)->OrbitObjects(); }, this);
    Actions::Register("markShape", [](std::string const& v, void* u) { static_cast<MarkModule*>(u)->Shape(v); }, this);

    // MODE. The C# module could render a shape LOCALLY or push it through VRChat's networking so
    // everyone sees it. Only the local half is ported: the networked renderer is a budgeted diff
    // against a measured send rate, and a half-done version of that is what floods an instance.
    Actions::Register("markMode", [](std::string const& v, void*) {
        static bool said = false;
        if (v == "vrchat" && !said) {
            said = true;
            Log::Warn("[Mark] 'vrchat' mode (everyone sees) is not ported yet -- local render.");
        }
        ScreenUI::Toast(v == "vrchat" ? "Mark: network mode not ported yet, local render"
                                      : "Mark : rendu local");
    });

    // WHAT THE CLIENT SHOWS. markMode and markNet state the ported truth rather than the C#
    // module's: only the LOCAL renderer exists here, so the card says local and the network line
    // says why there is nothing to report instead of sitting empty and looking broken.
    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<MarkModule*>(u);
        out += ",\"mark\":"     + Json::Bool(m->HasMark());
        out += ",\"markArt\":"  + Json::Bool(false);
        out += ",\"markMode\":" + Json::Str("local");
        out += ",\"markNet\":"  + Json::Str(
            m->HasMark() ? std::to_string(m->m_held.size()) + " objet(s) tenu(s) -- rendu local"
                         : std::string("no anchor placed"));
    }, this);

    m_enabled = true;       // costs nothing until a mark is placed
}

void MarkModule::SetMark(float const pos[3]) {
    if (!Unity::IsAlive(m_marker)) m_marker = MakeSphere();
    if (!m_marker) { Log::Warn("[Mark] could not create the anchor."); return; }
    m_pos[0] = pos[0]; m_pos[1] = pos[1]; m_pos[2] = pos[2];
    Unity::SetPosition(Unity::Transform(m_marker), m_pos);
    Unity::SetActive(m_marker, true);
}

void MarkModule::Put() {
    float o[3], d[3];
    if (!CameraRay(o, d)) { ScreenUI::Toast("Mark: no camera"); return; }

    // Where you are LOOKING. Nothing hit means three metres ahead, which is still a usable anchor
    // in an open space rather than a refusal.
    float p[3] = { o[0] + d[0] * 3.f, o[1] + d[1] * 3.f, o[2] + d[2] * 3.f };
    RayHit(o, d, 80.f, p);
    SetMark(p);
    Log::Writef("Info", "[Mark] anchor placed at %.2f %.2f %.2f", static_cast<double>(p[0]),
                static_cast<double>(p[1]), static_cast<double>(p[2]));
    ScreenUI::Toast("Anchor placed");
}

void MarkModule::Restore() {
    for (auto const& h : m_held) {
        if (!Unity::IsAlive(h.transform)) continue;
        if (h.body && Unity::IsAlive(h.body)) {
            void* setKin = Il2::FindMethod("UnityEngine.Rigidbody", "set_isKinematic", 1);
            void* setGrv = Il2::FindMethod("UnityEngine.Rigidbody", "set_useGravity", 1);
            if (setGrv) { bool g = h.wasGravity;   void* a[1] = { &g }; Il2::Invoke(setGrv, h.body, a); }
            if (setKin) { bool k = h.wasKinematic; void* a[1] = { &k }; Il2::Invoke(setKin, h.body, a); }
        }
        Unity::SetPosition(h.transform, h.pos);
    }
    if (!m_held.empty())
        Log::Writef("Info", "[Mark] %zu objet(s) remis ou ils etaient.", m_held.size());
    m_held.clear();
}

void MarkModule::Clear() {
    Restore();
    if (Unity::IsAlive(m_marker)) Unity::Destroy(m_marker);
    m_marker = nullptr;
    Log::Info("[Mark] anchor removed.");
    ScreenUI::Toast("Anchor removed");
}

void MarkModule::OnDisable() { Clear(); }

std::vector<HeldObject> MarkModule::Gather(bool freeze) {
    std::vector<HeldObject> out;
    int want = m_count > 0 ? m_count : 800;
    for (auto const& f : WorldObjects::Collect(m_pos, m_range, want, false)) {
        if (!Unity::IsAlive(f.transform)) continue;
        HeldObject h;
        h.transform  = f.transform;
        h.gameObject = Unity::GameObjectOf(f.transform);
        Unity::GetPosition(f.transform, h.pos);
        h.body = Unity::GetComponent(h.gameObject, "UnityEngine.Rigidbody");
        if (!Unity::IsAlive(h.body)) h.body = nullptr;
        if (h.body && freeze) {
            void* getKin = Il2::FindMethod("UnityEngine.Rigidbody", "get_isKinematic", 0);
            void* setKin = Il2::FindMethod("UnityEngine.Rigidbody", "set_isKinematic", 1);
            void* getGrv = Il2::FindMethod("UnityEngine.Rigidbody", "get_useGravity", 0);
            void* setGrv = Il2::FindMethod("UnityEngine.Rigidbody", "set_useGravity", 1);
            if (getKin) {
                void* r = Il2::Invoke(getKin, h.body, nullptr);
                h.wasKinematic = r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10);
            }
            if (getGrv) {
                void* r = Il2::Invoke(getGrv, h.body, nullptr);
                h.wasGravity = r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10);
            }
            // Frozen so physics stops dragging a shape apart the instant it is built.
            if (setKin) { bool k = true;  void* a[1] = { &k }; Il2::Invoke(setKin, h.body, a); }
            if (setGrv) { bool g = false; void* a[1] = { &g }; Il2::Invoke(setGrv, h.body, a); }
        }
        out.push_back(h);
    }
    return out;
}

void MarkModule::TeleportObjects() {
    if (!HasMark()) { ScreenUI::Toast("Place an anchor first"); return; }
    Restore();
    m_held = Gather(false);                 // left free: they are meant to fall into a pile
    if (m_held.empty()) {
        Log::Warn("[Mark] no loose object in this world.");
        ScreenUI::Toast("No loose objects in range");
        return;
    }
    for (size_t i = 0; i < m_held.size(); ++i) {
        float a = static_cast<float>(i) * 2.39996f;      // the golden angle: an even pile
        float r = 0.35f + 0.09f * std::sqrt(static_cast<float>(i));
        float p[3] = { m_pos[0] + std::cos(a) * r,
                       m_pos[1] + 0.15f + 0.12f * static_cast<float>(i % 4),
                       m_pos[2] + std::sin(a) * r };
        Unity::SetPosition(m_held[i].transform, p);
    }
    Log::Writef("Info", "[Mark] %zu objet(s) teleporte(s) a l'ancre.", m_held.size());
    ScreenUI::Toast(std::to_string(m_held.size()) + " objet(s) a l'ancre");
}

void MarkModule::OrbitObjects() {
    if (!HasMark()) { ScreenUI::Toast("Place an anchor first"); return; }
    // Handed to the orbit module rather than reimplemented: one ring, one ledger, one restore.
    // It orbits a PLAYER by user id, so the mark lends it the local player as the centre and the
    // objects circle where you are standing -- which is where you just put the anchor.
    Log::Info("[Mark] orbit around the anchor -> Object Orbit module.");
    for (Module* m : Engine::Modules())
        if (m->Name() == "Object Orbit") { m->SetEnabled(true); break; }
    if (void* api = Player::LocalApi()) {
        for (auto const& p : Player::All())
            if (p.isLocal) { Actions::Dispatch("objectOrbit", p.userId); break; }
        (void)api;
    }
}

void MarkModule::Shape(std::string const& kind) {
    if (!HasMark()) { ScreenUI::Toast("Place an anchor first"); return; }
    Restore();
    m_held = Gather(true);
    if (m_held.empty()) {
        Log::Warn("[Mark] no loose object in this world.");
        ScreenUI::Toast("No loose objects in range");
        return;
    }

    float right[3] = { 1, 0, 0 }, up[3] = { 0, 1, 0 };
    FacingAxes(right, up);

    int n = static_cast<int>(m_held.size());
    // Sized from how many objects there are when no size is set, so a handful makes a small shape
    // and a hundred makes a big one instead of all of them piling into the same metre.
    float size = m_artSize > 0.f ? m_artSize
                                 : (std::max)(1.f, static_cast<float>(n) * 0.35f * 1.05f / kPi);

    std::string k = kind.empty() ? "ring" : kind;
    for (char& ch : k) if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');
    auto pts = ShapePoints(k, n, size, right, up);

    for (size_t i = 0; i < m_held.size() && i < pts.size(); ++i) {
        float p[3] = { m_pos[0] + pts[i][0], m_pos[1] + pts[i][1], m_pos[2] + pts[i][2] };
        Unity::SetPosition(m_held[i].transform, p);
    }
    Log::Writef("Info", "[Mark] shape '%s' with %d object(s), size %.1f.", k.c_str(), n,
                static_cast<double>(size));
    ScreenUI::Toast("Shape '" + k + "' : " + std::to_string(n) + " objet(s)");
}

void MarkModule::OnUpdate() {
    // Nothing per-frame: a shape is placed once and the objects are frozen there. Only the ledger
    // is swept, so a prop the world destroyed under us is not written to again.
    if (m_held.empty()) return;
    m_held.erase(std::remove_if(m_held.begin(), m_held.end(),
                                [](HeldObject const& h) { return !Unity::IsAlive(h.transform); }),
                 m_held.end());
}

}
