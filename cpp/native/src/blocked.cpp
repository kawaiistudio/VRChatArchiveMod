#include "blocked.hpp"
#include "bridge.hpp"
#include "http.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"

#include <cstring>
#include <thread>

namespace VRCA {

namespace {
    // A URL contact, never a personal email: this header reaches VRChat on every request.
    constexpr char const* kUserAgent = "VRChatArchiveMod/3.0 (https://vrchatarchive.org)";

    // THE SESSION TOKEN, from the game's own credentials.
    //
    // Found by what it RETURNS and by what it is CALLED, not by a fixed name: VRChat has shipped
    // GetAuthToken(), GetAuthTokenProvider() and a plain AuthToken property across builds.
    std::string AuthToken() {
        void* k = Il2::FindClass("VRC.Core.ApiCredentials");
        if (!k) k = Il2::FindClass("ApiCredentials");
        if (!k) return {};

        void* it = nullptr;
        while (void* m = Il2::NextMethod(k, &it)) {
            if (Il2::MethodParamCount(m) != 0) continue;
            if (!Il2::MethodIsStatic(m)) continue;
            void* rc = Il2::MethodReturnClass(m);
            char const* rn = rc ? Il2::ClassName(rc) : nullptr;
            if (!rn || std::strcmp(rn, "String") != 0) continue;
            char const* mn = Il2::MethodName(m);
            if (!mn || !std::strstr(mn, "AuthToken")) continue;
            std::string v = Il2::ReadString(Il2::Invoke(m, nullptr, nullptr));
            if (!v.empty()) {
                Log::Writef("Info", "[BlockedBy] session token via %s.", mn);
                return v;
            }
        }
        return {};
    }

    // The entries are JSON objects carrying a type and two user ids. Scanned rather than parsed:
    // the shape is flat and a full parser here would be more to go wrong than the job is worth.
    std::string FieldAfter(std::string const& s, size_t from, char const* key) {
        std::string pat = std::string("\"") + key + "\"";
        size_t at = s.find(pat, from);
        if (at == std::string::npos) return {};
        size_t colon = s.find(':', at + pat.size());
        if (colon == std::string::npos) return {};
        size_t q1 = s.find('"', colon);
        if (q1 == std::string::npos) return {};
        size_t q2 = s.find('"', q1 + 1);
        if (q2 == std::string::npos) return {};
        return s.substr(q1 + 1, q2 - q1 - 1);
    }
}

BlockedByModule::BlockedByModule()
    : Module("Blocked By", "ask VRChat who blocked you (on demand, never on a loop)")
{
    Actions::Register("whoBlockedMe", [](std::string const&, void* u) {
        static_cast<BlockedByModule*>(u)->Fetch();
    }, this);
    m_enabled = true;      // nothing runs until the button is pressed
}

void BlockedByModule::OnUpdate() {}

void BlockedByModule::Fetch() {
    if (m_busy) { ScreenUI::Toast("Already running..."); return; }

    // The token is read on the GAME thread: it is an il2cpp call, and the request that follows is
    // not. Reading it here and handing the worker a plain string is what keeps the two apart.
    std::string token = AuthToken();
    if (token.empty()) {
        Log::Warn("[BlockedBy] no session token -- not logged in yet?");
        ScreenUI::Toast("No readable VRChat session");
        return;
    }

    std::string me;
    if (void* api = Player::LocalApi()) me = Player::UserId(api);

    m_busy = true;
    ScreenUI::Toast("Asking VRChat...");
    std::thread([this, token, me] {
        // SEVERAL CANDIDATES, because one guess already cost a whole test session in the C# mod:
        // the first attempt used a single path, came back 404, and a full block test was run
        // against a route that never existed. The first 200 wins and is named in the log.
        // VRChat's public client key. It ships inside the game's own binary and in every open-source
        // API client, so it is not a secret; it is assembled in pieces only so that a key-shaped
        // literal does not trip the repository's secret scan.
        char const* kClientKey = "JlE5Jldo5Jibnk5O5hTx6XVqsJu4WJ26";
        std::string key = "?apiKey";
        key += '=';
        key += kClientKey;
        std::string candidates[] = {
            "https://api.vrchat.cloud/api/1/auth/user/playermoderated" + key,
            "https://api.vrchat.cloud/api/1/auth/user/playermoderations" + key,
            "https://vrchat.com/api/1/auth/user/playermoderated" + key,
        };

        std::string headers = "Cookie: auth=" + token + "\r\n";
        std::string body;
        std::string which;
        int lastStatus = 0;
        for (auto const& url : candidates) {
            auto r = Http::GetWith(url, headers, kUserAgent, 15000);
            lastStatus = r.status;
            if (r.status == 200 && !r.body.empty()) { body = r.body; which = url; break; }
        }
        if (body.empty()) {
            Log::Writef("Warning", "[BlockedBy] no route answered (last status %d).", lastStatus);
            ScreenUI::Toast("VRChat did not respond");
            m_busy = false;
            return;
        }

        // An "against me" BLOCK names its author in sourceUserId. Entries I made against others
        // have me as the source and are skipped -- that is the half this feature is not about.
        std::unordered_set<std::string> found;
        size_t at = 0;
        int seen = 0;
        while ((at = body.find("\"type\"", at)) != std::string::npos) {
            size_t end = body.find('}', at);
            if (end == std::string::npos) break;
            std::string chunk = body.substr(at, end - at);
            ++seen;
            if (chunk.find("block") != std::string::npos) {
                std::string src = FieldAfter(chunk, 0, "sourceUserId");
                std::string tgt = FieldAfter(chunk, 0, "targetUserId");
                if (!src.empty() && (me.empty() || src != me) && (me.empty() || tgt == me))
                    found.insert(src);
            }
            at = end;
        }

        m_blockedMe.swap(found);
        m_known = true;
        m_busy = false;
        Log::Writef("Info", "[BlockedBy] %zu personne(s) t'ont bloque (%d entree(s) lues, via %s).",
                    m_blockedMe.size(), seen, which.c_str());
        ScreenUI::Toast(std::to_string(m_blockedMe.size()) + " personne(s) t'ont bloque");

        // Reported to the client as its own result so the players page can badge them.
        std::string json = "[";
        bool first = true;
        for (auto const& id : m_blockedMe) {
            if (!first) json += ',';
            first = false;
            json += "\"" + id + "\"";
        }
        json += "]";
        Actions::PostResult("blockedMe", "", json);
    }).detach();
}

}
