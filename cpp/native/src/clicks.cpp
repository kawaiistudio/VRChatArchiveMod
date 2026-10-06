#include "clicks.hpp"
#include "hooks.hpp"
#include "il2cpp.hpp"
#include "log.hpp"

#include <mutex>
#include <vector>

namespace VRCA::Clicks {

namespace {
    using PressFn = void (*)(void* self, void* method);
    PressFn g_original = nullptr;
    bool    g_ready    = false;

    struct Entry { void* button; Callback cb; void* user; };
    std::mutex         g_lock;
    std::vector<Entry> g_routes;
    std::vector<Entry> g_any;

    void PressDetour(void* self, void* method) {
        g_original(self, method);          // VRChat's own click handling first, untouched
        if (!self) return;

        // Snapshot under the lock: a callback may register or drop a button, and mutating the
        // vector while iterating it would be a dangling read.
        std::vector<Entry> routes, any;
        {
            std::lock_guard lock(g_lock);
            routes = g_routes;
            any    = g_any;
        }
        for (auto const& e : routes)
            if (e.button == self && e.cb) e.cb(self, e.user);
        for (auto const& e : any)
            if (e.cb) e.cb(self, e.user);
    }
}

namespace {
    // OnPointerClick(PointerEventData) is the real entry the event system calls; Press() is an
    // internal Button helper that a VRChat subclass may override, in which case a detour on the
    // base Press never runs. Hooking both costs one extra detour and removes the guesswork.
    using ClickFn = void (*)(void* self, void* eventData, void* method);
    ClickFn g_clickOriginal = nullptr;

    void ClickDetour(void* self, void* eventData, void* method) {
        g_clickOriginal(self, eventData, method);
        if (!self) return;
        std::vector<Entry> routes, any;
        {
            std::lock_guard lock(g_lock);
            routes = g_routes;
            any    = g_any;
        }
        for (auto const& e : routes)
            if (e.button == self && e.cb) e.cb(self, e.user);
        for (auto const& e : any)
            if (e.cb) e.cb(self, e.user);
    }
}

bool Init() {
    if (g_ready) return true;
    void* press = Hooks::DetourMethod("UnityEngine.UI.Button", "Press", 0,
                                      reinterpret_cast<void*>(&PressDetour),
                                      reinterpret_cast<void**>(&g_original));
    void* click = Hooks::DetourMethod("UnityEngine.UI.Button", "OnPointerClick", 1,
                                      reinterpret_cast<void*>(&ClickDetour),
                                      reinterpret_cast<void**>(&g_clickOriginal));
    g_ready = (press != nullptr) || (click != nullptr);
    Log::Writef(g_ready ? "Info" : "Warning",
                "[Clicks] routage : Press=%s OnPointerClick=%s",
                press ? "ok" : "NON", click ? "ok" : "NON");
    return g_ready;
}

bool On(void* button, Callback cb, void* user) {
    if (!button || !cb) return false;
    if (!Init()) return false;
    std::lock_guard lock(g_lock);
    for (auto& e : g_routes)
        if (e.button == button) { e.cb = cb; e.user = user; return true; }
    g_routes.push_back({ button, cb, user });
    return true;
}

void Off(void* button) {
    std::lock_guard lock(g_lock);
    for (size_t i = 0; i < g_routes.size(); ++i)
        if (g_routes[i].button == button) { g_routes.erase(g_routes.begin() + static_cast<ptrdiff_t>(i)); return; }
}

void OnAny(Callback cb, void* user) {
    if (!cb || !Init()) return;
    std::lock_guard lock(g_lock);
    g_any.push_back({ nullptr, cb, user });
}

}
