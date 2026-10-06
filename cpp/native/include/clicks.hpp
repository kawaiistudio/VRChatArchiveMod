#pragma once

// BUTTON CLICKS WITHOUT A DELEGATE.
//
// Hearing about a click on a button we injected would normally mean
// Button.onClick.AddListener(UnityAction) -- and building an Il2Cpp delegate goes through
// Type.MakeGenericType, which throws inside il2cpp on this build. So no delegate at all:
// UnityEngine.UI.Button.Press() is what actually runs on every click, whoever raised it. Detour it
// once and route by POINTER. One hook serves every button the mod will ever inject, on this build
// and the next.
namespace VRCA::Clicks {

    using Callback = void (*)(void* button, void* user);

    // Installs the Press() detour on first use. False if the method cannot be hooked.
    bool Init();

    // Calls `cb(button, user)` when that exact Button component is clicked. Registering the same
    // button again replaces its callback.
    bool On(void* button, Callback cb, void* user = nullptr);

    // Stops routing for a button (it was destroyed, or the feature went off).
    void Off(void* button);

    // Calls `cb` for EVERY click on any button, after the per-button routes. Used to notice that a
    // different tab was pressed so our page can step aside.
    void OnAny(Callback cb, void* user = nullptr);
}
