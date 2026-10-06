#pragma once
#include "module.hpp"

#include <string>
#include <unordered_map>
#include <vector>

namespace VRCA {

    // GLOW ESP -- the outline of each remote player's ACTUAL avatar mesh, lit with VRChat's own
    // highlight post-effect (the one the game uses for "Hold to Grab").
    //
    // WHY THIS BEATS A DRAWN BOX: the glow is the real geometry, so it follows the pose, the
    // rotation and the shape exactly, instead of a screen-aligned rectangle that looks wrong the
    // moment somebody turns. It is still only a visualisation of what your client already draws.
    //
    // OFF MEANS OFF, THIS FRAME. Every renderer lit is remembered, and a switch going off unlights
    // exactly those before anything else can return early. That is the whole defence against the
    // "ghost glow" -- a prop left lit after its switch was turned off.
    //
    // The effect class is renamed every build, so it is not looked up by name: the engine's
    // recovery pass already found it by the SIGNATURE of its add-renderer method
    // (Renderer, Color, Boolean) and registered the alias.
    class GlowEspModule : public Module {
    public:
        GlowEspModule();
        void OnUpdate()  override;
        void OnDisable() override;

        bool  m_players = true;
        bool  m_pickups = false;
        float m_maxDistance = 0.f;      // 0 = no limit

    private:
        bool Resolve();
        void Light(void* renderer, float r, float g, float b);
        void Unlight(void* renderer);
        void UnlightAll();

        void*  m_fx = nullptr;
        void*  m_add = nullptr;         // (Renderer, Color, bool)
        bool   m_triedThisSecond = false;
        double m_nextResolve = 0.0;
        double m_nextSweep = 0.0;
        std::vector<void*> m_lit;
    };
}
