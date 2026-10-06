#pragma once
#include "module.hpp"

#include <string>
#include <vector>

namespace VRCA {

    // THE PLAYERS PANEL, BESIDE THE QUICKMENU'S LEFT WING.
    //
    // WHERE IT HANGS, AND WHY IT IS NOT ANYWHERE ELSE. Four attempts in the C# mod settled this,
    // and each failure is worth keeping:
    //   1. Rows appended into VRChat's own wing list -- two nested layout groups fought and the
    //      section measured 356x0, far below a 1024-tall wing, with our rows interleaved among
    //      the game's.
    //   2. Hiding VRChat's rows to make room -- refused outright: it takes the menu away.
    //   3. A whole wing page cloned and left visible -- each wing has its OWN state controller and
    //      shows exactly one page at a time, so an always-active clone is invisible to it and just
    //      paints over whatever the wing was showing. The wings went blank.
    //   4. That clone moved beside the wings -- it STOPPED THE QUICKMENU OPENING, because the
    //      clone carried a UIPage and VRChat refused a duplicate page named "Root".
    //
    // SO NOTHING IS CLONED. The panel is BUILT from new GameObjects, which sidesteps UIPage and
    // the theme components entirely. Only the FONT is borrowed from a real text, because text
    // built from scratch has no font asset and renders invisible.
    //
    // It is parented to the menu's Window: measured, that object carries no theme component, no
    // layout group and no canvas group, so nothing there restyles or re-lays-out what we attach,
    // and unlike a wing it never animates to alpha 0 on retract. Child of the QuickMenu rather
    // than the HUD because in VR the menu rides the wrist -- a HUD panel would stay in the world.
    //
    // Clicking a row selects that player in the DESKTOP CLIENT's players page, through the sync's
    // "wingSelect" field. The in-game menu it used to open is sealed and never shows.
    class WingsModule : public Module {
    public:
        WingsModule();
        void OnUpdate()  override;
        void OnDisable() override;

        // What the bridge puts in the sync: the last row clicked, with a sequence the client
        // consumes once. Zero means nothing has been clicked.
        struct Selection { std::string userId, name; int seq = 0; };
        [[nodiscard]] static Selection Selected();

        bool m_showPositions = false;

        // One text per COLUMN per row. A single padded string does not line up in the menu's
        // proportional font -- spaces are narrower than digits and every row drifts.
        struct Cells { void* t[4]; };

    private:
        bool Build();
        void Fill();
        void Drop();
        void MakeHeadCells(void* host);
        void MakeCellSet(void* host, float size, bool heading, Cells* out);

        void*  m_panel = nullptr;      // our root under Window
        void*  m_countTx = nullptr;    // the player count in the header
        Cells  m_headCells{};          // the column-heading cells
        std::vector<Cells> m_cells;
        void*  m_rows  = nullptr;      // the row container
        void*  m_wing  = nullptr;      // the wing we track, to follow its open state
        void*  m_fontDonor = nullptr;  // a real text, cloned per row for its font
        std::vector<void*> m_pool;     // row objects, reused rather than rebuilt
        double m_nextTry = 0.0;
        double m_nextFill = 0.0;
        int    m_fails = 0;
        int    m_signature = -1;       // what the rows currently show, to skip a pointless rebuild
    };
}
