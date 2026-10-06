#include "engine.hpp"
#include "screenui.hpp"
#include "hooks.hpp"
#include "il2cpp.hpp"
#include "log.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <mutex>
#include <string>

namespace VRCA::Engine {

namespace {
    std::vector<std::unique_ptr<Module>> g_modules;
    std::mutex                           g_modulesLock;

    using EsUpdateFn = void (*)(void* self, void* method);
    EsUpdateFn g_esOriginal = nullptr;

    // THE LATE ANCHOR. Canvas.SendWillRenderCanvases is a STATIC method Unity's player loop calls
    // once a frame in PostLateUpdate -- after every MonoBehaviour's LateUpdate and after the
    // Animator has evaluated. A static il2cpp method with no parameters receives only its
    // MethodInfo*, hence the single argument here.
    using LateFn = void (*)(void* method);
    LateFn g_lateOriginal = nullptr;
    bool   g_lateUp = false;
    void*      g_esOwner    = nullptr;     // the one EventSystem we tick from
    ULONGLONG  g_esLastTick = 0;
    bool       g_ticking    = false;
    double     g_time       = 0.0;
    ULONGLONG  g_t0         = 0;

    // A module that throws must not take the frame (and VRChat's own Update) down with it. The SEH
    // guard lives in its own function because __try cannot share a scope with objects that unwind.
    void TickOne(Module* m) {
        __try { m->OnUpdate(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    // Its own function for the same reason TickOne is: __try may not share a scope with anything
    // that unwinds, and TickModules holds a vector.
    void PumpToastGuarded() {
        __try { ScreenUI::PumpToast(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    // HOTKEYS. Checked every frame whatever the modules are doing, because a key that only works
    // while its own module is already on would be useless. Edge-detected: a held key toggles once.
    struct Hotkey { char const* module; int vk; bool ctrl; bool wasDown; };
    Hotkey g_hotkeys[] = {
        { "Fly", 'F', true, false },   // Ctrl+F, as in the C# mod
    };

    void TickHotkeys(std::vector<Module*> const& live) {
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        for (auto& h : g_hotkeys) {
            bool down = (GetAsyncKeyState(h.vk) & 0x8000) != 0 && (!h.ctrl || ctrl);
            if (down && !h.wasDown) {
                for (Module* m : live)
                    if (m->Name() == h.module) { m->SetEnabled(!m->Enabled()); break; }
            }
            h.wasDown = down;
        }
    }

    void TickModules() {
        // Copied under the lock so a module that toggles another during its tick cannot invalidate
        // the iterator. Registration is done before Start(), so this is usually contention-free.
        std::vector<Module*> live;
        {
            std::lock_guard lock(g_modulesLock);
            live.reserve(g_modules.size());
            for (auto& m : g_modules) live.push_back(m.get());
        }
        TickHotkeys(live);
        for (Module* m : live) {
            if (!m->Enabled()) continue;
            TickOne(m);
        }

        // The status pill, whatever the modules are doing: a refusal must be visible even when the
        // feature that refused is switched off.
        PumpToastGuarded();
    }

    void TickOneLate(Module* m) {
        __try { m->OnLateUpdate(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    void TickLateModules() {
        std::vector<Module*> live;
        {
            std::lock_guard lock(g_modulesLock);
            live.reserve(g_modules.size());
            for (auto& m : g_modules) live.push_back(m.get());
        }
        for (Module* m : live) {
            if (!m->Enabled()) continue;
            TickOneLate(m);
        }
    }

    void LateDetour(void* method) {
        g_lateOriginal(method);     // Unity's own canvas pass first, untouched
        if (!g_ticking) return;     // nothing runs before the main pump has started
        TickLateModules();
    }

    void EsUpdateDetour(void* self, void* method) {
        g_esOriginal(self, method);    // VRChat's own UI update first, untouched

        // A scene can hold several EventSystems; tick from exactly one or every per-frame feature
        // runs at a multiple of its rate. Latch one and adopt another only once the first goes quiet
        // (destroyed with its scene). GetTickCount64, never an il2cpp time call, on this path.
        ULONGLONG now = GetTickCount64();
        if (!g_esOwner || now - g_esLastTick > 1000) g_esOwner = self;
        if (self != g_esOwner) return;
        g_esLastTick = now;

        if (!g_ticking) {
            g_ticking = true;
            g_t0 = now;
            VRCA::Log::Info("native pump: first tick -- modules are running.");
            // VRCA_ENABLE="ESP,Fly" switches named modules on at the first tick. A diagnostic
            // handle: a module that only misbehaves once ACTIVE cannot be exercised otherwise.
            char buf[256]{};
            if (GetEnvironmentVariableA("VRCA_ENABLE", buf, sizeof(buf)) && buf[0]) {
                std::string want(buf), one;
                size_t start = 0;
                while (start <= want.size()) {
                    size_t cut = want.find(',', start);
                    one = want.substr(start, cut == std::string::npos ? std::string::npos : cut - start);
                    while (!one.empty() && one.front() == ' ') one.erase(one.begin());
                    while (!one.empty() && one.back()  == ' ') one.pop_back();
                    if (!one.empty())
                        for (auto& mm : g_modules)
                            if (std::string(mm->Name()) == one && !mm->Enabled()) {
                                mm->SetEnabled(true);
                                VRCA::Log::Writef("Info", "VRCA_ENABLE: module '%s' enabled.", one.c_str());
                            }
                    if (cut == std::string::npos) break;
                    start = cut + 1;
                }
            }
        }
        g_time = (now - g_t0) / 1000.0;

        TickModules();
    }
}

bool Start() {
    void* m = VRCA::Hooks::DetourMethod("UnityEngine.EventSystems.EventSystem", "Update", 0,
                                        reinterpret_cast<void*>(&EsUpdateDetour),
                                        reinterpret_cast<void**>(&g_esOriginal));
    if (!m) {
        VRCA::Log::Error("native pump: could not hook EventSystem.Update.");
        return false;
    }
    VRCA::Log::Info("pump natif : EventSystem.Update detourne.");

    // THE LATE PHASE, SAID OUT LOUD WHEN IT IS MISSING. Without it the pose copy, and anything
    // else that writes a bone, is silently undone by the Animator every frame -- a failure that
    // looks exactly like a feature that does nothing, which is how it went unnoticed.
    if (VRCA::Hooks::DetourMethod("UnityEngine.Canvas", "SendWillRenderCanvases", 0,
                                  reinterpret_cast<void*>(&LateDetour),
                                  reinterpret_cast<void**>(&g_lateOriginal))) {
        g_lateUp = true;
        VRCA::Log::Info("native pump: Canvas.SendWillRenderCanvases hooked -- late phase active "
                        "(bones written after the Animator hold).");
    } else {
        VRCA::Log::Warn("native pump: late phase UNAVAILABLE on this build -- anything writing "
                        "a humanoid bone (pose mimic) will be erased by the Animator.");
    }
    return true;
}

void Stop() {
    if (void* mth = VRCA::Il2::FindMethod("UnityEngine.EventSystems.EventSystem", "Update", 0))
        VRCA::Hooks::Destroy(VRCA::Il2::MethodPointer(mth));
    if (g_lateUp)
        if (void* mth = VRCA::Il2::FindMethod("UnityEngine.Canvas", "SendWillRenderCanvases", 0))
            VRCA::Hooks::Destroy(VRCA::Il2::MethodPointer(mth));
}

bool Ticking() { return g_ticking; }
double Time()  { return g_time; }

Module* Add(std::unique_ptr<Module> m) {
    std::lock_guard lock(g_modulesLock);
    Module* raw = m.get();
    g_modules.push_back(std::move(m));
    return raw;
}

std::vector<Module*> Modules() {
    std::lock_guard lock(g_modulesLock);
    std::vector<Module*> out;
    out.reserve(g_modules.size());
    for (auto& m : g_modules) out.push_back(m.get());
    return out;
}

}
