#include "features.hpp"
#include "config.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstdio>
#include <string>
#include <utility>

namespace VRCA {

// ================================================================================ MOVEMENT
MovementModule::MovementModule()
    : Module("Movement", "walk, run, strafe and jump -- per channel, the world keeps the rest")
{
    Config::Bool ("Movement", "Walk",        "hold the walk and strafe speed", &m_walkOn);
    Config::Float("Movement", "WalkSpeed",   "walk speed",  &m_walk,   0.f, 20.f);
    Config::Float("Movement", "StrafeSpeed", "strafe speed",  &m_strafe, 0.f, 20.f);
    Config::Bool ("Movement", "Run",         "hold the run speed",              &m_runOn);
    Config::Float("Movement", "RunSpeed",    "run speed",  &m_run,    0.f, 40.f);
    Config::Bool ("Movement", "Jump",        "hold the jump impulse",               &m_jumpOn);
    Config::Float("Movement", "JumpImpulse", "jump impulse -- 0 hands control back to the world",
                  &m_jump, 0.f, 20.f);

    // The client's force-jump button sets the impulse and switches the channel on in one go.
    Actions::Register("forceJump", [](std::string const& v, void* u) {
        auto* m = static_cast<MovementModule*>(u);
        float f = v.empty() ? 0.f : static_cast<float>(atof(v.c_str()));
        m->m_jump   = (f > 0.f) ? f : 8.f;
        m->m_jumpOn = true;
        m->SetEnabled(true);
        Log::Writef("Info", "[Movement] forceJump -> impulsion %.1f", static_cast<double>(m->m_jump));
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<MovementModule*>(u);
        out += ",\"forceJump\":" + Json::Bool(m->Enabled() && m->m_jumpOn);
    }, this);
}

void MovementModule::Capture(void* api) {
    m_origWalk   = Player::GetWalkSpeed(api);
    m_origStrafe = Player::GetStrafeSpeed(api);
    m_origRun    = Player::GetRunSpeed(api);
    m_origJump   = Player::GetJumpImpulse(api);
    m_captured   = true;
}

void MovementModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_nextApply) return;
    m_nextApply = now + 0.1;      // ten times a second

    // Fly parks locomotion at zero on purpose, so that it does not play strafe walks mid-air.
    for (Module* m : Engine::Modules())
        if (m->Name() == "Fly" && m->Enabled()) return;

    // Nothing on and nothing left to undo: not one il2cpp call.
    if (m_captured && !m_walkOn && !m_runOn && !m_jumpOn && !m_wasWalk && !m_wasRun && !m_wasJump)
        return;

    void* api = Player::LocalApi();
    if (!api) return;             // transient around a respawn -- NOT a reason to re-capture
    if (!m_captured) Capture(api);

    if (m_walkOn) {
        Player::SetWalkSpeed(api, m_walk);
        Player::SetStrafeSpeed(api, m_strafe);
    } else if (m_wasWalk) {       // the one frame the switch went off: undo our override, then stop
        Player::SetWalkSpeed(api, m_origWalk);
        Player::SetStrafeSpeed(api, m_origStrafe);
    }

    if (m_runOn)        Player::SetRunSpeed(api, m_run);
    else if (m_wasRun)  Player::SetRunSpeed(api, m_origRun);

    bool jump = m_jumpOn && m_jump > 0.f;
    if (jump)            Player::SetJumpImpulse(api, m_jump);
    else if (m_wasJump)  Player::SetJumpImpulse(api, m_origJump);

    m_wasWalk = m_walkOn;
    m_wasRun  = m_runOn;
    m_wasJump = jump;
}

void MovementModule::OnDisable() {
    // Back to the WORLD's values, not to VRChat's stock numbers: writing 2/4/3 here would overwrite
    // a low-gravity world's own locomotion and leave it broken for the rest of the session.
    if (!m_captured) return;
    if (void* api = Player::LocalApi()) {
        if (m_wasWalk) { Player::SetWalkSpeed(api, m_origWalk); Player::SetStrafeSpeed(api, m_origStrafe); }
        if (m_wasRun)  Player::SetRunSpeed(api, m_origRun);
        if (m_wasJump) Player::SetJumpImpulse(api, m_origJump);
    }
    m_wasWalk = m_wasRun = m_wasJump = false;
}

// ================================================================================ FLY
namespace {
    // The camera's forward/right, so flying goes where the player is looking rather than along
    // world axes. Falls back to world axes when the camera cannot be read.
    bool CameraAxes(Player::Vec3& fwd, Player::Vec3& right) {
        static void* mainCam = Il2::FindMethod("UnityEngine.Camera", "get_main", 0);
        static void* getTr   = Il2::FindMethod("UnityEngine.Component", "get_transform", 0);
        static void* getFwd  = Il2::FindMethod("UnityEngine.Transform", "get_forward", 0);
        static void* getRt   = Il2::FindMethod("UnityEngine.Transform", "get_right", 0);
        if (!mainCam || !getTr || !getFwd || !getRt) return false;
        void* cam = Il2::Invoke(mainCam, nullptr, nullptr);
        if (!cam) return false;
        void* tr = Il2::Invoke(getTr, cam, nullptr);
        if (!tr) return false;
        void* f = Il2::Invoke(getFwd, tr, nullptr);
        void* r = Il2::Invoke(getRt, tr, nullptr);
        if (!f || !r) return false;
        auto const* ff = reinterpret_cast<float const*>(static_cast<char*>(f) + 0x10);
        auto const* rr = reinterpret_cast<float const*>(static_cast<char*>(r) + 0x10);
        fwd   = { ff[0], ff[1], ff[2] };
        right = { rr[0], rr[1], rr[2] };
        return true;
    }

    bool Key(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
}

FlyModule::FlyModule()
    : Module("Fly", "free flight: WASD camera, E up, Q down, Shift for fast, Ctrl+F")
{
    Config::Float("Fly", "Speed",      "flight speed",            &m_speed,      0.5f, 40.f);
    Config::Float("Fly", "BoostSpeed", "speed with Shift",        &m_boostSpeed, 0.5f, 80.f);
    Config::Bool ("Fly", "Noclip",     "pass through geometry while flying", &m_noclip);

    // NOT "ghost": that is the network serializer (see GhostModule), and wiring it here left the
    // real feature unreachable. Fly has no client action of its own yet -- it is the QuickMenu
    // card and Ctrl+F.
}

void FlyModule::OnEnable() {
    // Anchor on the spot we are standing on, so switching fly on floats instead of dropping.
    if (void* root = Player::LocalRoot()) {
        Unity::GetPosition(root, m_hold);
        m_hasHold = true;
    }
    // Park the locomotion speeds. VRChat's animator reads them, and left alone it plays strafe
    // walk/run while we fly, which twists the spine and pushes the collar into the camera.
    if (void* api = Player::LocalApi()) {
        m_savedWalk   = Player::GetWalkSpeed(api);
        m_savedStrafe = Player::GetStrafeSpeed(api);
        m_savedRun    = Player::GetRunSpeed(api);
        Player::SetWalkSpeed(api, 0.f);
        Player::SetStrafeSpeed(api, 0.f);
        Player::SetRunSpeed(api, 0.f);
        m_speedsSuppressed = true;
    }
    if (m_noclip) SetNoclip(true);
    Log::Info("[Fly] ON");
}

void FlyModule::OnDisable() {
    m_hasHold = false;
    if (m_speedsSuppressed) {
        m_speedsSuppressed = false;
        if (void* api = Player::LocalApi()) {
            // A zero here would leave the player unable to walk, so fall back to VRChat's defaults
            // if what we captured was already zero (fly switched on twice in a row).
            Player::SetWalkSpeed(api,   m_savedWalk   > 0.1f ? m_savedWalk   : 2.f);
            Player::SetStrafeSpeed(api, m_savedStrafe > 0.1f ? m_savedStrafe : 2.f);
            Player::SetRunSpeed(api,    m_savedRun    > 0.1f ? m_savedRun    : 4.f);
        }
    }
    SetNoclip(false);   // leaving fly must bring collision back
    Log::Info("[Fly] OFF");
}

void FlyModule::OnUpdate() {
    void* api  = Player::LocalApi();
    void* root = Player::LocalRoot();
    if (!api || !root) return;

    Player::ZeroVelocity(api);          // gravity integrates velocity: keep it at zero

    // Hold the animator's locomotion at zero -- a world or an avatar load writes the speeds back.
    if (m_speedsSuppressed &&
        (Player::GetWalkSpeed(api) > 0.01f || Player::GetStrafeSpeed(api) > 0.01f ||
         Player::GetRunSpeed(api)  > 0.01f)) {
        Player::SetWalkSpeed(api, 0.f);
        Player::SetStrafeSpeed(api, 0.f);
        Player::SetRunSpeed(api, 0.f);
    }

    Player::Vec3 fwd{ 0, 0, 1 }, right{ 1, 0, 0 };
    CameraAxes(fwd, right);

    float dx = 0, dy = 0, dz = 0;
    auto add = [&](Player::Vec3 const& v, float sc) { dx += v.x * sc; dy += v.y * sc; dz += v.z * sc; };
    if (Key('W')) add(fwd,    1.f);
    if (Key('S')) add(fwd,   -1.f);
    if (Key('D')) add(right,  1.f);
    if (Key('A')) add(right, -1.f);
    if (Key('E')) dy += 1.f;
    if (Key('Q')) dy -= 1.f;

    double now = Engine::Time();
    float dt = (m_lastTick > 0.0) ? static_cast<float>(now - m_lastTick) : 0.016f;
    if (dt > 0.1f) dt = 0.1f;           // a hitch must not fling us across the map
    m_lastTick = now;

    float len = std::sqrt(dx * dx + dy * dy + dz * dz);
    if (len > 0.001f) {
        float step = (Key(VK_SHIFT) ? m_boostSpeed : m_speed) * dt / len;
        float p[3];
        if (Unity::GetPosition(root, p)) {
            p[0] += dx * step; p[1] += dy * step; p[2] += dz * step;
            Unity::SetPosition(root, p);
            m_hold[0] = p[0]; m_hold[1] = p[1]; m_hold[2] = p[2];
            m_hasHold = true;
        }
    } else if (m_hasHold) {
        // Nothing held: put us back where we were, so no external force can drift us down.
        Unity::SetPosition(root, m_hold);
    } else {
        Unity::GetPosition(root, m_hold);
        m_hasHold = true;
    }

    if (m_noclipOn) ReassertNoclip();
}

// NOCLIP -- the local player's solid colliders, switched off and REMEMBERED.
//
// CharacterController derives from Collider, so one scan covers the capsule and the controller.
// Triggers are left alone: they are world interaction zones, not collision.
void FlyModule::SetNoclip(bool on) {
    if (on == m_noclipOn) return;

    if (!on) {
        int restored = 0;
        for (auto const& h : m_disabled) {
            if (!Unity::IsAlive(h.collider)) continue;
            Unity::SetBehaviourEnabled(h.collider, h.wasEnabled);
            ++restored;
        }
        m_disabled.clear();
        m_noclipOn = false;
        if (restored) Log::Writef("Info", "[Fly] noclip OFF -- %d collider(s) remis.", restored);
        return;
    }

    void* root = Player::LocalRoot();
    if (!root) return;
    m_disabled.clear();
    for (void* c : Unity::ComponentsInChildren(Unity::GameObjectOf(root), "UnityEngine.Collider", true)) {
        if (!Unity::IsAlive(c) || Unity::ColliderIsTrigger(c)) continue;
        m_disabled.push_back({ c, Unity::BehaviourEnabled(c) });
        Unity::SetBehaviourEnabled(c, false);
    }
    m_noclipOn = true;
    Log::Writef(m_disabled.empty() ? "Warning" : "Info",
                m_disabled.empty() ? "[Fly] noclip ON -- NO collider found, it will do nothing."
                                   : "[Fly] noclip ON -- %d collider(s) desactive(s).",
                static_cast<int>(m_disabled.size()));
}

// VRChat re-enables the player's colliders on avatar load and on locomotion resets, so the state
// has to be re-asserted. A dead entry means the rig was rebuilt: rescan, but put the survivors
// back FIRST -- the ledger is the only record of what we switched off, and dropping it while they
// are still disabled would leave the player walking through walls with noclip supposedly off.
void FlyModule::ReassertNoclip() {
    bool stale = m_disabled.empty();
    for (auto const& h : m_disabled) {
        if (!Unity::IsAlive(h.collider)) { stale = true; break; }
        if (Unity::BehaviourEnabled(h.collider)) Unity::SetBehaviourEnabled(h.collider, false);
    }
    if (!stale) return;

    for (auto const& h : m_disabled)
        if (Unity::IsAlive(h.collider)) Unity::SetBehaviourEnabled(h.collider, h.wasEnabled);
    m_disabled.clear();
    m_noclipOn = false;
    SetNoclip(true);
}

// ================================================================================ ESP
namespace {
    // THE OFFICIAL VRCHAT RANK COLOURS, plus a bright red for the VRChat Team rank. The colour IS
    // the information here: it is how you read a room at a glance without stopping to read names.
    struct Rgb { float r, g, b; };
    Rgb TrustColour(std::string const& trust) {
        if (trust == "Visitor")       return { 0.84f, 0.84f, 0.88f };   // light grey
        if (trust == "New User")      return { 0.09f, 0.47f, 1.00f };   // blue
        if (trust == "User")          return { 0.17f, 0.81f, 0.36f };   // green
        if (trust == "Known User")    return { 1.00f, 0.48f, 0.26f };   // orange
        if (trust == "Trusted User")  return { 0.51f, 0.26f, 0.90f };   // purple
        if (trust == "Veteran User")  return { 0.51f, 0.26f, 0.90f };
        if (trust == "Nuisance")      return { 0.55f, 0.00f, 0.00f };   // dark red
        if (trust == "VRChat User")   return { 1.00f, 0.12f, 0.12f };   // VRChat Team
        return { 1.f, 1.f, 1.f };
    }
}

EspModule::EspModule()
    : Module("ESP", "a rank-coloured label above each player, drawn with VRChat's UI") {
    Config::Bool ("ESP", "ShowDistance", "show the distance next to the name", &m_showDistance);
    Config::Float("ESP", "MaxDistance",  "draw nothing beyond this -- 0 = no limit",
                  &m_maxDistance, 0.f, 500.f);
    Config::Int  ("ESP", "Cap",
                  "maximum players drawn, nearest first", &m_cap, 1, 80);

    // "hud" is the client's single switch over everything the mod draws on screen.
    Actions::Register("hud", [](std::string const& v, void* u) {
        auto* m = static_cast<EspModule*>(u);
        m->SetEnabled(v == "on" ? true : (v == "off" ? false : !m->Enabled()));
        Log::Writef("Info", "[ESP] hud -> %s", m->Enabled() ? "ON" : "OFF");
    }, this);
}

void EspModule::OnDisable() {
    for (auto const& kv : m_labels) ScreenUI::ReleaseLabel(kv.second);
    m_labels.clear();
}

void EspModule::OnUpdate() {
    if (!ScreenUI::Ready()) return;

    void* localApi = Player::LocalApi();
    Player::Vec3 me = localApi ? Player::Position(localApi) : Player::Vec3{};

    // NEAREST-N. ESP draws every frame, so it cannot be throttled in time without the labels
    // flickering; the cap is on COUNT instead. The per-player projection is what froze the main
    // thread in the C# mod at 30 players (467 ms/s), and a freeze that deep drops you from the
    // instance to your home. Anyone past the cap is distant clutter.
    struct Near { Player::Info const* p; float dist; };
    std::vector<Near> closest;        // not `near`: windef.h defines that as a macro
    auto all = Player::All();
    closest.reserve(all.size());
    for (auto const& p : all) {
        if (p.isLocal) continue;
        float dx = p.pos.x - me.x, dy = p.pos.y - me.y, dz = p.pos.z - me.z;
        float d = std::sqrt(dx * dx + dy * dy + dz * dz);
        if (m_maxDistance > 0.f && d > m_maxDistance) continue;
        closest.push_back({ &p, d });
    }
    std::sort(closest.begin(), closest.end(),
              [](Near const& a, Near const& b) { return a.dist < b.dist; });
    if (static_cast<int>(closest.size()) > m_cap) closest.resize(static_cast<size_t>(m_cap));

    std::vector<std::pair<int,int>> next;
    next.reserve(closest.size());

    for (auto const& n : closest) {
        Player::Info const& p = *n.p;

        // Project a point above the head, not the feet: a label at the feet reads as belonging to
        // whatever is in front of the player.
        Player::Vec3 head{ p.pos.x, p.pos.y + m_headOffset, p.pos.z };
        bool onScreen = false;
        Player::Vec3 s = ScreenUI::WorldToScreen(head, onScreen);
        if (!onScreen) continue;

        // Reuse this player's label across frames so the pool does not churn.
        int handle = ScreenUI::kNoLabel;
        for (auto const& kv : m_labels) if (kv.first == p.id) { handle = kv.second; break; }
        if (handle == ScreenUI::kNoLabel) handle = ScreenUI::AcquireLabel();
        if (handle == ScreenUI::kNoLabel) continue;

        std::string text = p.name;
        if (!p.platform.empty()) text += " [" + p.platform + "]";
        if (m_showDistance) {
            char b[32];
            std::snprintf(b, sizeof(b), "  %.0fm", static_cast<double>(n.dist));
            text += b;
        }
        Rgb c = TrustColour(p.trust);
        ScreenUI::SetText(handle, text);
        ScreenUI::SetColor(handle, c.r, c.g, c.b, 1.f);
        ScreenUI::SetScreenPos(handle, s.x, s.y);
        ScreenUI::SetVisible(handle, true);
        next.emplace_back(p.id, handle);
    }

    // Players that went away (or off screen) give their label back.
    for (auto const& kv : m_labels) {
        bool kept = false;
        for (auto const& n : next) if (n.second == kv.second) { kept = true; break; }
        if (!kept) ScreenUI::ReleaseLabel(kv.second);
    }
    m_labels.swap(next);
}


// =============================================================================== GRAVITY

GravityModule::GravityModule()
    : Module("Gravity", "cut your gravity (and, optionally, the world objects')")
{
    Config::Bool("Gravity", "Player", "cut YOUR gravity -- only you float", &m_playerOff);
    Config::Bool("Gravity", "World",
                 "cut the world objects' gravity ON YOUR SIDE -- may break elevators and puzzles",
                 &m_worldOff);
}

void GravityModule::OnDisable() {
    if (m_playerApplied) {
        if (void* api = Player::LocalApi()) {
            Player::SetGravityStrength(api, m_originalPlayer);
            m_playerApplied = false;
        }
        // If the api was not there, the flag STAYS set: OnUpdate retries until the restore lands.
    }
    if (m_worldApplied) {
        static void* setG = Il2::FindMethod("UnityEngine.Physics", "set_gravity", 1);
        if (setG) {
            float v[3] = { m_originalWorld[0], m_originalWorld[1], m_originalWorld[2] };
            void* a[1] = { v };
            Il2::Invoke(setG, nullptr, a);
        }
        m_worldApplied = false;
    }
}

void GravityModule::OnUpdate() {
    void* api = Player::LocalApi();

    if (m_playerOff) {
        if (api && !m_playerApplied) {
            m_originalPlayer = Player::GetGravityStrength(api);
            // A world already at zero would otherwise be "restored" to zero later.
            if (m_originalPlayer <= 0.001f) m_originalPlayer = 1.f;
            Player::SetGravityStrength(api, 0.f);
            m_playerApplied = true;
            Log::Info("[Gravity] your gravity is cut -- nobody else sees you float.");
        }
    } else if (m_playerApplied && api) {
        Player::SetGravityStrength(api, m_originalPlayer);
        m_playerApplied = false;
        Log::Info("[Gravity] your gravity is restored.");
    }

    static void* getG = Il2::FindMethod("UnityEngine.Physics", "get_gravity", 0);
    static void* setG = Il2::FindMethod("UnityEngine.Physics", "set_gravity", 1);
    if (!getG || !setG) return;

    if (m_worldOff && !m_worldApplied) {
        void* boxed = Il2::Invoke(getG, nullptr, nullptr);
        if (boxed) {
            auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
            m_originalWorld[0] = f[0]; m_originalWorld[1] = f[1]; m_originalWorld[2] = f[2];
        }
        float z[3] = { 0, 0, 0 };
        void* a[1] = { z };
        Il2::Invoke(setG, nullptr, a);
        m_worldApplied = true;
        Log::Warn("[Gravity] WORLD gravity cut on your side -- elevators and puzzles may break.");
    } else if (!m_worldOff && m_worldApplied) {
        float v[3] = { m_originalWorld[0], m_originalWorld[1], m_originalWorld[2] };
        void* a[1] = { v };
        Il2::Invoke(setG, nullptr, a);
        m_worldApplied = false;
        Log::Info("[Gravity] world gravity restored.");
    }
}

// ================================================================================ SELF HIDE
SelfHideModule::SelfHideModule()
    : Module("Self Hide", "your avatar is no longer drawn on YOUR screen; others still see it") {}

namespace {
    void SetForceOff(void* renderer, bool on) {
        static void* m = Il2::FindMethod("UnityEngine.Renderer", "set_forceRenderingOff", 1);
        if (!m) return;
        bool v = on; void* a[1] = { &v };
        Il2::Invoke(m, renderer, a);
    }

    // THE AVATAR ROOT -- the object carrying VRCAvatarDescriptor. Falling back to the player
    // subtree would hide VRChat's own objects along with the avatar.
    void* AvatarRootUnder(void* playerGo) {
        if (!playerGo) return nullptr;
        void* d = Unity::GetComponentInChildren(
            playerGo, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", true);
        return d ? Unity::GameObjectOf(d) : nullptr;
    }
}

void SelfHideModule::RestoreAll() {
    for (auto const& h : m_held) {
        if (!Unity::IsAlive(h.renderer)) continue;
        SetForceOff(h.renderer, false);
        Unity::SetBehaviourEnabled(h.renderer, h.wasEnabled);
    }
    m_held.clear();
    m_known.clear();
    m_root = nullptr;
}

void SelfHideModule::OnDisable() {
    if (!m_held.empty())
        Log::Writef("Info", "[SelfHide] off -- %zu renderer(s) rendus a l'ecran.", m_held.size());
    RestoreAll();
}

int SelfHideModule::Take(void* rootGameObject) {
    int added = 0;
    for (void* r : Unity::ComponentsInChildren(rootGameObject, "UnityEngine.Renderer", true)) {
        if (!Unity::IsAlive(r)) continue;
        if (!m_known.insert(r).second) continue;
        m_held.push_back({ r, Unity::BehaviourEnabled(r) });
        Unity::SetBehaviourEnabled(r, false);
        SetForceOff(r, true);
        ++added;
    }
    return added;
}

void SelfHideModule::OnUpdate() {
    // THE LAST WORD BEFORE THE FRAME DRAWS. Whatever VRChat or the animator did, the renderers we
    // hold are off when the cameras look.
    for (size_t i = m_held.size(); i-- > 0; ) {
        void* r = m_held[i].renderer;
        if (!Unity::IsAlive(r)) {          // destroyed with the avatar: forget it, ledger and set
            m_known.erase(r);
            m_held.erase(m_held.begin() + static_cast<long long>(i));
            continue;
        }
        if (Unity::BehaviourEnabled(r)) Unity::SetBehaviourEnabled(r, false);
        SetForceOff(r, true);
    }

    double t = Engine::Time();
    if (t < m_next) return;
    m_next = t + 1.0;

    void* playerGo = Player::GameObjectOf(Player::LocalApi());
    if (!Unity::IsAlive(playerGo)) { RestoreAll(); return; }   // transient: put everything back

    void* root = AvatarRootUnder(playerGo);
    if (!Unity::IsAlive(root)) { RestoreAll(); return; }       // avatar still loading

    // A DIFFERENT AVATAR MEANS A DIFFERENT LEDGER. Restoring first keeps the old renderers from
    // being stranded invisible if they outlive the swap.
    if (root != m_root) { RestoreAll(); m_root = root; }

    int added = Take(root);

    // VRChat's mirror copy of the local avatar lives beside it under the player root and is a
    // separate set of renderers; without it you stay visible in every mirror.
    static void* trClass = nullptr;
    for (void* tr : Unity::ComponentsInChildren(playerGo, "UnityEngine.Transform", true)) {
        (void)trClass;
        if (!Unity::IsAlive(tr)) continue;
        std::string n = Unity::Name(Unity::GameObjectOf(tr));
        if (n.find("MirrorClone") == std::string::npos) continue;
        added += Take(Unity::GameObjectOf(tr));
    }

    if (added > 0)
        Log::Writef("Info", "[SelfHide] %zu renderer(s) caches (+%d), tenus hors ecran chaque frame.",
                    m_held.size(), added);
}

}
