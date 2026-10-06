#pragma once
#include "module.hpp"

#include <atomic>
#include <string>
#include <thread>

namespace VRCA {

    // VA TAGS -- the community tag database the PLAYERS page and nameplates show.
    //
    // vrchatarchive.org/api/va-tags is a public read: {"records":[{"user_id","tags":[{text,color,
    // b,i,u,fx,author}],"locked"}]}. This fetches the whole table on its own worker (never the game
    // thread -- the HTTP blocks), indexes it by user id, and the bridge appends each roster player's
    // tags straight into the sync. The client already renders text/color/b/i/u/fx; the extra
    // "author" field rides along harmlessly and is ignored there.
    //
    // Pulled as data, never trusted as instructions: the only thing done with a record is to copy
    // its tags array back out, verbatim, for the one player whose id matches a live roster entry.
    class VaTagsModule : public Module {
    public:
        VaTagsModule();
        ~VaTagsModule() override;
        void OnUpdate() override;

        // The tags array (a JSON "[...]" string) for this user id, or "[]" if none. Thread-safe.
        static std::string ForUser(std::string const& userId);

    private:
        void Worker();
        std::thread*      m_worker = nullptr;
        std::atomic<bool> m_stop{false};
    };
}
