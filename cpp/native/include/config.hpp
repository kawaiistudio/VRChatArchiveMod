#pragma once
#include <string>
#include <vector>

// THE SETTINGS REGISTRY -- what the desktop client sees and drives.
//
// The C# mod's bridge did not know about any feature: it walked a registry of settings, each one a
// (Section, Key, Type, Description, Range) entry over a live value, and built both the schema and
// the sync payload from it. Anything a module registered appeared in the client automatically, and
// anything the client sent back was routed by "Section/Key" without the bridge naming it.
//
// This is that registry. A module BINDS its fields at construction; the bridge does the rest. Two
// consequences worth keeping in mind:
//   * a module that binds nothing is invisible to the client beyond its on/off switch;
//   * the bound pointer must outlive the registry, so bind MEMBERS of a module the engine owns,
//     never locals.
//
// Values are written from the bridge worker thread and read from the game thread. They are plain
// scalars, written whole, so a torn read is not possible for the types offered here; nothing in
// the registry may touch il2cpp.
namespace VRCA::Config {

    struct Entry {
        std::string section, key, type, desc, range;
        bool*        b = nullptr;
        float*       f = nullptr;
        int*         i = nullptr;
        std::string* s = nullptr;
    };

    // Bind a live value. The description is what the client shows under the control; the range
    // makes it draw a slider with the bounds the mod actually enforces instead of a text box.
    void Bool  (char const* section, char const* key, char const* desc, bool*  storage);
    void Float (char const* section, char const* key, char const* desc, float* storage,
                float lo, float hi);
    void Int   (char const* section, char const* key, char const* desc, int*   storage,
                int lo, int hi);
    void Text  (char const* section, char const* key, char const* desc, std::string* storage);

    [[nodiscard]] std::vector<Entry> const& All();

    // Apply "Section/Key" = value, as the client sends it. False means no such setting, or a value
    // that did not fit its type -- the bridge says so out loud rather than dropping it silently.
    bool Set(std::string const& id, std::string const& value);

    // The value as the client expects to read it back: "True"/"False" for a flag, an invariant
    // decimal for a number.
    [[nodiscard]] std::string ValueOf(Entry const& e);
}
