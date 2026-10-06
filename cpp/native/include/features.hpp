#pragma once
#include "module.hpp"
#include "player.hpp"

#include <unordered_set>
#include <vector>

namespace VRCA {

    // MOVEMENT -- run / walk / strafe / jump / gravity.
    //
    // Re-applied every frame rather than set once: a world (and every avatar load) writes its own
    // values back, so a one-shot set looks like the feature "stopped working" a minute later.
    // WALK / RUN / STRAFE / JUMP -- your own locomotion, through VRChat's own setters.
    //
    // These are the sanctioned per-player API (VRCPlayerApi.SetWalkSpeed and friends), the same
    // calls a world's Udon uses for a race or for mud. Local player only; nothing is sent.
    //
    // Three rules carried over from the C# module, each one a bug that was paid for:
    //   * PER CHANNEL. A switch that is OFF is left ENTIRELY to the world. Writing a captured
    //     original every frame pinned jump to whatever was read at spawn -- a world that spawns
    //     with jump disabled then made jumping impossible for the session, with the switch off.
    //   * CAPTURE ONCE PER WORLD, and never while our own values are applied. The local api is
    //     briefly null around a respawn; re-capturing then records OUR numbers as the world's.
    //   * ZERO IS "HANDS OFF", NOT "CANNOT JUMP". A jump impulse of 0 is physically valid and the
    //     slider reaches it, so it hands the channel back instead of taking jumping away.
    //
    // Re-applied ten times a second, not every frame: worlds write locomotion back constantly, and
    // at 100+ fps four il2cpp setters per frame measured 180 ms per second in the C# mod.
    class MovementModule : public Module {
    public:
        MovementModule();
        void OnUpdate()  override;
        void OnDisable() override;

        // Per-channel switches and their values, all bound into the client's settings registry.
        bool  m_walkOn = false, m_runOn = false, m_jumpOn = false;
        float m_walk = 4.f, m_strafe = 4.f, m_run = 8.f, m_jump = 5.f;

    private:
        void Capture(void* api);

        bool  m_captured = false;
        bool  m_wasWalk = false, m_wasRun = false, m_wasJump = false;
        float m_origWalk = 2.f, m_origStrafe = 2.f, m_origRun = 4.f, m_origJump = 3.f;
        double m_nextApply = 0.0;
    };

    // FLY -- the VRChat Archive fly, not a gravity trick.
    //
    // It PINS the VRCPlayer[Local] root transform every frame. Editing world gravity disturbs the
    // whole scene and VRChat fights a velocity push, but the controller follows its transform, so
    // writing the position is what actually flies. With no key held the last position is
    // re-asserted, which is why you float instead of sinking.
    //
    // Three things travel with it, exactly as in the C# module:
    //   * velocity is zeroed every frame, so gravity never accumulates;
    //   * walk / strafe / run are forced to 0 and restored on exit, or VRChat's animator plays
    //     strafe walks that twist the spine into the first-person camera;
    //   * NOCLIP follows fly on and off -- the local non-trigger colliders are disabled and put
    //     back from a ledger, re-asserted each frame because VRChat re-enables them on avatar load.
    //
    // Controls: WASD (ZQSD too) relative to the camera, E up, Q down, Shift boost, Ctrl+F toggles.
    class FlyModule : public Module {
    public:
        FlyModule();
        void OnUpdate()  override;
        void OnEnable()  override;
        void OnDisable() override;

        float m_speed      = 6.f;    // metres per second
        float m_boostSpeed = 18.f;   // with Shift held
        bool  m_noclip     = true;   // noclip follows fly

    private:
        void SetNoclip(bool on);
        void ReassertNoclip();

        struct HeldCollider { void* collider; bool wasEnabled; };
        std::vector<HeldCollider> m_disabled;

        float m_hold[3]{};
        bool  m_hasHold = false;
        bool  m_hotkeyWasDown = false;
        bool  m_noclipOn = false;
        bool  m_speedsSuppressed = false;
        float m_savedWalk = 2.f, m_savedStrafe = 2.f, m_savedRun = 4.f;
        double m_lastTick = 0.0;
    };

    // ESP -- a label over every remote player, drawn with VRChat's own UI.
    //
    // No overlay renderer: each label is a CLONE of a real HUD text, parented under the game's HUD
    // canvas and moved by world->screen projection every frame. Labels are pooled, so a player
    // leaving frees its label instead of destroying a UI object.
    class EspModule : public Module {
    public:
        EspModule();
        void OnUpdate()  override;
        void OnDisable() override;

        bool  m_showDistance = true;
        float m_maxDistance  = 0.f;    // 0 = no limit
        float m_headOffset   = 1.9f;   // metres above the player's feet
        int   m_cap          = 24;     // nearest N only: the projection cost is what froze the C# mod
    private:
        std::vector<std::pair<int,int>> m_labels;   // playerId -> ScreenUI::Label
    };

    // GRAVITY -- turn it off and float.
    //
    // READ THIS BEFORE EXPECTING A PRANK: gravity is simulated by each client for itself. This
    // changes YOUR gravity on YOUR machine. Everyone else keeps falling normally, and on their
    // screens you are simply a player drifting upward. There is no API to set somebody else's.
    //
    // TWO INDEPENDENT SWITCHES, because they break different things:
    //   PLAYER -- VRCPlayerApi.SetGravityStrength on you. Sanctioned SDK call, the same one worlds
    //             use for low-gravity rooms. Reversible, affects nothing but you.
    //   WORLD  -- Physics.gravity, which floats every loose rigidbody on your client. This one can
    //             genuinely break a world FOR YOU: lifts, physics puzzles and doors that rely on
    //             falling stop working. So it is off by default.
    //
    // A FLAG IS ONLY CLEARED ONCE THE RESTORE LANDED. The local api is momentarily null around a
    // respawn -- which is also when VRChat resets gravity -- and dropping the record there left
    // the player floating for the rest of the session with the switch reading OFF.
    class GravityModule : public Module {
    public:
        GravityModule();
        void OnUpdate()  override;
        void OnDisable() override;

        bool m_playerOff = true;
        bool m_worldOff  = false;

    private:
        bool  m_playerApplied = false;
        float m_originalPlayer = 1.f;
        bool  m_worldApplied = false;
        float m_originalWorld[3]{ 0.f, -9.81f, 0.f };
    };

    // SELF HIDE -- your own avatar is not drawn on YOUR screen; everyone else still sees it.
    //
    // Not "refuse the bundle": refusing your own avatar makes VRChat fall back to the error robot
    // and complain, and what the others see is unaffected either way. The avatar loads normally
    // and its renderers are held off.
    //
    // HELD OFF EVERY FRAME. VRChat writes those flags itself on the local avatar (first-person
    // head hiding, mirrors) and undoes a once-a-second pass between passes, which is exactly how
    // an earlier version could report "823 renderers hidden" with the avatar plainly visible. So
    // every frame each tracked renderer gets enabled = false AND forceRenderingOff = true, and the
    // set is re-collected once a second under the avatar root and under any MirrorClone.
    //
    // A DEAD RENDERER IS FORGOTTEN, A THROWING ONE IS PUT BACK FIRST. The ledger is the only record
    // of what was switched off: dropping a live renderer from it leaves a permanently invisible
    // piece of the avatar that switching the feature off cannot bring back.
    class SelfHideModule : public Module {
    public:
        SelfHideModule();
        void OnUpdate()  override;
        void OnDisable() override;
    private:
        void RestoreAll();
        int  Take(void* rootGameObject);

        struct Held { void* renderer; bool wasEnabled; };
        std::vector<Held>         m_held;
        std::unordered_set<void*> m_known;   // membership, so a 800-renderer avatar is not O(n^2)
        void*  m_root = nullptr;             // the avatar root the ledger belongs to
        double m_next = 0.0;
    };
}
