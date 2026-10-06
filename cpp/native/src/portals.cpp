#include "portals.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#include <cmath>
#include <cstring>
#include <string>

namespace VRCA {

namespace {
    constexpr double kPassInterval = 0.5;    // seconds between samples
    constexpr float  kBigLife      = 1.0e9f; // far past any session

    bool Finite(float v) { return !std::isnan(v) && !std::isinf(v); }

    // Every live component of a type, inactive ones included: a portal mid-fade is still a portal.
    std::vector<void*> AllOfClass(void* k) {
        std::vector<void*> out;
        static void* findAll = Il2::FindMethod("UnityEngine.Resources", "FindObjectsOfTypeAll", 1);
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
        for (int i = 0; i < n; ++i) if (void* c = Il2::ArrayAt(arr, i)) out.push_back(c);
        return out;
    }

    int InstanceSize(void* klass) {
        if (!klass) return 0;
        return static_cast<int>(*reinterpret_cast<unsigned int*>(static_cast<char*>(klass) + 0xF8));
    }

    // THE PORTAL CLASS IS RENAMED ON THIS BUILD.
    //
    // PortalInternal used to resolve by name and does not here, so the type is learned from a
    // portal that EXISTS: a dropped portal's GameObject still carries "Portal" in its name (VRChat
    // names the instance, not the class), and the component on it that is not a Unity type is the
    // one. Learned once and cached; until somebody drops a portal there is nothing to find and
    // nothing to do, which is the honest state rather than a warning every five seconds.
        void* PortalClass() {
        static void* cached = nullptr;
        if (cached) return cached;

        for (char const* n : { "PortalInternal", "VRC.PortalInternal", "PortalInternalDynamic" })
            if (void* k = Il2::FindClass(n)) { cached = k; return cached; }

        static void* findGo = Il2::FindMethod("UnityEngine.Resources", "FindObjectsOfTypeAll", 1);
        void* goClass = Il2::FindClass("UnityEngine.GameObject");
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!findGo || !goClass || !cgt || !tgo) return nullptr;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* ty = reinterpret_cast<CGT>(cgt)(goClass);
        void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
        if (!sysType) return nullptr;
        void* a[1] = { sysType };
        void* arr = Il2::Invoke(findGo, nullptr, a);
        int n = Il2::ArrayLength(arr);

        for (int i = 0; i < n; ++i) {
            void* go = Il2::ArrayAt(arr, i);
            if (!Unity::IsAlive(go)) continue;
            std::string name = Unity::Name(go);
            if (name.find("Portal") == std::string::npos) continue;

            for (void* comp : Unity::AllComponentsInChildren(go, true)) {
                if (!Unity::IsAlive(comp)) continue;
                void* k = Il2::ClassOfObject(comp);
                char const* ns = k ? Il2::ClassNamespace(k) : nullptr;
                // Unity's own components are not the portal; VRChat's live outside UnityEngine.
                if (ns && std::strncmp(ns, "UnityEngine", 11) == 0) continue;
                if (!k) continue;
                int size = InstanceSize(k);
                if (size < 0x80) continue;            // a portal carries real state, not two fields
                cached = k;
                Log::Writef("Info", "[Portal] portal class learned from an object '%s': '%s' (%d B).",
                            name.c_str(), Il2::ClassName(k), size);
                return cached;
            }
        }
        return nullptr;
    }

}

PortalModule::PortalModule()
    : Module("Infinite Portal", "your portals stay open, with no wait between them")
{
    Actions::Register("portalInfinite", [](std::string const&, void* u) {
        auto* m = static_cast<PortalModule*>(u);
        m->SetEnabled(!m->Enabled());
        // SAID PLAINLY: a dropped portal is networked, so an open one is open for the instance.
        ScreenUI::Toast(m->Enabled()
            ? "Infinite portals ON -- everyone sees them stay"
            : "Infinite portals OFF");
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<PortalModule*>(u);
        out += ",\"portalInfinite\":" + Json::Bool(m->Enabled());
        out += ",\"portalInfiniteStatus\":" + Json::Str(
            !m->Enabled() ? std::string("inactive")
            : m->m_pinned.empty() ? std::string("active -- no portal open yet")
            : std::to_string(m->m_pinned.size()) + " portail(s) maintenu(s) ouvert(s)");
    }, this);
}

void PortalModule::OnDisable() {
    int n = 0;
    for (auto const& p : m_pinned) {
        if (!Unity::IsAlive(p.portal) || p.capOff <= 0) continue;
        *reinterpret_cast<float*>(static_cast<char*>(p.portal) + p.capOff) = p.wasCap;
        ++n;
    }
    m_pinned.clear();
    m_prev.clear();
    if (n) Log::Writef("Info", "[Portal] %d portail(s) rendu(s) a leur duree normale.", n);
}

// ZERO THE DROP COOLDOWN. PortalManager keeps it in static float state; anything sitting in a
// plausible cooldown window is pushed to zero so the next drop is allowed at once.
void PortalModule::ClearCooldown() {
    void* mgr = Il2::FindClass("PortalManager");
    if (!mgr) mgr = Il2::FindClass("VRC.PortalManager");
    if (!mgr) return;

    static void* getStatic = Il2::Export("il2cpp_field_static_get_value");
    static void* setStatic = Il2::Export("il2cpp_field_static_set_value");
    if (!getStatic || !setStatic) return;
    using GSV = void(*)(void*, void*);
    using SSV = void(*)(void*, void*);

    void* it = nullptr;
    while (void* f = Il2::NextField(mgr, &it)) {
        if (!Il2::FieldIsStatic(f)) continue;
        void* fc = Il2::FieldClass(f);
        char const* fn = fc ? Il2::ClassName(fc) : nullptr;
        if (!fn || std::strcmp(fn, "Single") != 0) continue;
        float v = 0.f;
        reinterpret_cast<GSV>(getStatic)(f, &v);
        // A cooldown is a small positive number of seconds. Anything else is some other constant
        // and is left alone -- writing every static float of a manager is how a feature breaks it.
        if (!Finite(v) || v <= 0.f || v > 120.f) continue;
        float zero = 0.f;
        reinterpret_cast<SSV>(setStatic)(f, &zero);
    }
}

void PortalModule::ScanPortals() {
    void* klass = PortalClass();
    if (!klass) return;     // no portal in the world yet: nothing to find and nothing to do
    int size = InstanceSize(klass);
    if (size <= 0x20 || size > 0x4000) return;

    // The LEARNED class, never a name: this build renamed it.
    for (void* p : AllOfClass(klass)) {
        if (!Unity::IsAlive(p)) continue;

        bool already = false;
        for (auto const& q : m_pinned) if (q.portal == p) { already = true; break; }
        if (already) continue;

        // One pass over the object's own bytes, reading each 4-byte slot as a float.
        bool sawRising = false;
        int  capOff = -1;
        for (int off = 0x10; off + 4 <= size; off += 4) {
            float v = *reinterpret_cast<float*>(static_cast<char*>(p) + off);
            if (!Finite(v)) continue;

            unsigned long long key =
                (reinterpret_cast<unsigned long long>(p) << 16) ^ static_cast<unsigned long long>(off);
            auto prev = m_prev.find(key);
            if (prev != m_prev.end()) {
                float d = v - prev->second;
                // Rose by roughly the time that passed, and stayed in a lifetime range: this is
                // the elapsed counter, which is what makes a nearby constant a CAP and not just a
                // number that happens to look like one.
                if (d > kPassInterval * 0.3 && d < kPassInterval * 4.0 && v >= 0.f && v < 600.f)
                    sawRising = true;
            }
            m_prev[key] = v;

            // A steady, near-whole value in a plausible portal-lifetime window.
            if (capOff < 0 && std::fabs(v - std::round(v)) < 0.01f && v >= 8.f && v <= 120.f)
                capOff = off;
        }

        if (!sawRising || capOff < 0) continue;      // no pairing: not a timer, leave it alone

        float was = *reinterpret_cast<float*>(static_cast<char*>(p) + capOff);
        *reinterpret_cast<float*>(static_cast<char*>(p) + capOff) = kBigLife;
        m_pinned.push_back({ p, capOff, was });
        if (!m_loggedLife) {
            m_loggedLife = true;
            Log::Writef("Info", "[Portal] timer found: cap @0x%X (was %.1f) pushed out of range.",
                        capOff, static_cast<double>(was));
        }
    }

    // Drop anything that died rather than keeping a dangling pointer in the undo ledger.
    for (auto it = m_pinned.begin(); it != m_pinned.end(); ) {
        if (Unity::IsAlive(it->portal)) ++it;
        else it = m_pinned.erase(it);
    }
}

void PortalModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_nextPass) return;
    m_nextPass = now + kPassInterval;
    ScanPortals();
    ClearCooldown();
}

}
