#include "hooks.hpp"
#include "log.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace VRCA::Hooks {

namespace {
    // Dobby's C API. DobbyHook(address, replace, &origin) returns 0 on success.
    using DobbyHookFn    = int (*)(void* address, void* replace, void** origin);
    using DobbyDestroyFn = int (*)(void* address);

    DobbyHookFn    g_hook    = nullptr;
    DobbyDestroyFn g_destroy = nullptr;
    bool           g_ready   = false;
}

bool Init() {
    HMODULE dobby = GetModuleHandleA("dobby.dll");
    if (!dobby) dobby = LoadLibraryA("dobby.dll");
    if (!dobby) {
        // Fall back to BepInEx's copy by full path if the bare name did not resolve.
        char self[MAX_PATH]{};
        GetModuleFileNameA(GetModuleHandleA("BepInEx.Unity.IL2CPP.dll"), self, MAX_PATH);
        VRCA::Log::Warn("dobby.dll not found by name -- hooking will be unavailable.");
    }
    if (!dobby) return false;

    g_hook    = reinterpret_cast<DobbyHookFn>(GetProcAddress(dobby, "DobbyHook"));
    g_destroy = reinterpret_cast<DobbyDestroyFn>(GetProcAddress(dobby, "DobbyDestroy"));
    g_ready   = (g_hook != nullptr);
    if (!g_ready) VRCA::Log::Error("dobby.dll loaded but DobbyHook missing.");
    else          VRCA::Log::Info("hook backend: dobby.dll ready.");
    return g_ready;
}

bool Create(void* target, void* detour, void** original) {
    if (!g_ready || !target || !detour) return false;
    return g_hook(target, detour, original) == 0;
}

void* DetourMethod(char const* className, char const* methodName, int argc, void* detour, void** original) {
    void* method = VRCA::Il2::FindMethod(className, methodName, argc);
    if (!method) {
        VRCA::Log::Writef("Warning", "hook: %s.%s(%d) not found in the metadata.", className, methodName, argc);
        return nullptr;
    }
    void* code = VRCA::Il2::MethodPointer(method);
    if (!code) {
        VRCA::Log::Writef("Warning", "hook: %s.%s has no compiled body (stripped).", className, methodName);
        return nullptr;
    }
    if (!Create(code, detour, original)) {
        VRCA::Log::Writef("Warning", "hook: dobby refused the detour of %s.%s.", className, methodName);
        return nullptr;
    }
    return method;
}

void Destroy(void* target) {
    if (g_ready && g_destroy && target) g_destroy(target);
}

}
