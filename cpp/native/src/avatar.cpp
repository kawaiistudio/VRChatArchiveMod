#include "avatar.hpp"
#include "bridge.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#include <cstring>

namespace VRCA {

namespace {
    // A string the il2cpp runtime owns, so the method we hand it can hold on to it.
    void* NewString(std::string const& s) {
        static void* fn = Il2::Export("il2cpp_string_new");
        if (!fn) return nullptr;
        using F = void*(*)(char const*);
        return reinterpret_cast<F>(fn)(s.c_str());
    }

    void* NewObject(void* klass) {
        static void* newObj = Il2::Export("il2cpp_object_new");
        if (!newObj || !klass) return nullptr;
        using F = void*(*)(void*);
        return reinterpret_cast<F>(newObj)(klass);
    }

    // The default constructor, so the object is not a bag of zeroes the game will dereference.
    void RunDefaultCtor(void* obj, void* klass) {
        for (void* k = klass; k; k = Il2::ClassParent(k))
            if (void* ctor = Il2::FindMethodIn(k, ".ctor", 0)) { Il2::Invoke(ctor, obj, nullptr); return; }
    }
}

AvatarModule::AvatarModule()
    : Module("Avatar", "wear an avatar by its id, copy a player's, or read its metadata")
{
    Actions::Register("wear", [](std::string const& v, void* u) {
        static_cast<AvatarModule*>(u)->Wear(v);
    }, this);
    Actions::Register("clone", [](std::string const& v, void* u) {
        static_cast<AvatarModule*>(u)->Clone(v);
    }, this);
    Actions::Register("wearLocal", [](std::string const& v, void* u) {
        static_cast<AvatarModule*>(u)->WearLocal(v);
    }, this);
    Actions::Register("dumpAvatar", [](std::string const& v, void* u) {
        auto* m = static_cast<AvatarModule*>(u);
        Actions::PostResult("avatar", v, m->Dump(v));
    }, this);
}

void AvatarModule::OnUpdate() {}   // everything here is driven by the client, not by the frame

// FIND THE GAME'S OWN CHANGE-AVATAR CALL, BY SHAPE.
//
// Its class and its name are renamed every build, but its SIGNATURE is not: a static void method
// taking exactly (ApiAvatar, String). ApiAvatar itself comes from the player layer, which learned
// it from the object that answered with an "avtr_" id -- so nothing here is matched on a name.
//
// An AMBIGUOUS match is refused. Calling the wrong static two-argument method with an avatar and
// a string is not a failed feature, it is whatever that method does.
bool AvatarModule::Resolve() {
    if (m_resolved) return m_change != nullptr;
    void* apiAvatar = Player::ApiAvatarClass();
    if (!apiAvatar) {
        m_status = "the ApiAvatar class is not known yet -- a loaded player is needed";
        return false;                               // not marked resolved: retry once a player is in
    }
    m_resolved = true;

    void* stringClass = Il2::FindClass("System.String");
    void* hit = nullptr;
    int hits = 0;
    for (void* k : Il2::AssemblyClasses("Assembly-CSharp")) {
        void* it = nullptr;
        while (void* m = Il2::NextMethod(k, &it)) {
            if (!Il2::MethodIsStatic(m)) continue;
            if (Il2::MethodParamCount(m) != 2) continue;
            if (Il2::MethodReturnClass(m) != nullptr) continue;          // void
            if (Il2::MethodParamClass(m, 0) != apiAvatar) continue;
            if (Il2::MethodParamClass(m, 1) != stringClass) continue;
            hit = m; ++hits;
        }
    }
    if (hits != 1) {
        m_status = hits ? "several methods match -- ambiguous, refused"
                        : "no static (ApiAvatar, String) method on this build";
        Log::Writef("Warning", "[Avatar] changement d'avatar : %d candidat(s) -- %s.",
                    hits, hits ? "ambiguous, refused" : "none");
        return false;
    }
    m_change = hit;

    // 'id' is declared on the ApiModel base, so the setter is looked for up the chain.
    for (void* k = apiAvatar; k; k = Il2::ClassParent(k))
        if (void* set = Il2::FindMethodIn(k, "set_id", 1)) { m_setId = set; break; }

    Log::Writef(m_setId ? "Info" : "Warning",
                m_setId ? "[Avatar] changement d'avatar resolu (ApiAvatar, String) + set_id."
                        : "[Avatar] method found but ApiAvatar has no writable 'id'.");
    return m_setId != nullptr;
}

void* AvatarModule::NewApiAvatar(std::string const& avatarId) {
    void* klass = Player::ApiAvatarClass();
    if (!klass || !m_setId) return nullptr;
    void* av = NewObject(klass);
    if (!av) return nullptr;
    RunDefaultCtor(av, klass);
    void* id = NewString(avatarId);
    if (!id) return nullptr;
    void* a[1] = { id };
    Il2::Invoke(m_setId, av, a);
    return av;
}

bool AvatarModule::Wear(std::string const& avatarId) {
    // Two shapes are valid: a published avatar, and a LOCAL test avatar VRChat has listed from
    // its test-avatar folder. Anything else is refused before the game is asked.
    bool local = avatarId.rfind("local:", 0) == 0;
    if (!local && avatarId.rfind("avtr_", 0) != 0) {
        m_status = "that does not look like an avatar id";
        ScreenUI::Toast(m_status);
        Log::Writef("Warning", "[Avatar] '%s' is neither an avtr_ id nor a local avatar.", avatarId.c_str());
        return false;
    }
    if (!Resolve()) {
        Log::Writef("Warning", "[Avatar] %s", m_status.c_str());
        ScreenUI::Toast("Avatar: " + m_status);
        return false;
    }

    // Asking for the avatar already worn makes VRChat tear it down and load it again for nothing.
    if (void* api = Player::LocalApi())
        if (Player::AvatarId(api) == avatarId) {
            m_status = "you are already wearing this avatar";
            Log::Info("[Avatar] already worn -- nothing to do.");
            return true;
        }

    void* fresh = NewApiAvatar(avatarId);
    if (!fresh) {
        m_status = "could not build the ApiAvatar";
        ScreenUI::Toast("Avatar: " + m_status);
        Log::Warn("[Avatar] could not build the ApiAvatar.");
        return false;
    }
    void* empty = NewString("");
    void* a[2] = { fresh, empty };
    Il2::Invoke(m_change, nullptr, a);
    m_status = "changement demande : " + avatarId;
    ScreenUI::Toast("Avatar requested");
    Log::Writef("Info", "[Avatar] changement demande -> %s", avatarId.c_str());
    return true;
}

// A .VRCA THE CLIENT JUST DROPPED IN. VRChat watches its test-avatar folder and lists what it
// finds as "local:sdk_<name>", so wearing one is the ordinary change-avatar call with that id --
// no file is loaded here, and nothing is uploaded anywhere.
//
// If VRChat has NOT listed it, the change simply does nothing, and the honest reason is that the
// game only scans that folder at startup: it has to be launched from the Mods Loader, or restarted
// once after the file was put there.
bool AvatarModule::WearLocal(std::string const& name) {
    std::string n = name;
    while (!n.empty() && (n.front() == ' ' || n.front() == '\t')) n.erase(n.begin());
    while (!n.empty() && (n.back() == ' ' || n.back() == '\t')) n.pop_back();
    if (n.size() > 5) {
        std::string tail = n.substr(n.size() - 5);
        for (char& c : tail) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        if (tail == ".vrca") n.resize(n.size() - 5);
    }
    if (n.empty()) {
        m_status = "no local avatar name given";
        ScreenUI::Toast(m_status);
        return false;
    }
    std::string id = (n.rfind("local:", 0) == 0) ? n : ("local:sdk_" + n);
    Log::Writef("Info", "[Avatar] local avatar requested: %s", id.c_str());
    if (!Wear(id)) {
        ScreenUI::Toast("Local avatar: launch VRChat from the Mods Loader, or restart once");
        return false;
    }
    return true;
}

bool AvatarModule::Clone(std::string const& userId) {
    for (auto const& p : Player::All()) {
        if (p.userId != userId) continue;
        if (p.avatarId.rfind("avtr_", 0) != 0) {
            m_status = "this player's avatar is not readable (private, or not loaded yet)";
            ScreenUI::Toast(m_status);
            Log::Writef("Warning", "[Avatar] clone: '%s' exposes no avtr_ id.", p.name.c_str());
            return false;
        }
        Log::Writef("Info", "[Avatar] cloning '%s' -> %s", p.name.c_str(), p.avatarId.c_str());
        return Wear(p.avatarId);
    }
    m_status = "player not found";
    ScreenUI::Toast("Clone: player not found");
    return false;
}

// WHAT THE GAME ALREADY KNOWS, nothing fetched. The roster holds the id and the name for every
// player in the instance; anything beyond that would mean a call to VRChat's API, which is the
// client's job, not the mod's.
std::string AvatarModule::Dump(std::string const& avatarId) {
    auto esc = [](std::string const& v) {
        std::string o = "\"";
        for (char c : v) {
            if (c == '"' || c == '\\') { o += '\\'; o += c; }
            else if (static_cast<unsigned char>(c) < 0x20) o += ' ';
            else o += c;
        }
        return o + "\"";
    };
    std::string out = "{\n  \"id\": " + esc(avatarId);
    for (auto const& p : Player::All()) {
        if (p.avatarId != avatarId) continue;
        out += ",\n  \"name\": " + esc(Player::AvatarName(p.api));
        out += ",\n  \"wornBy\": " + esc(p.name);
        out += ",\n  \"platform\": " + esc(p.platform);
        break;
    }
    return out + "\n}";
}

}
