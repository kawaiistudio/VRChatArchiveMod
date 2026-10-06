#pragma once
#include "module.hpp"
#include "objects.hpp"

#include <string>
#include <vector>

namespace VRCA {

    // MARK -- an anchor you place with your aim, then use for the world's loose objects: teleport
    // them around it, orbit them, or arrange them into SHAPES.
    //
    // The anchor is a small sphere with no collider, so it never blocks anything and never becomes
    // something a world can interact with. It is placed where you are LOOKING: a ray out of the
    // camera, ignoring triggers and the player layers, falling back to three metres ahead when it
    // hits nothing.
    //
    // EVERY OBJECT MOVED IS REMEMBERED. The same ledger the orbit and the elevator use: a prop is
    // frozen kinematic while it is part of a shape, and switching the feature off puts its body
    // state, its position and its rotation back exactly as they were found.
    class MarkModule : public Module {
    public:
        MarkModule();
        void OnUpdate()  override;
        void OnDisable() override;

        void Put();
        void Clear();
        void TeleportObjects();
        void OrbitObjects();
        void Shape(std::string const& kind);

        float m_range = 60.f;        // how far out loose objects are gathered
        float m_artSize = 0.f;       // 0 = sized from how many objects there are
        int   m_count = 0;           // 0 = every loose object

    private:
        [[nodiscard]] bool HasMark() const { return m_marker != nullptr; }
        void  SetMark(float const pos[3]);
        void  Restore();
        [[nodiscard]] std::vector<HeldObject> Gather(bool freeze);

        void*  m_marker = nullptr;
        float  m_pos[3]{};
        std::vector<HeldObject> m_held;
    };
}
