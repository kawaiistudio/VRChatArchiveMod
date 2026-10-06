#pragma once
#include "module.hpp"

#include <string>

namespace VRCA {

    // GHOST -- stop YOUR OWN player being serialised to the network.
    //
    // On the local player object, VRC.Networking.FlatBufferNetworkSerializer is what packs your
    // position, rotation and IK into the outbound stream. Switch it off and nothing about your
    // body leaves this client: everyone else sees you standing exactly where you were, while you
    // keep moving normally on your side. Switch it back on and the next serialisation catches
    // everybody up.
    //
    // THIS IS NOT FLY, and an earlier port had the client's "ghost" button wired to flying --
    // which is a different feature entirely and left this one unreachable.
    //
    // Your own object only: no other client, no world state, and LESS traffic, not more. A world
    // change rebuilds the player object, so the toggle drops to OFF there instead of chasing a
    // component that no longer exists.
    //
    // The serializer class is renamed every build; the engine's recovery pass already found it
    // through VRCPlayer's own field and registered the alias.
    class GhostModule : public Module {
    public:
        GhostModule();
        void OnUpdate()  override;
        void OnDisable() override;

    private:
        void* Serializer();
        void* m_serializer = nullptr;
        double m_nextCheck = 0.0;

    public:
        // What the client's card says under the switch. Written by OnUpdate, read by the sync.
        std::string m_status = "inactive";
    private:
        bool   m_writeWarned = false;
        bool   m_onLogged = false;
    };

    // FAST SYNC -- ask VRChat to serialise you at its FAST rate.
    //
    // RequireFastRate on the same serializer. This is VRChat's own flag, so VRChat's own
    // throttling still applies: it asks for the fast rate, it does not invent one.
    //
    // The write is READ BACK rather than assumed -- "set" and "took" are different claims, and a
    // flag the build moved would otherwise report success forever.
    class FastSyncModule : public Module {
    public:
        FastSyncModule();
        void OnUpdate()  override;
        void OnDisable() override;

    private:
        bool   Apply(bool want);
        bool   m_applied = false;
        bool   m_warned = false;
        double m_next = 0.0;
    };
}
