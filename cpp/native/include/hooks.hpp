#pragma once
#include "il2cpp.hpp"

// INLINE HOOKS, ON DOBBY.
//
// BepInEx's own IL2CPP hooking uses dobby.dll (it ships in BepInEx/core and is already loaded), so
// this engine uses the very same backend instead of vendoring MinHook or hand-writing a length
// disassembler. We resolve DobbyHook/DobbyDestroy at runtime; a detour is a plain function with the
// target's native ABI, and the trampoline it hands back is the original.
namespace VRCA::Hooks {

    // Resolves dobby.dll's exports. False if dobby is not present (it always is under BepInEx IL2CPP).
    bool Init();

    // Detours the code at `target`, returning the trampoline (call it to reach the original) in
    // `*original`. False if dobby refused it.
    bool Create(void* target, void* detour, void** original);

    // Convenience: resolve an il2cpp method BY NAME, read its compiled entry point, and detour it.
    // Returns the method (so the caller can introspect params) or null. The trampoline lands in
    // `*original`. Never hooks a stripped method (no compiled body).
    void* DetourMethod(char const* className, char const* methodName, int argc, void* detour, void** original);

    // Removes a detour previously created on `target`'s code.
    void Destroy(void* target);
}
