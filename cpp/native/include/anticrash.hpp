#pragma once
#include "module.hpp"

#include <functional>
#include <string>
#include <unordered_set>
#include <vector>

namespace VRCA {

    // AVATAR ANTI-CRASH.
    //
    // Switches off components that are in crasher territory on a loaded avatar: thousands of
    // particle systems, lights, audio sources, physbones, contacts, trails or constraints, and a
    // particle renderer allowed to draw screen-filling quads. Those are not heavy avatars, they are
    // payloads, and left alone they take the client down.
    //
    // NOTHING HERE IS PERMANENT. Every clamp is written to an undo journal with the value it
    // replaced, so switching the module off puts every avatar back exactly as it was found.
    // Components are DISABLED, never destroyed: a disabled component can be switched back on.
    //
    // It scans the AVATAR ROOT (the object carrying VRCAvatarDescriptor), not the player subtree:
    // sweeping the player would clamp VRChat's own objects, and with the camera budget at zero it
    // would switch off a camera the game put there itself.
    class AntiCrashModule : public Module {
    public:
        AntiCrashModule();

        void OnUpdate()  override;
        void OnDisable() override;

        int   m_maxParticleSystems = 48;
        int   m_maxLights          = 8;
        int   m_maxAudioSources    = 16;
        int   m_maxCloth           = 4;
        int   m_maxPhysBones       = 256;
        int   m_maxContacts        = 256;
        int   m_maxTrails          = 32;
        int   m_maxConstraints     = 512;
        float m_maxParticleSize    = 0.25f;
        int   m_rescanSeconds      = 5;

    private:
        struct Clamp {
            void*                 target = nullptr;
            std::function<void()> undo;
        };

        void Scan(void* avatarRoot);
        void RestoreAll();
        bool Journal(void* target, std::function<void()> undo);

        std::vector<Clamp>        m_journal;
        std::unordered_set<void*> m_seen;     // targets already clamped, so a rescan is idempotent
        double                    m_next = 0.0;
        int                       m_clamped = 0;
        std::string               m_last;
    };
}
