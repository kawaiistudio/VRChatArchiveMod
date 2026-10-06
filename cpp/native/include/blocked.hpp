#pragma once
#include "module.hpp"

#include <string>
#include <unordered_set>

namespace VRCA {

    // WHO BLOCKED ME.
    //
    // VRChat never tells you outright, but its own API carries both directions: a player-moderation
    // entry of type "block" whose TARGET is me names the person who did it in its source. That is
    // the whole trick, and it needs no guessing from how an avatar renders.
    //
    // ASKED OVER HTTP, NOT THROUGH THE GAME'S OWN CALL. The managed route needs a callback handed
    // to il2cpp, and that conversion is documented in this project as killing the process. The same
    // answer is one endpoint away, asked with the session's own cookie, and it depends on no member
    // index -- so it survives the next time VRChat permutes its members.
    //
    // ASKED ONLY WHEN ASKED, never on a timer: it is a request to VRChat's servers carrying the
    // user's session, and a feature that makes those on its own is a feature that gets an account
    // rate-limited.
    //
    // The User-Agent names the app and a CONTACT URL. VRChat refuses an empty one, and a personal
    // email would be handed to a third party on every single request.
    class BlockedByModule : public Module {
    public:
        BlockedByModule();
        void OnUpdate() override;

        // True once a fetch has answered; empty before that, which is NOT the same as "nobody".
        [[nodiscard]] bool Known() const { return m_known; }
        [[nodiscard]] std::unordered_set<std::string> const& BlockedMe() const { return m_blockedMe; }

    private:
        void Fetch();

        std::unordered_set<std::string> m_blockedMe;
        bool m_known = false;
        bool m_busy = false;
    };
}
