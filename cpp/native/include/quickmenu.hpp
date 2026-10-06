#pragma once
#include "module.hpp"

namespace VRCA {

    // THE IN-GAME VISUAL: a "VRChat Archive" tab in VRChat's own QuickMenu.
    //
    // Built the way the C# mod proved out: the QuickMenu ships a DISABLED DevTools tab and page
    // (Page_DevTools / Menu_DevTools). We Instantiate those -- clones inherit real fonts, sprites
    // and layout, which hand-built UI never gets -- reparent them into the tab strip and page host,
    // strip VRChat's obfuscated page controller off the clone roots so its state machine cannot
    // fight us, and relabel them. The QuickMenu is created lazily, so this polls until it exists and
    // injects exactly once.
    class QuickMenuModule : public Module {
    public:
        QuickMenuModule();
        void OnUpdate() override;

    private:
        bool TryInject();
        bool   m_done = false;
        double m_nextTry = 0.0;
        double m_nextRelabel = 0.0;
        int    m_attempts = 0;
    };
}
