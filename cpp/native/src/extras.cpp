#include "extras.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "screenui.hpp"
#include "udon.hpp"
#include "unity.hpp"

#include <cstring>

namespace VRCA {

namespace {
    std::vector<void*> AllOfTypeAllClass(void* k) {
        std::vector<void*> out;
        static void* findAll = Il2::FindMethod("UnityEngine.Resources", "FindObjectsOfTypeAll", 1);
        if (!findAll || !k) return out;
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!cgt || !tgo) return out;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* ty = reinterpret_cast<CGT>(cgt)(k);
        void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
        if (!sysType) return out;
        void* a[1] = { sysType };
        void* arr = Il2::Invoke(findAll, nullptr, a);
        int n = Il2::ArrayLength(arr);
        for (int i = 0; i < n; ++i) if (void* c = Il2::ArrayAt(arr, i)) out.push_back(c);
        return out;
    }

    void* NewString(std::string const& s) {
        static void* fn = Il2::Export("il2cpp_string_new");
        if (!fn) return nullptr;
        using F = void*(*)(char const*);
        return reinterpret_cast<F>(fn)(s.c_str());
    }
}

// ============================================================================= BACKGROUNDS

BackgroundsModule::BackgroundsModule()
    : Module("Menu Backgrounds", "unlock the menu backgrounds reserved for VRChat Plus")
{
    Actions::Register("menuBackgrounds", [](std::string const&, void* u) {
        auto* m = static_cast<BackgroundsModule*>(u);
        m->SetEnabled(!m->Enabled());
        ScreenUI::Toast(m->Enabled() ? "Menu backgrounds unlocked" : "Menu backgrounds restored");
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<BackgroundsModule*>(u);
        size_t n = m->m_flipped.size();
        out += ",\"menuBackgrounds\":"      + Json::Bool(m->Enabled());
        out += ",\"menuBackgroundsCount\":" + Json::Int(static_cast<long long>(n));
        out += ",\"menuBackgroundsStatus\":" + Json::Str(
            !m->Enabled() ? std::string("inactive")
            : n == 0      ? std::string("active -- open the menu so they unlock")
                          : std::to_string(n) + " fond(s) debloque(s)");
    }, this);
}

void BackgroundsModule::OnDisable() {
    int n = 0;
    for (auto const& f : m_flipped) {
        if (!f.option || f.off <= 0) continue;
        *reinterpret_cast<unsigned char*>(static_cast<char*>(f.option) + f.off) = 1;
        ++n;
    }
    m_flipped.clear();
    m_next = 0.0;
    if (n) Log::Writef("Info", "[Backgrounds] %d fond(s) remis en VRC+.", n);
}

void BackgroundsModule::OnUpdate() {
    double now = Engine::Time();
    if (now < m_next) return;
    // Re-swept slowly: the option list is a ScriptableObject the menu loads lazily, so it is
    // simply not there during the first seconds of a session.
    m_next = now + 5.0;

    // THE CLASS IS RENAMED ON THIS BUILD, THE FIELD IS NOT.
    //
    // VRC.BackgroundOption does not resolve by name here, and asking for it by name reported the
    // whole feature dead. The option type is instead the one class that still declares a field
    // called _isVRCPlus -- which is exactly the bool the unlock needs, so finding the class and
    // finding the lever are one step. Searched across the assemblies VRChat's own types live in.
    static void* optClass = nullptr;
    static int   off = -2;
    if (off == -2) {
        off = -1;
        for (char const* asmName : { "VRChat", "Assembly-CSharp", "VRCCore-Standalone", "VRChat.UI" }) {
            optClass = Il2::FindClassWithField(asmName, "_isVRCPlus");
            if (optClass) break;
        }
        if (optClass) {
            off = Il2::FieldOffset(optClass, "_isVRCPlus");
            Log::Writef("Info", "[Backgrounds] background option: class '%s', _isVRCPlus @%d.",
                        Il2::ClassName(optClass), off);
        } else {
            Log::Warn("[Backgrounds] no class declares _isVRCPlus -- inactive on this build.");
        }
    }
    if (!optClass || off <= 0) return;

    auto options = AllOfTypeAllClass(optClass);
    if (options.empty()) {
        if (!m_warned && now > 30.0) {
            m_warned = true;
            Log::Info("[Backgrounds] no background option loaded yet (the menu loads them late).");
        }
        return;
    }

    int flipped = 0;
    for (void* o : options) {
        auto* flag = reinterpret_cast<unsigned char*>(static_cast<char*>(o) + off);
        if (*flag == 0) continue;                 // already free: not ours to put back later
        bool known = false;
        for (auto const& f : m_flipped) if (f.option == o) { known = true; break; }
        if (known) continue;
        *flag = 0;
        m_flipped.push_back({ o, off });
        ++flipped;
    }
    if (flipped)
        Log::Writef("Info", "[Backgrounds] %d fond(s) debloque(s) (%zu au total).",
                    flipped, m_flipped.size());
}

// ============================================================================== VIDEO URL

VideoUrlModule::VideoUrlModule()
    : Module("Video URL", "send a link to the world's video player")
{
    Actions::Register("videoUrl", [](std::string const& v, void* u) {
        static_cast<VideoUrlModule*>(u)->Play(v);
    }, this);
    m_enabled = true;      // nothing runs until a url is sent
}

void VideoUrlModule::OnUpdate() {}

bool VideoUrlModule::Play(std::string const& url) {
    if (url.empty()) { ScreenUI::Toast("Video: no link"); return false; }

    // THE WORLD'S OWN PLAYER, asked the way its own buttons ask it. The script is identified by
    // what it HOLDS -- a symbol whose current value is a VRCUrl -- because every world names its
    // video player differently and matching on a name would work in one world and no other.
    UdonModule* udon = nullptr;
    for (Module* m : Engine::Modules())
        if (m->Name() == "Udon") { udon = static_cast<UdonModule*>(m); break; }
    if (!udon) { ScreenUI::Toast("Video: Udon module missing"); return false; }

    udon->Rescan();

    void* urlClass = Il2::FindClass("VRC.SDKBase.VRCUrl");
    if (!urlClass) urlClass = Il2::FindClass("VRCUrl");
    if (!urlClass) {
        m_status = "this build does not expose VRCUrl";
        Log::Warn("[Video] VRCUrl not found -- cannot reach the player.");
        ScreenUI::Toast("Video: VRCUrl not found on this build");
        return false;
    }

    // Built natively, so the managed restriction on constructing a VRCUrl does not apply.
    static void* newObj = Il2::Export("il2cpp_object_new");
    using NO = void*(*)(void*);
    void* vrcUrl = newObj ? reinterpret_cast<NO>(newObj)(urlClass) : nullptr;
    if (!vrcUrl) { ScreenUI::Toast("Video: could not build the link"); return false; }
    if (void* ctor = Il2::FindMethodIn(urlClass, ".ctor", 1)) {
        void* s = NewString(url);
        void* a[1] = { s };
        Il2::Invoke(ctor, vrcUrl, a);
    }

    static void* setVar = nullptr;
    static bool looked = false;
    if (!looked) {
        looked = true;
        if (void* ub = Il2::FindClass("VRC.Udon.UdonBehaviour"))
            setVar = Il2::FindMethodIn(ub, "SetProgramVariable", 2);
    }
    if (!setVar) { ScreenUI::Toast("Video: SetProgramVariable unavailable"); return false; }

    int written = 0;
    std::string where;
    for (auto& e : udon->Rows()) {
        if (!Unity::IsAlive(e.behaviour)) continue;
        for (auto const& v : udon->Variables(e)) {
            if (v.type.find("VRCUrl") == std::string::npos) continue;
            void* ns = NewString(v.name);
            void* a[2] = { ns, vrcUrl };
            Il2::Invoke(setVar, e.behaviour, a);
            ++written;
            if (where.empty()) where = e.shortName;
            break;                                  // one url symbol per script is enough
        }
        if (written >= 4) break;                    // a world with several players: the nearest few
    }

    if (!written) {
        m_status = "no script in this world exposes a VRCUrl";
        Log::Warn("[Video] no video player found in this world.");
        ScreenUI::Toast("Video: no player found in this world");
        return false;
    }
    m_status = "link written to " + std::to_string(written) + " lecteur(s)";
    Log::Writef("Info", "[Video] link written to %d player(s) (including '%s'): %s",
                written, where.c_str(), url.c_str());
    ScreenUI::Toast("Link sent to the world's player");
    return true;
}

}
