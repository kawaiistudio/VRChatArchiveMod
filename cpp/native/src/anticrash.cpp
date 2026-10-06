#include "anticrash.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "unity.hpp"

#include <algorithm>
#include <cstring>

namespace VRCA {

namespace {
    // Components of a type under a root, inactive ones included. A type this build does not ship
    // simply yields nothing -- the vector is the natural "absent" answer, no special case needed.
    std::vector<void*> ChildrenOfType(void* rootGo, char const* fullName) {
        std::vector<void*> out;
        static void* getComps = Il2::FindMethod("UnityEngine.GameObject", "GetComponentsInChildren", 2);
        if (!rootGo || !getComps) return out;
        void* k = Il2::FindClass(fullName);
        if (!k) return out;
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!cgt || !tgo) return out;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* ty = reinterpret_cast<CGT>(cgt)(k);
        void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
        if (!sysType) return out;

        bool inc = true;
        void* a[2] = { sysType, &inc };
        void* arr = Il2::Invoke(getComps, rootGo, a);
        int n = Il2::ArrayLength(arr);
        out.reserve(static_cast<size_t>(n));
        for (int i = 0; i < n; ++i)
            if (void* c = Il2::ArrayAt(arr, i)) out.push_back(c);
        return out;
    }

    // The enabled flag, resolved on the component's REAL class. Cloth declares its own rather than
    // inheriting Behaviour's, so asking Behaviour for it would hand an icall the wrong object.
    void* EnabledSetter(void* comp, bool& found) {
        found = false;
        for (void* k = Il2::ClassOfObject(comp); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, "set_enabled", 1)) { found = true; return m; }
        return nullptr;
    }
    bool IsEnabledNow(void* comp) {
        for (void* k = Il2::ClassOfObject(comp); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, "get_enabled", 0)) {
                void* r = Il2::Invoke(m, comp, nullptr);
                return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10);
            }
        return true;
    }
    void SetEnabledNow(void* comp, void* setter, bool on) {
        if (!setter) return;
        bool v = on;
        void* a[1] = { &v };
        Il2::Invoke(setter, comp, a);
    }

    // The avatar root: the object carrying VRCAvatarDescriptor. No descriptor, no scan -- better
    // to clamp nothing than to clamp VRChat's own objects.
    void* AvatarRootOf(void* playerGo) {
        if (!playerGo) return nullptr;
        void* d = Unity::GetComponentInChildren(playerGo, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", true);
        return d ? Unity::GameObjectOf(d) : nullptr;
    }
}

AntiCrashModule::AntiCrashModule()
    : Module("Anti-Crash",
             "neutralises crasher-type components on avatars; everything is reversible")
{
    // The client's rescan button.
    Actions::Register("antiCrashRescan", [](std::string const&, void* u) {
        static_cast<AntiCrashModule*>(u)->m_next = 0.0;
        Log::Info("[AntiCrash] rescan requested by the client.");
    }, this);
}

bool AntiCrashModule::Journal(void* target, std::function<void()> undo) {
    // An identical clamp already held (a rescan found the same component) is not recorded twice,
    // or the undo would run twice and the second run would restore the CLAMPED value.
    if (!m_seen.insert(target).second) return false;
    m_journal.push_back({ target, std::move(undo) });
    return true;
}

void AntiCrashModule::RestoreAll() {
    // Newest first: two clamps on one component undo onto the true original.
    for (auto it = m_journal.rbegin(); it != m_journal.rend(); ++it) {
        if (!Unity::IsAlive(it->target)) continue;
        it->undo();
    }
    if (!m_journal.empty())
        Log::Writef("Info", "[AntiCrash] %zu clamp(s) annule(s) -- avatars remis comme trouves.",
                    m_journal.size());
    m_journal.clear();
    m_seen.clear();
    m_clamped = 0;
}

void AntiCrashModule::OnDisable() { RestoreAll(); }

void AntiCrashModule::Scan(void* avatarRoot) {
    if (!avatarRoot) return;
    std::string avatar = Unity::Name(avatarRoot);
    int clamped = 0;
    std::string tripped;
    auto trip = [&](char const* what, int n) {
        if (n <= 0) return;
        clamped += n;
        if (!tripped.empty()) tripped += ", ";
        tripped += what;
    };

    // Switches off everything past `max`, through each component's own enabled flag.
    auto byCount = [&](char const* type, int max) -> int {
        auto comps = ChildrenOfType(avatarRoot, type);
        if (static_cast<int>(comps.size()) <= max) return 0;
        int n = 0;
        for (size_t i = static_cast<size_t>(max); i < comps.size(); ++i) {
            void* c = comps[i];
            if (!Unity::IsAlive(c)) continue;
            bool has = false;
            void* setter = EnabledSetter(c, has);
            if (!has || !IsEnabledNow(c)) continue;      // no flag, or already off
            if (!Journal(c, [c, setter] { SetEnabledNow(c, setter, true); })) continue;
            SetEnabledNow(c, setter, false);
            ++n;
        }
        return n;
    };

    trip("Light",       byCount("UnityEngine.Light",       m_maxLights));
    trip("AudioSource", byCount("UnityEngine.AudioSource", m_maxAudioSources));
    trip("Cloth",       byCount("UnityEngine.Cloth",       m_maxCloth));
    trip("PhysBone",    byCount("VRC.Dynamics.VRCPhysBoneBase", m_maxPhysBones));

    {
        int half = m_maxContacts / 2;
        trip("Contact", byCount("VRC.Dynamics.ContactReceiver", half)
                      + byCount("VRC.Dynamics.ContactSender",   half));
    }

    // Trails and lines are dynamic meshes rebuilt every frame; they share one budget because the
    // cost is the same whichever type produced it.
    {
        int budget = m_maxTrails;
        int n = byCount("UnityEngine.TrailRenderer", budget);
        budget = (std::max)(0, budget - 1);
        n += byCount("UnityEngine.LineRenderer", budget);
        trip("Trail/Line", n);
    }

    // Constraint chains resolve serially against their sources every frame.
    {
        int n = 0, budget = m_maxConstraints;
        for (char const* t : { "VRC.Dynamics.VRCConstraintBase",
                               "UnityEngine.Animations.ParentConstraint",
                               "UnityEngine.Animations.PositionConstraint",
                               "UnityEngine.Animations.RotationConstraint",
                               "UnityEngine.Animations.ScaleConstraint",
                               "UnityEngine.Animations.AimConstraint",
                               "UnityEngine.Animations.LookAtConstraint" }) {
            n += byCount(t, budget);
            budget = (std::max)(0, budget - 1);
        }
        trip("Constraint", n);
    }

    // A ParticleSystem has no enabled flag of its own: stop and clear the ones past the budget.
    {
        static void* psStop  = Il2::FindMethod("UnityEngine.ParticleSystem", "Stop", 1);
        static void* psClear = Il2::FindMethod("UnityEngine.ParticleSystem", "Clear", 1);
        static void* psPlayStatic = Il2::FindMethod("UnityEngine.ParticleSystem", "Play", 1);
        void* psPlay = psPlayStatic;   // a static cannot be captured by a lambda
        auto systems = ChildrenOfType(avatarRoot, "UnityEngine.ParticleSystem");
        int n = 0;
        for (size_t i = static_cast<size_t>(m_maxParticleSystems); i < systems.size(); ++i) {
            void* ps = systems[i];
            if (!Unity::IsAlive(ps) || !psStop || !psClear) continue;
            if (!Journal(ps, [ps, psPlay] {
                    if (psPlay) { bool w = true; void* a[1] = { &w }; Il2::Invoke(psPlay, ps, a); }
                })) continue;
            bool w = true;
            void* a[1] = { &w };
            Il2::Invoke(psStop, ps, a);
            Il2::Invoke(psClear, ps, a);
            ++n;
        }
        trip("ParticleSystem", n);
    }

    // THE SCREEN-FILLING QUAD. A particle renderer with a huge maxParticleSize draws each particle
    // across the whole screen: a handful is an instant GPU stall, and it is the classic spawn
    // crasher. Capping the size leaves the effect visible and harmless.
    {
        static void* getMax = Il2::FindMethod("UnityEngine.ParticleSystemRenderer", "get_maxParticleSize", 0);
        static void* setMaxStatic = Il2::FindMethod("UnityEngine.ParticleSystemRenderer", "set_maxParticleSize", 1);
        void* setMax = setMaxStatic;   // a static cannot be captured by a lambda
        int n = 0;
        if (getMax && setMax) {
            for (void* r : ChildrenOfType(avatarRoot, "UnityEngine.ParticleSystemRenderer")) {
                if (!Unity::IsAlive(r)) continue;
                void* boxed = Il2::Invoke(getMax, r, nullptr);
                if (!boxed) continue;
                float cur = *reinterpret_cast<float*>(static_cast<char*>(boxed) + 0x10);
                if (cur <= m_maxParticleSize) continue;
                if (!Journal(r, [r, cur, setMax] {
                        float v = cur; void* a[1] = { &v };
                        Il2::Invoke(setMax, r, a);
                    })) continue;
                float v = m_maxParticleSize;
                void* a[1] = { &v };
                Il2::Invoke(setMax, r, a);
                ++n;
            }
        }
        trip("TailleParticule", n);
    }

    // A camera or projector on an avatar renders the scene a second time, every frame.
    trip("Camera", byCount("UnityEngine.Camera", 0) + byCount("UnityEngine.Projector", 0));

    if (clamped > 0) {
        m_clamped += clamped;
        m_last = avatar;
        Log::Writef("Warning", "[AntiCrash] %d element(s) neutralised on '%s' (%s).",
                    clamped, avatar.c_str(), tripped.c_str());
    }
}

void AntiCrashModule::OnUpdate() {
    double t = Engine::Time();
    if (t < m_next) return;
    m_next = t + static_cast<double>((std::max)(1, m_rescanSeconds));

    // Drop journal entries whose target is gone (avatar swapped, player left): they can never be
    // undone and would otherwise grow without bound.
    if (!m_journal.empty()) {
        for (auto const& c : m_journal)
            if (!Unity::IsAlive(c.target)) m_seen.erase(c.target);
        m_journal.erase(std::remove_if(m_journal.begin(), m_journal.end(),
                                       [](Clamp const& c) { return !Unity::IsAlive(c.target); }),
                        m_journal.end());
    }

    // Walk the PLAYERS: every avatar that matters hangs off one, so the work is bounded by player
    // count instead of by everything Unity has ever loaded.
    for (auto const& p : Player::All()) {
        if (!p.api) continue;
        void* go = Player::GameObjectOf(p.api);
        if (!Unity::IsAlive(go)) continue;
        Scan(AvatarRootOf(go));
    }
}

}
