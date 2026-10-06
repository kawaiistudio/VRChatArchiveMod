#include "worldobjects.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#include <algorithm>
#include <string>
#include <unordered_set>

namespace VRCA::WorldObjects {

namespace {
    bool ContainsNoCase(std::string const& hay, char const* needle) {
        std::string h = hay, n = needle;
        for (char& c : h) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        for (char& c : n) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        return h.find(n) != std::string::npos;
    }

    // An avatar is not a prop. Walking up the parents and matching the names VRChat gives player
    // objects catches the whole subtree, including the local player's.
    bool IsPlayerOwned(void* transform) {
        int guard = 0;
        for (void* p = transform; p && guard++ < 64; p = Unity::Parent(p)) {
            std::string n = Unity::Name(Unity::GameObjectOf(p));
            if (ContainsNoCase(n, "player") || ContainsNoCase(n, "avatar") ||
                ContainsNoCase(n, "usr_"))
                return true;
        }
        return false;
    }

    // Anything the mod itself built, and VRChat's UI, which is not scenery.
    bool IsOurs(void* transform) {
        int guard = 0;
        for (void* p = transform; p && guard++ < 64; p = Unity::Parent(p)) {
            std::string n = Unity::Name(Unity::GameObjectOf(p));
            if (n.rfind("Archive", 0) == 0) return true;
            if (ContainsNoCase(n, "Canvas_QuickMenu")) return true;
            if (ContainsNoCase(n, "UserInterface")) return true;
        }
        return false;
    }

    // Grabbable AND not in someone's hand. A synced feature must not fight the person holding it.
    bool IsGrabbablePickup(void* transform) {
        static void* isHeld = nullptr;
        static bool looked = false;
        if (!looked) {
            looked = true;
            if (void* k = Il2::FindClass("VRC.SDKBase.VRC_Pickup"))
                isHeld = Il2::FindMethodIn(k, "get_IsHeld", 0);
        }
        int guard = 0;
        for (void* p = transform; p && guard++ < 64; p = Unity::Parent(p)) {
            void* pick = Unity::GetComponent(Unity::GameObjectOf(p), "VRC.SDKBase.VRC_Pickup");
            if (!Unity::IsAlive(pick)) continue;
            if (isHeld) {
                void* r = Il2::Invoke(isHeld, pick, nullptr);
                if (r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10)) return false;
            }
            return true;
        }
        return false;
    }

    // Every live component of a type in the scene, as a System.Type lookup the build cannot rename:
    // VRC_Pickup and Rigidbody are SDK/engine names, so they survive obfuscation.
    std::vector<void*> AllOfType(char const* fullName) {
        std::vector<void*> out;
        static void* findAll = Il2::FindMethod("UnityEngine.Object", "FindObjectsOfType", 1);
        void* k = Il2::FindClass(fullName);
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
        out.reserve(static_cast<size_t>(n));
        for (int i = 0; i < n; ++i)
            if (void* c = Il2::ArrayAt(arr, i)) out.push_back(c);
        return out;
    }

    // A 30-metre bound is a building, not something to orbit. Only checked when the item actually
    // has a renderer; a collider-only trigger volume is judged by its filters alone.
    bool TooBig(void* transform, float maxSize) {
        static void* getBounds = Il2::FindMethod("UnityEngine.Renderer", "get_bounds", 0);
        void* r = Unity::GetComponentInChildren(Unity::GameObjectOf(transform), "UnityEngine.Renderer", false);
        if (!Unity::IsAlive(r) || !getBounds) return false;
        void* boxed = Il2::Invoke(getBounds, r, nullptr);
        if (!boxed) return false;
        // Bounds is {Vector3 center; Vector3 extents} -- size is twice the extents.
        auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
        float sx = f[3] * 2.f, sy = f[4] * 2.f, sz = f[5] * 2.f;
        return (std::max)(sx, (std::max)(sy, sz)) > maxSize;
    }
}

std::vector<Found> Collect(float const around[3], float range, int want,
                           bool networkableOnly, float maxSize) {
    std::vector<Found> out;
    if (want <= 0) return out;

    std::unordered_set<void*> seen;
    std::vector<Found> cands;
    float r2 = range * range;

    auto consider = [&](void* comp) {
        if (!Unity::IsAlive(comp)) return;
        void* t = Il2::Invoke(Il2::FindMethod("UnityEngine.Component", "get_transform", 0), comp, nullptr);
        if (!Unity::IsAlive(t)) return;
        if (!seen.insert(t).second) return;

        float p[3];
        if (!Unity::GetPosition(t, p)) return;
        float dx = p[0] - around[0], dy = p[1] - around[1], dz = p[2] - around[2];
        float d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > r2 || d2 < 0.04f) return;        // too far, or it IS us
        if (IsPlayerOwned(t) || IsOurs(t)) return;
        if (TooBig(t, maxSize)) return;
        cands.push_back({ t, d2 });
    };

    for (void* pk : AllOfType("VRC.SDKBase.VRC_Pickup")) {
        if (!Unity::IsAlive(pk)) continue;
        void* t = Il2::Invoke(Il2::FindMethod("UnityEngine.Component", "get_transform", 0), pk, nullptr);
        if (networkableOnly && !IsGrabbablePickup(t)) continue;
        consider(pk);
    }

    // Rigidbody props are LOCAL only: a non-pickup rigidbody is not networkable, so a synced
    // feature could not move it for anyone else -- it would only desync you.
    if (!networkableOnly)
        for (void* rb : AllOfType("UnityEngine.Rigidbody")) consider(rb);

    std::sort(cands.begin(), cands.end(),
              [](Found const& a, Found const& b) { return a.dist2 < b.dist2; });
    if (static_cast<int>(cands.size()) > want) cands.resize(static_cast<size_t>(want));
    return cands;
}

}
