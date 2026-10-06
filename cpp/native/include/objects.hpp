#pragma once
#include "module.hpp"

#include <string>
#include <vector>

namespace VRCA {

    // THE HELD-OBJECT LEDGER, shared by every feature that moves the world's props.
    //
    // Taking a prop means changing it: its rigidbody is frozen kinematic so physics stops fighting
    // the per-frame writes, and its position is driven. ALL OF THAT IS RECORDED, because the ledger
    // is the only thing that can put the world back.
    //
    // TWO RULES PAID FOR IN THE C# MODULES:
    //   * A dead object is dropped, but a LIVE one that merely threw is RESTORED FIRST. Dropping a
    //     live prop from the ledger leaves it permanently kinematic, re-parented and wherever the
    //     feature had moved it, with nothing left that could ever put it back.
    //   * Objects are moved through the RIGIDBODY (MovePosition) when they have one. Writing
    //     transform.position on a kinematic body leaves its velocity at zero, VRChat serialises
    //     that zero, and remote clients have nothing to extrapolate -- they see a stutter of
    //     samples instead of motion.
    struct HeldObject {
        void* transform = nullptr;
        void* gameObject = nullptr;
        void* body = nullptr;          // Rigidbody, or null
        bool  addedBody = false;       // we created it, so we destroy it again
        bool  wasKinematic = false;
        int   wasInterpolation = 0;
        bool  wasGravity = true;
        float pos[3]{};
        float rot[4]{};
        float phase = 0.f;             // its place in the ring
        float tilt = 0.f;
        float ox = 0.f, oz = 0.f;      // its fixed spot on the platform disc
    };

    // OBJECT ORBIT -- the world's loose props spun in a ring around someone.
    class ObjectOrbitModule : public Module {
    public:
        ObjectOrbitModule();
        void OnUpdate()  override;
        void OnDisable() override;

        float m_radius = 2.5f;
        float m_height = 1.f;
        float m_speed  = 90.f;
        float m_range  = 30.f;
        int   m_count  = 0;            // 0 = as many as the world has
        bool  m_spin   = true;

    private:
        void Start(std::string const& userId);
        void Stop(char const* why);

        std::vector<HeldObject> m_held;
        std::string             m_target;
        float                   m_angle = 0.f;
        double                  m_lastTick = 0.0;
    };

    // ELEVATOR -- the same trick, but the props are gathered into a PLATFORM under someone and
    // driven straight up. It only ever moves OBJECTS: VRChat lets no client move another player's
    // avatar, and this does not try to. Whoever stands on the platform rides it the way they ride
    // any world lift -- their own client's physics carries them.
    class ElevatorModule : public Module {
    public:
        ElevatorModule();
        void OnUpdate()  override;
        void OnDisable() override;

        float m_climbSpeed = 1.5f;
        float m_maxHeight  = 15.f;     // 0 = no ceiling
        float m_radius     = 0.6f;     // a small, LEVEL pad: wide left gaps you fell through
        float m_range      = 30.f;
        int   m_count      = 0;
        bool  m_autoCorrect = true;    // follow the rider sideways so they cannot step off

    private:
        void Start(std::string const& userId);
        void Stop(char const* why);

        std::vector<HeldObject> m_held;
        std::string             m_target;
        float                   m_lift = 0.f;
        float                   m_base[3]{};
        double                  m_lastTick = 0.0;
    };

    // FLOAT OBJECTS -- gravity removed from every pickup in the world, per object.
    //
    // Rigidbody.useGravity = false on each body, never global Physics gravity: that would also stop
    // lifts, doors and physics puzzles for you. Re-swept every two seconds so pickups that appear
    // later float too, and every body's own flag is put back when it is switched off.
    class FloatObjectsModule : public Module {
    public:
        FloatObjectsModule();
        void OnUpdate()  override;
        void OnDisable() override;

        float m_range = 100000.f;      // the whole world by default

    private:
        struct Floated { void* body; bool wasGravity; };
        std::vector<Floated> m_bodies;
        double               m_next = 0.0;
    };
}
