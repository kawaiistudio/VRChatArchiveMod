#pragma once
#include "module.hpp"

namespace VRCA {

    // UI DUMP -- the ground truth for every menu we touch.
    //
    // VRChat reshapes its menus every release and obfuscates the components behind them, so the
    // only reliable way to clone a page or find a button is to read the LIVE hierarchy. This walks
    // it and writes a readable tree next to the game: every object's name, whether it is active,
    // the il2cpp class of each component it carries, and any text it shows.
    //
    // Enumerated through Resources.FindObjectsOfTypeAll(Canvas) rather than scene roots: the scene
    // API boxes a struct and returns nothing useful on this build, while every menu lives under a
    // Canvas. Dumps itself once a few seconds after the first tick, and again later so a menu the
    // player opened in between is captured too.
    class UiDumpModule : public Module {
    public:
        UiDumpModule();
        void OnUpdate() override;

        // Writes the tree now. Returns how many nodes were written.
        int Dump(char const* tag);

    private:
        double m_next = 0.0;
        int    m_left = 2;
    };
}
