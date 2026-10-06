#include "netself.hpp"
#include "obf.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#include <cstring>

namespace VRCA {

namespace {
    inline std::string kSerName() { return OBF("VRC.Networking.FlatBufferNetworkSerializer"); }

    // Why the last search failed, in words, so the client's card can say it instead of reading
    // as a feature that simply does nothing.
    std::string g_serWhy = "not looked up yet";

    // THE SERIALIZER ON THE LOCAL PLAYER'S RIG.
    //
    // By name first. When this build has renamed the class, the name finds nothing and everything
    // built on it reports itself dead -- so the fallback asks the question that survives renaming:
    // which component on the local rig carries a bool field called RequireFastRate? That field's
    // NAME is stable here (FastSync already relies on it), and it belongs to exactly one component.
    void* LocalSerializer() {
        void* root = Player::LocalRoot();
        if (!Unity::IsAlive(root)) { g_serWhy = "your local player does not exist yet (not in a world?)"; return nullptr; }
        void* go = Unity::GameObjectOf(root);
        std::string ser = kSerName();
        void* s = Unity::GetComponent(go, ser.c_str());
        if (!Unity::IsAlive(s)) s = Unity::GetComponentInChildren(go, ser.c_str(), true);
        if (Unity::IsAlive(s)) { g_serWhy.clear(); return s; }

        for (void* c : Unity::ComponentsInChildren(go, "UnityEngine.MonoBehaviour", true)) {
            if (!Unity::IsAlive(c)) continue;
            for (void* k = Il2::ClassOfObject(c); k; k = Il2::ClassParent(k))
                if (Il2::FieldOffset(k, OBF("RequireFastRate").c_str()) > 0) {
                    static bool said = false;
                    if (!said) {
                        said = true;
                        Log::Info("[Ghost] serialiser found by its SHAPE (RequireFastRate field) -- "
                                  "the class is renamed on this build.");
                    }
                    g_serWhy.clear();
                    return c;
                }
        }
        g_serWhy = "the local player's network serialiser was not found on this build";
        return nullptr;
    }
}

// ==================================================================================== GHOST

GhostModule::GhostModule()
    : Module("Ghost", "your body is no longer sent to the network: others see you frozen")
{
    Actions::Register("ghost", [](std::string const&, void* u) {
        auto* m = static_cast<GhostModule*>(u);
        m->SetEnabled(!m->Enabled());
        ScreenUI::Toast(m->Enabled() ? "Ghost ON -- others see you frozen"
                                     : "Ghost OFF -- you move again for them");
    }, this);

    // The card's second line. "ON" alone cannot tell the user that the serialiser has not been
    // found yet, which looks exactly like the feature silently not working.
    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<GhostModule*>(u);
        out += ",\"ghost\":" + Json::Bool(m->Enabled());
        out += ",\"ghostStatus\":" + Json::Str(m->Enabled() ? m->m_status : std::string("inactive"));
    }, this);
}

void* GhostModule::Serializer() {
    if (Unity::IsAlive(m_serializer)) return m_serializer;
    m_serializer = LocalSerializer();
    return m_serializer;
}

void GhostModule::OnDisable() {
    // Put it back even if the object was rebuilt meanwhile: re-resolving here is what stops a
    // world change from leaving you permanently invisible to everyone with the switch reading OFF.
    if (void* s = Serializer()) Unity::SetBehaviourEnabled(s, true);
    m_serializer = nullptr;
    m_status = "inactive";
    m_writeWarned = m_onLogged = false;
    Log::Info("[Ghost] OFF -- your body goes back on the network.");
}

void GhostModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_nextCheck) return;
    m_nextCheck = now + 0.5;

    void* s = Serializer();
    if (!s) { m_status = g_serWhy; return; }   // not in a world yet; nothing to hold off

    // Held OFF rather than switched once: VRChat rebuilds the player object on a world change and
    // on some avatar loads, and a component that came back enabled would silently end the effect.
    if (!Unity::BehaviourEnabled(s)) {
        m_status = "active -- your body is no longer sent";
        return;
    }
    Unity::SetBehaviourEnabled(s, false);

    // READ BACK. "Written" and "took" are different claims: a component the game re-enables every
    // frame, or one whose enabled flag is not where we wrote it, would otherwise report success
    // forever while everyone else keeps seeing you move.
    if (Unity::BehaviourEnabled(s)) {
        if (!m_writeWarned) {
            m_writeWarned = true;
            Log::Warn("[Ghost] the serialiser re-enables immediately -- VRChat reactivates it, ghost has no effect.");
        }
        m_status = "no effect -- VRChat re-enables the serialiser immediately";
        return;
    }
    m_status = "active -- your body is no longer sent";
    if (!m_onLogged) { m_onLogged = true; Log::Info("[Ghost] ON -- local player's serialiser switched off (verified)."); }
}

// ================================================================================= FAST SYNC

FastSyncModule::FastSyncModule()
    : Module("Fast Sync", "ask VRChat to serialise you at its fast rate")
{
    Actions::Register("fastSync", [](std::string const&, void* u) {
        auto* m = static_cast<FastSyncModule*>(u);
        m->SetEnabled(!m->Enabled());
        ScreenUI::Toast(m->Enabled() ? "Fast sync ON" : "Fast sync OFF");
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<FastSyncModule*>(u);
        out += ",\"fastSync\":" + Json::Bool(m->Enabled());
        out += ",\"fastSyncStatus\":" + Json::Str(
            !m->Enabled() ? "inactive"
            : m->m_applied ? "applique"
            : m->m_warned  ? "refused by this VRChat build"
                           : "en attente");
    }, this);
}

// Written and then READ BACK: "set" and "took" are different claims, and a property this build
// moved would otherwise report success for the rest of the session.
bool FastSyncModule::Apply(bool want) {
    void* s = LocalSerializer();
    if (!s) return false;

    // IT IS A FIELD, NOT A PROPERTY.
    //
    // The serializer CLASS is renamed on this build, so looking for get_/set_RequireFastRate
    // found nothing and the feature reported itself dead. Dumping the type settled it: the flag
    // is a plain bool FIELD whose NAME survived obfuscation, at offset 248 on this build. Found
    // by name because that is what is stable here; the offset is never assumed.
    static int off = -2;
    if (off == -2) {
        off = -1;
        for (void* k = Il2::ClassOfObject(s); k; k = Il2::ClassParent(k)) {
            off = Il2::FieldOffset(k, OBF("RequireFastRate").c_str());
            if (off > 0) break;
        }
        Log::Writef(off > 0 ? "Info" : "Warning",
                    off > 0 ? "[FastSync] RequireFastRate : champ @%d."
                            : "[FastSync] RequireFastRate not found on this build (%d) -- inactive.",
                    off);
    }
    if (off <= 0) {
        if (!m_warned) { m_warned = true; }
        return false;
    }

    auto* flag = reinterpret_cast<unsigned char*>(static_cast<char*>(s) + off);
    if ((*flag != 0) == want) return true;

    *flag = want ? 1 : 0;

    // READ BACK rather than assume: "set" and "took" are different claims, and a field the game
    // rewrites every frame would otherwise report success forever.
    bool took = (*flag != 0) == want;
    Log::Writef(took ? "Info" : "Warning",
                took ? "[FastSync] RequireFastRate = %s -- verified by read-back. It is the rate "
                       "of VRChat, so its own throttle still applies."
                     : "[FastSync] the RequireFastRate write did not take (%s).",
                want ? "true" : "false");
    return took;
}

void FastSyncModule::OnDisable() { Apply(false); m_applied = false; }

void FastSyncModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_next) return;
    m_next = now + 1.0;     // re-asserted: the serializer is rebuilt with the player object
    m_applied = Apply(true);
}


// ============================================================== SMALL CLIENT ACTIONS
//
// Three buttons that need no module of their own. They live here rather than in the bridge so
// the bridge keeps knowing nothing about features, and so each one can say WHY it refused.
namespace {
    // The last thing the join attempt had to say, shown on the client's card. GoToRoom returns
    // before the transition happens, so this never claims success -- only what was ASKED.
    std::string g_joinStatus;

    // VRChat's own GoToRoom -- the same call the game makes when you click a world. It returns
    // immediately and the transition happens over the next seconds, so success is never claimed:
    // whether an invite-only instance accepts us is the server's answer, not ours.
    bool GoToRoom(std::string const& room) {
        static void* m = nullptr;
        static bool looked = false;
        if (!looked) {
            looked = true;
            if (void* k = Il2::FindClass("VRC.SDKBase.Networking"))
                m = Il2::FindMethodIn(k, "GoToRoom", 1);
        }
        if (!m) {
            g_joinStatus = "unavailable on this VRChat build";
            Log::Warn("[ForceJoin] Networking.GoToRoom missing from this build.");
            ScreenUI::Toast("Join: unavailable on this build");
            return false;
        }
        static void* newStr = Il2::Export("il2cpp_string_new");
        if (!newStr) return false;
        using NS = void*(*)(char const*);
        void* s = reinterpret_cast<NS>(newStr)(room.c_str());
        void* a[1] = { s };
        Il2::Invoke(m, nullptr, a);
        g_joinStatus = "join request sent for " + room;
        Log::Writef("Info", "[ForceJoin] join request %s", room.c_str());
        ScreenUI::Toast("Join request sent");
        return true;
    }

    // "wrld_<guid>" with whatever the client wrapped it in -- a full launch URL, an instance
    // suffix, or just the id. Anything with no world id in it is refused out loud rather than
    // handed to the game as an empty room.
    std::string NormaliseRoom(std::string const& raw) {
        size_t at = raw.find("wrld_");
        if (at == std::string::npos) return {};
        size_t end = at;
        while (end < raw.size()) {
            char ch = raw[end];
            bool ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') ||
                      (ch >= 'A' && ch <= 'Z') || ch == '-' || ch == '_' || ch == ':' ||
                      ch == '~' || ch == '(' || ch == ')' || ch == '.';
            if (!ok) break;
            ++end;
        }
        return raw.substr(at, end - at);
    }

    struct SmallActions {
        SmallActions() {
            Actions::Register("forceJoin", [](std::string const& v, void*) {
                std::string room = NormaliseRoom(v);
                if (room.empty()) {
                    g_joinStatus = "no world id in what was pasted";
                    Log::Writef("Warning", "[ForceJoin] no world id in '%s'.", v.c_str());
                    ScreenUI::Toast("Join: no world id");
                    return;
                }
                GoToRoom(room);
            });

            // THE ROSTER IS ALREADY LIVE HERE. The C# mod cached players and tags, so the client
            // had to ask for a refresh; this port reads the roster fresh on every sync, so there
            // is nothing to invalidate. Claimed anyway -- an action that silently does nothing
            // and one that is not implemented must not look the same in the log.
            auto alreadyLive = [](std::string const&, void*) {
                static bool said = false;
                if (said) return;
                said = true;
                Log::Info("[Bridge] refresh requested: the roster is re-read every sync, nothing to invalidate.");
            };
            Actions::Register("refreshTags", alreadyLive);
            Actions::Register("refreshFavs", alreadyLive);

            Actions::RegisterState([](std::string& out, void*) {
                out += ",\"forceJoinStatus\":" + Json::Str(g_joinStatus);
            });
        }
    };
    SmallActions g_smallActions;
}

}
