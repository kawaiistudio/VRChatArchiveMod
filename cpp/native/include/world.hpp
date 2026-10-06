#pragma once
#include <string>

// WHICH WORLD YOU ARE IN, READ OFF VRCHAT'S OWN LOG.
//
// The game's own objects would be the first source, but RoomManager and ApiWorldInstance are
// renamed on this build, so asking them hands back nothing -- and the failure is SILENT: the
// client's world name, its Udon world profiles and its preset packs all go quiet at once with
// nothing saying "I do not know where you are".
//
// VRChat writes the answer to its own log on every join, in plain text no obfuscator touches:
//     [Behaviour] Joining wrld_xxxxxxxx-....:12345~region(eu)
//     [Behaviour] Joining or Creating Room: <the world's name>
//
// The C# mod subscribed to Unity's log callback for this. Natively the log FILE is simpler and
// needs no delegate bridge at all: the newest output_log_*.txt is tailed from a worker thread, so
// nothing here runs on the game thread and nothing touches il2cpp.
namespace VRCA::World {

    // Starts the tail. Safe to call more than once.
    void Start();
    void Stop();

    // Empty until a join line has been seen.
    [[nodiscard]] std::string Id();
    [[nodiscard]] std::string Name();
    [[nodiscard]] std::string Instance();
}
