#include "glowesp.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "unity.hpp"
#include "worldobjects.hpp"

#include <algorithm>
#include <cmath>

namespace VRCA {

namespace {
    // The official VRChat rank colours, so the glow carries the same information the labels do.
    struct Rgb { float r, g, b; };
    Rgb TrustGlow(std::string const& trust) {
        if (trust == "Visitor")      return { 0.84f, 0.84f, 0.88f };
        if (trust == "New User")     return { 0.09f, 0.47f, 1.00f };
        if (trust == "User")         return { 0.17f, 0.81f, 0.36f };
        if (trust == "Known User")   return { 1.00f, 0.48f, 0.26f };
        if (trust == "Trusted User") return { 0.51f, 0.26f, 0.90f };
        if (trust == "Veteran User") return { 0.51f, 0.26f, 0.90f };
        if (trust == "Nuisance")     return { 0.55f, 0.00f, 0.00f };
        return { 1.f, 1.f, 1.f };
    }

    void* AvatarRootUnder(void* playerGo) {
        if (!playerGo) return nullptr;
        void* d = Unity::GetComponentInChildren(
            playerGo, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", true);
        return d ? Unity::GameObjectOf(d) : nullptr;
    }
}

GlowEspModule::GlowEspModule()
    : Module("Glow ESP", "make players' real mesh glow using the game's own effect")
{
    Config::Bool ("Glow ESP", "Players", "make players glow", &m_players);
    Config::Bool ("Glow ESP", "Pickups", "also make pickupable objects glow", &m_pickups);
    Config::Float("Glow ESP", "MaxDistance", "glow nothing beyond this -- 0 = no limit",
                  &m_maxDistance, 0.f, 500.f);
}

// FOUND IS NOT THE SAME AS RUNNING. The effect can be present but DISABLED -- VRChat switches it
// off with certain graphics settings -- and a disabled post-effect draws nothing while every call
// into it still succeeds silently. So it is switched on explicitly once we hold it.
bool GlowEspModule::Resolve() {
    if (m_fx && Unity::IsAlive(m_fx) && m_add) return true;

    // NOT YET IS NOT NEVER: the effect does not exist during loading, so early misses are normal
    // and the hunt is rate-limited rather than given up on.
    double now = Engine::Time();
    if (now < m_nextResolve) return false;
    m_nextResolve = now + 1.0;

    void* klass = Il2::FindClass("HighlightsFX");       // alias posed by the recovery pass
    if (!klass) return false;

    if (!m_add) {
        void* it = nullptr;
        while (void* m = Il2::NextMethod(klass, &it)) {
            if (Il2::MethodParamCount(m) != 3) continue;
            void* p0 = Il2::MethodParamClass(m, 0);
            if (!p0) continue;
            char const* n0 = Il2::ClassName(p0);
            if (!n0 || std::strcmp(n0, "Renderer") != 0) continue;
            m_add = m;
            break;
        }
    }
    if (!m_add) return false;

    // On the main camera first -- that is where VRChat puts it -- then anywhere in the scene.
    static void* mainCam = Il2::FindMethod("UnityEngine.Camera", "get_main", 0);
    if (mainCam) {
        void* cam = Il2::Invoke(mainCam, nullptr, nullptr);
        if (Unity::IsAlive(cam)) {
            void* go = Unity::GameObjectOf(cam);
            m_fx = Unity::GetComponent(go, "HighlightsFX");
            if (!Unity::IsAlive(m_fx)) m_fx = Unity::GetComponentInChildren(go, "HighlightsFX", true);
        }
    }
    if (!Unity::IsAlive(m_fx)) m_fx = nullptr;
    if (!m_fx) return false;

    Unity::SetBehaviourEnabled(m_fx, true);
    Log::Info("[GlowEsp] HighlightsFX found on the main camera and enabled.");
    return true;
}

void GlowEspModule::Light(void* renderer, float r, float g, float b) {
    if (!m_add || !m_fx || !Unity::IsAlive(renderer)) return;
    float col[4] = { r, g, b, 1.f };      // Color is a by-value struct: pass its address
    bool on = true;
    void* a[3] = { renderer, col, &on };
    Il2::Invoke(m_add, m_fx, a);
    m_lit.push_back(renderer);
}

void GlowEspModule::Unlight(void* renderer) {
    if (!m_add || !m_fx || !Unity::IsAlive(renderer)) return;
    // The same call that lit it, with the effect's on/off flag FALSE. Every build has kept this
    // working, which is why it is the fallback the C# module settled on too.
    float col[4] = { 0, 0, 0, 0 };
    bool on = false;
    void* a[3] = { renderer, col, &on };
    Il2::Invoke(m_add, m_fx, a);
}

void GlowEspModule::UnlightAll() {
    for (void* r : m_lit) Unlight(r);
    m_lit.clear();
}

void GlowEspModule::OnDisable() {
    UnlightAll();
    m_nextSweep = 0.0;
    Log::Info("[GlowEsp] OFF -- tout eteint.");
}

void GlowEspModule::OnUpdate() {
    if (!Resolve()) return;

    // Lighting is not free and an avatar's renderer set does not change within a frame, so the
    // whole set is re-done a few times a second rather than every frame.
    double now = Engine::Time();
    if (now < m_nextSweep) return;
    m_nextSweep = now + 0.5;

    UnlightAll();

    void* localApi = Player::LocalApi();
    Player::Vec3 me = localApi ? Player::Position(localApi) : Player::Vec3{};

    if (m_players) {
        for (auto const& p : Player::All()) {
            if (p.isLocal) continue;
            if (m_maxDistance > 0.f) {
                float dx = p.pos.x - me.x, dy = p.pos.y - me.y, dz = p.pos.z - me.z;
                if (std::sqrt(dx * dx + dy * dy + dz * dz) > m_maxDistance) continue;
            }
            void* root = AvatarRootUnder(Player::GameObjectOf(p.api));
            if (!Unity::IsAlive(root)) continue;
            Rgb c = TrustGlow(p.trust);
            for (void* r : Unity::ComponentsInChildren(root, "UnityEngine.Renderer", false))
                Light(r, c.r, c.g, c.b);
        }
    }

    if (m_pickups) {
        float origin[3] = { me.x, me.y, me.z };
        float range = m_maxDistance > 0.f ? m_maxDistance : 60.f;
        for (auto const& f : WorldObjects::Collect(origin, range, 120, false)) {
            void* go = Unity::GameObjectOf(f.transform);
            for (void* r : Unity::ComponentsInChildren(go, "UnityEngine.Renderer", false))
                Light(r, 1.f, 0.85f, 0.2f);          // a warm amber, clearly not a player
        }
    }
}

}
