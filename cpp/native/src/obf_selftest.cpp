#include "obf.hpp"
#include "log.hpp"

namespace VRCA {
    // Proves at boot that OBF round-trips on this compiler. The strings here are throwaway, so this
    // is also a safe place to confirm the hiding works before any patch depends on it: if the
    // round-trip ever broke, every hidden il2cpp target name would resolve to garbage and the
    // patches would silently miss -- better caught here, loudly, than in the field.
    void ObfSelfTest() {
        bool ok = OBF("VRC.Networking.FlatBufferNetworkSerializer")
                      == std::string("VRC.Networking.FlatBufferNetworkSerializer")
               && OBF("dexpatch") == std::string("dexpatch")
               && OBF("") == std::string("");
        Log::Writef(ok ? "Info" : "Error",
                    ok ? "[Obf] self-test OK -- the sensitive strings decrypt at runtime."
                       : "[Obf] SELF-TEST FAILED -- do not ship, the target names would be broken.");
    }
}
