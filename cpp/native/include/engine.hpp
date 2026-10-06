#pragma once
#include "module.hpp"

#include <memory>
#include <vector>

// The engine: owns the modules, installs the native frame pump, and ticks every enabled module
// once per frame. The pump detours EventSystem.Update -- a void method (never a coroutine), the
// same heartbeat the C++ GodClient port proved reliable where a managed/injected pump was not.
namespace VRCA::Engine {

    // Installs the native pump. Call once, after il2cpp and the hook backend are ready.
    bool Start();

    void Stop();

    // True once the frame pump has actually delivered its first tick.
    [[nodiscard]] bool Ticking();

    // Registers a module (engine takes ownership). Call before Start(), on the load thread.
    Module* Add(std::unique_ptr<Module> m);

    template <typename T, typename... Args>
    T* Register(Args&&... args) {
        auto p = std::make_unique<T>(std::forward<Args>(args)...);
        T* raw = p.get();
        Add(std::move(p));
        return raw;
    }

    [[nodiscard]] std::vector<Module*> Modules();

    // Seconds since the first tick, advanced on the game thread. Use this off-thread instead of any
    // il2cpp time call.
    [[nodiscard]] double Time();
}
