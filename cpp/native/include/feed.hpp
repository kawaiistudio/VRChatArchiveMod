#pragma once
#include <string>
#include <vector>

// WHAT THE ARCHIVE ITSELF IS DOING, KEPT FOR THE IN-GAME CONSOLE.
//
// The two things this mod exists to serve -- the AUTO ARCHIVER uploading what you load, and the
// CACHE VIEWER recording the avatars that pass through -- both run in the DESKTOP CLIENT. Their
// output was only visible there, on a second screen or behind alt-tab, which is exactly where
// nobody is looking while they are in VRChat. So the client pushes each line over the bridge and
// this holds it.
//
// A RING BUFFER WITH A CLOCK, nothing more: no parsing, no formatting, no opinion about what a
// line means. The client decided that when it wrote the line, and one formatting decision in one
// place is what keeps the two from ever disagreeing.
//
// Written from the bridge worker, read from the game thread, so it takes its own lock.
namespace VRCA::Feed {

    enum class Kind { Archiver, Cache };

    struct Entry {
        std::string clock;     // HH:MM:SS, stamped on arrival
        Kind        kind = Kind::Archiver;
        std::string text;
    };

    void Add(Kind k, std::string const& line);

    // Which feed the in-game console shows. Toggled from the client.
    void Show(Kind k);
    [[nodiscard]] Kind Showing();

    // A copy of the feed being shown. Copied rather than borrowed: the writer is another thread.
    [[nodiscard]] std::vector<Entry> Current();

    // Bumped on every append and on every switch, so a console redraws on a CHANGE instead of
    // polling two list lengths -- a line arriving in the feed you are not looking at must not
    // cost a rebuild, and switching feeds must redraw even though no line arrived.
    [[nodiscard]] int Version();
}
