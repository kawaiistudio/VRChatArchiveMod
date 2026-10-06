#pragma once
#include "module.hpp"

#include <atomic>
#include <string>

namespace VRCA {

    // THE PHOTON LAYER -- every networked event the instance sends you passes through one method,
    // VRCNetworkingClient.OnEvent(EventData), and this is a detour on it.
    //
    // IT RUNS ON THE NETWORK THREAD, NOT UNITY'S, and hundreds of times a second. Two rules follow
    // and neither is optional:
    //
    //   * NO IL2CPP CALLS IN THE DETOUR. A managed invoke from a thread the runtime has not
    //     attached is how a GC lands on "collecting from unknown thread". The event's code and
    //     sender are read as RAW FIELDS at offsets resolved once, on the game thread. That is both
    //     safer than the C# version and cheaper.
    //   * NEVER KEEP THE EventData. Photon RECYCLES the instance, so a pointer held past the call
    //     describes some later event.
    //
    // Dropping an event is simply not forwarding to the original.
    //
    // WHAT IS NEVER RATE-LIMITED: the server (sender <= 0) and the room-state codes. A limit on
    // those mutes the whole room, and an unidentifiable event is left alone entirely -- if the
    // code cannot be read, every event would look like code 0 from nobody.
    class PhotonModule : public Module {
    public:
        PhotonModule();
        void OnUpdate()  override;
        void OnDisable() override;

        // Counts per event code, for the client's network page.
        [[nodiscard]] std::string CountsJson() const;
        void Reset();

        bool m_guard = false;        // drop what trips the limits
        int  m_perSecond = 60;       // per (actor, code)

    private:
        bool Install();
        int  m_attempts = 0;
        double m_next = 0.0;
        bool m_installed = false;
    };
}
