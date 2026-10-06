#include "player.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#include <cstring>

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace VRCA::Player {

namespace {
    constexpr char const* kApi = "VRC.SDKBase.VRCPlayerApi";

    struct M {
        void* getAll = nullptr, *getLocal = nullptr;
        void* getName = nullptr, *getId = nullptr, *isLocal = nullptr, *isMaster = nullptr;
        void* isOwner = nullptr, *isVR = nullptr;
        void* getPos = nullptr, *getRot = nullptr, *teleport = nullptr;
        void* setRun = nullptr, *setWalk = nullptr, *setStrafe = nullptr, *setJump = nullptr, *setGrav = nullptr;
        void* getRun = nullptr, *getGrav = nullptr;
        void* setVel = nullptr, *getWalk = nullptr, *getStrafe = nullptr, *getJump = nullptr;
        bool  reportPending = false;
        bool  ready = false;
    };
    M g;

    void Ensure() {
        if (g.ready) return;
        g.ready = true;
        g.getAll   = Il2::FindMethod(kApi, "get_AllPlayers", 0);
        if (!g.getAll) g.getAll = Il2::FindMethod(kApi, "GetPlayers", 1);
        g.getLocal = Il2::FindMethod(kApi, "get_Instance", 0);
        g.getName  = Il2::FindMethod(kApi, "get_displayName", 0);
        g.getId    = Il2::FindMethod(kApi, "get_playerId", 0);
        g.isLocal  = Il2::FindMethod(kApi, "get_isLocal", 0);
        g.isMaster = Il2::FindMethod(kApi, "get_isMaster", 0);
        g.isOwner  = Il2::FindMethod(kApi, "IsOwner", 1);
        g.isVR     = Il2::FindMethod(kApi, "IsUserInVR", 0);
        g.getPos   = Il2::FindMethod(kApi, "GetPosition", 0);
        g.getRot   = Il2::FindMethod(kApi, "GetRotation", 0);
        g.teleport = Il2::FindMethod(kApi, "TeleportTo", 2);
        g.setRun    = Il2::FindMethod(kApi, "SetRunSpeed", 1);
        g.setWalk   = Il2::FindMethod(kApi, "SetWalkSpeed", 1);
        g.setStrafe = Il2::FindMethod(kApi, "SetStrafeSpeed", 1);
        g.setJump   = Il2::FindMethod(kApi, "SetJumpImpulse", 1);
        g.setGrav   = Il2::FindMethod(kApi, "SetGravityStrength", 1);
        g.getRun    = Il2::FindMethod(kApi, "GetRunSpeed", 0);
        // WHAT ACTUALLY RESOLVED, said once. This build has already removed get_isLocal and
        // get_gameObject from VRCPlayerApi without a word, and anything else missing here fails
        // the same silent way -- an empty display name reads as "no players" everywhere
        // downstream rather than as a missing getter.
        g.reportPending = true;
        g.getJump   = Il2::FindMethod(kApi, "GetJumpImpulse", 0);
        g.setVel    = Il2::FindMethod(kApi, "SetVelocity", 1);
        g.getWalk   = Il2::FindMethod(kApi, "GetWalkSpeed", 0);
        g.getStrafe = Il2::FindMethod(kApi, "GetStrafeSpeed", 0);
        g.getGrav   = Il2::FindMethod(kApi, "GetGravityStrength", 0);
    }

    // A boxed value type's payload starts right after the 0x10 object header.
    template <typename T> T Unbox(void* boxed, T fallback = {}) {
        return boxed ? *reinterpret_cast<T*>(static_cast<char*>(boxed) + 0x10) : fallback;
    }

    // Renamed symbols on this build are built from a high-Latin alphabet, so a string that is
    // mostly bytes above 0x7F is an internal name, never something a person typed.
    bool LooksObfuscated(std::string const& v) {
        if (v.empty()) return false;
        int high = 0;
        for (unsigned char ch2 : v) if (ch2 > 0x7F) ++high;
        return high * 2 >= static_cast<int>(v.size());
    }

    void SetFloat(void* method, void* api, float v) {
        if (!method || !api) return;
        float f = v; void* a[1] = { &f };
        Il2::Invoke(method, api, a);
    }
}

// Defined with the chain further down, where the player objects are reachable.
namespace { std::string NameFromPlayerObjects(void* api); }

std::string DisplayName(void* api) {
    Ensure();
    if (!api) return {};
    if (g.getName) {
        std::string v = Il2::ReadString(Il2::Invoke(g.getName, api, nullptr));
        if (!v.empty()) return v;
    }
    // get_displayName is absent from VRCPlayerApi on this build: the name comes off the player
    // objects instead. An empty name is not cosmetic -- every list that shows players skips a
    // nameless row, so the whole roster reads as empty.
    return NameFromPlayerObjects(api);
}
int Id(void* api) {
    Ensure();
    return (api && g.getId) ? Unbox<int>(Il2::Invoke(g.getId, api, nullptr), -1) : -1;
}
bool IsLocal(void* api) {
    Ensure();
    // get_isLocal is absent on this build, so identity against the local api is the answer.
    if (g.isLocal) return api && Unbox<unsigned char>(Il2::Invoke(g.isLocal, api, nullptr)) != 0;
    return api && api == LocalApi();
}
Vec3 Position(void* api) {
    Ensure();
    Vec3 v;
    if (!api || !g.getPos) return v;
    void* boxed = Il2::Invoke(g.getPos, api, nullptr);
    if (!boxed) return v;
    auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
    v.x = f[0]; v.y = f[1]; v.z = f[2];
    return v;
}

void SetRunSpeed(void* api, float v)         { Ensure(); SetFloat(g.setRun, api, v); }
void SetWalkSpeed(void* api, float v)        { Ensure(); SetFloat(g.setWalk, api, v); }
void SetStrafeSpeed(void* api, float v)      { Ensure(); SetFloat(g.setStrafe, api, v); }
void SetJumpImpulse(void* api, float v)      { Ensure(); SetFloat(g.setJump, api, v); }
void SetGravityStrength(void* api, float v)  { Ensure(); SetFloat(g.setGrav, api, v); }
float GetRunSpeed(void* api) {
    Ensure();
    return (api && g.getRun) ? Unbox<float>(Il2::Invoke(g.getRun, api, nullptr)) : 0.f;
}
float GetJumpImpulse(void* api) {
    Ensure();
    return (api && g.getJump) ? Unbox<float>(Il2::Invoke(g.getJump, api, nullptr)) : 0.f;
}
float GetWalkSpeed(void* api) {
    Ensure();
    return (api && g.getWalk) ? Unbox<float>(Il2::Invoke(g.getWalk, api, nullptr)) : 0.f;
}
float GetStrafeSpeed(void* api) {
    Ensure();
    return (api && g.getStrafe) ? Unbox<float>(Il2::Invoke(g.getStrafe, api, nullptr)) : 0.f;
}
float GetGravityStrength(void* api) {
    Ensure();
    return (api && g.getGrav) ? Unbox<float>(Il2::Invoke(g.getGrav, api, nullptr)) : 0.f;
}

void TeleportTo(void* localApi, Vec3 pos) {
    Ensure();
    if (!localApi || !g.teleport) return;
    // TeleportTo(Vector3, Quaternion) -- both are by-value structs; keep the current rotation.
    float p[3] = { pos.x, pos.y, pos.z };
    void* rot = g.getRot ? Il2::Invoke(g.getRot, localApi, nullptr) : nullptr;
    float q[4] = { 0, 0, 0, 1 };
    if (rot) {
        auto const* r = reinterpret_cast<float const*>(static_cast<char*>(rot) + 0x10);
        q[0] = r[0]; q[1] = r[1]; q[2] = r[2]; q[3] = r[3];
    }
    void* a[2] = { p, q };
    Il2::Invoke(g.teleport, localApi, a);
}

// THE ACCOUNT BEHIND A PLAYER.
//
// Chain: VRCPlayerApi -> VRCPlayer -> VRC.Player -> APIUser. The two middle classes are renamed
// (the recovery layer aliased them), and on this build APIUser is no longer a FIELD on VRC.Player
// but a getter whose name is obfuscated too. It is found by what it RETURNS: the one zero-argument
// method on VRC.Player handing back a VRC.Core.APIUser. The SDK type's name survives obfuscation,
// so this holds on any build where the chain still exists.
namespace {
    // THE ACCOUNT BEHIND A PLAYER -- and the direction that actually exists.
    //
    // A first version looked for a VRCPlayerApi -> VRCPlayer getter and found none, because the
    // link runs the OTHER WAY: VRC.Player is the component that HOLDS a VRCPlayerApi and a
    // VRCPlayer. So to go from the api object we have to the account, we find the VRC.Player whose
    // VRCPlayerApi field IS our api. Both classes are renamed on this build; the recovery layer
    // aliased them, and the fields are located by TYPE, never by name.
    struct Chain {
        void* playerClass = nullptr;
        int   apiOff = -1;              // VRC.Player -> VRCPlayerApi
        void* playerToApiUser = nullptr; // VRC.Player -> APIUser (getter, found by return type)
        void* userGetId = nullptr;
        void* userGetAvatarId = nullptr;
        bool  tried = false;
        int   probed = 0;
        void* gUserId = nullptr, *gAvatarId = nullptr, *gFileId = nullptr;
        void* gPlatform = nullptr, *gTrust = nullptr;
        // THE WORN AVATAR'S avtr_ ID. Not a string getter on VRCPlayer: it lives one object out,
        // on the ApiAvatar the player holds. avFieldOff is the field on VRCPlayer (or VRC.Player)
        // holding it; avGetId is the 0-arg String getter on that object that returns "avtr_...".
        int   avFieldOff = -1;
        bool  avOnVrcPlayer = true;      // which object avFieldOff indexes into
        void* avGetId = nullptr;
        void* avGetName = nullptr;
        void* avClass = nullptr;        // the ApiAvatar class itself, for building a fresh one
        void* avGetRelease = nullptr;   // ApiAvatar.get_releaseStatus -> "public" / "private"
        int   avReleaseOff = -1;        // ...or the field of that name, which is what exists here
        int   avNameOff    = -1;        // ApiAvatar.name, likewise
        int   avReleaseTries = 0;       // the object is empty until the avatar loads: retried
        int   avNameTries    = 0;
        // WHICH FIELD HOLDS IT, PER OWNER. A single cached offset was wrong: the probe runs on
        // whichever player is nearest, and a field that only the LOCAL player fills reads null on
        // everyone else -- which is how the roster ended up full of file_ ids instead of avtr_.
        // Resolved by field TYPE on each owner class, and BOTH are tried per player.
        int   avOffOnVrcPlayer = -1;
        int   avOffOnPlayer    = -1;
        bool  avOffsetsDone    = false;
        // THE DISPLAY NAME. get_displayName is gone from VRCPlayerApi on this build, and an empty
        // name is worse than a missing one: every page that lists players skips a nameless row,
        // so the roster reads as "nobody here" instead of "the getter moved".
        int   nameOff = -1;             // field offset, on nameOwnerIsVrcPlayer's object
        bool  nameOwnerIsVrcPlayer = true;
        void* nameGetter = nullptr;     // or a 0-arg getter on the held object
        int   nameHeldOff = -1;         // ...reached through this reference field, when needed
        bool  nameProbed = false;
        int   nameTries = 0;
        bool  avProbed = false;
        int   avTries = 0;              // a miss before the avatar loads is NOT a final answer
    };
    Chain ch;

    // The one 0-arg method on `klass` returning `wantClass`. Refuses an ambiguous match.
    void* GetterReturning(void* klass, void* wantClass, char const* what) {
        if (!klass || !wantClass) {
            Log::Writef("Warning", "[Player] %s : classe manquante (owner=%s, cible=%s)",
                        what, klass ? "ok" : "NON", wantClass ? "ok" : "NON");
            return nullptr;
        }
        void* hit = nullptr; int hits = 0;
        void* it = nullptr;
        while (void* mm = Il2::NextMethod(klass, &it)) {
            if (Il2::MethodParamCount(mm) != 0) continue;
            if (Il2::MethodReturnClass(mm) != wantClass) continue;
            hit = mm; ++hits;
        }
        if (hits != 1)
            Log::Writef("Warning", "[Player] %s: %d 0-arg getter(s) of this type -- %s.",
                        what, hits, hits ? "ambiguous, refused" : "none");
        return hits == 1 ? hit : nullptr;
    }

    void EnsureChain() {
        if (ch.tried) return;
        ch.tried = true;

        ch.playerClass = Il2::FindClass("VRC.Player");
        void* apiClass = Il2::FindClass(kApi);
        void* apiUserClass = Il2::FindClass("VRC.Core.APIUser");
        if (!apiUserClass) apiUserClass = Il2::FindClass("APIUser");

        // VRC.Player -> VRCPlayerApi, by field TYPE.
        if (ch.playerClass && apiClass) {
            void* it = nullptr;
            while (void* f = Il2::NextField(ch.playerClass, &it)) {
                if (Il2::FieldIsStatic(f)) continue;
                if (Il2::FieldClass(f) != apiClass) continue;
                ch.apiOff = Il2::FieldOffsetOf(f);
                break;
            }
        }

        // NB: no APIUser lookup. On this build nothing on VRC.Player returns it, and the data we
        // wanted from it (user id, platform, trust) comes from VRCPlayer's string getters instead,
        // identified by the VALUES they return. Chasing APIUser here only produced a warning.
        if (apiUserClass) {
            ch.userGetId       = Il2::FindMethodIn(apiUserClass, "get_id", 0);
            ch.userGetAvatarId = Il2::FindMethodIn(apiUserClass, "get_currentAvatarId", 0);
            if (!ch.userGetAvatarId) ch.userGetAvatarId = Il2::FindMethodIn(apiUserClass, "get_avatarId", 0);
        }

        // EVIDENCE BEFORE GUESSWORK. The APIUser getter was not found by return type, so list what
        // VRC.Player actually exposes -- its fields with their types, and its 0-arg methods with
        // what they return. One shot; this is what names the real route on this build.
        if (!ch.playerToApiUser && ch.playerClass) {
            Log::Info("[Player] --- VRC.Player : champs ---");
            void* it = nullptr; int n = 0;
            while (void* f = Il2::NextField(ch.playerClass, &it)) {
                if (++n > 40) break;
                void* fc = Il2::FieldClass(f);
                char const* fn = Il2::FieldName(f);
                char const* tn = fc ? Il2::ClassName(fc) : nullptr;
                Log::Writef("Info", "[Player]   champ @%-4d %-28s : %s",
                            Il2::FieldOffsetOf(f), fn ? fn : "?", tn ? tn : "?");
            }
            Log::Info("[Player] --- VRC.Player : methodes 0-arg ---");
            it = nullptr; n = 0;
            while (void* mm = Il2::NextMethod(ch.playerClass, &it)) {
                if (Il2::MethodParamCount(mm) != 0) continue;
                if (++n > 40) break;
                void* rc = Il2::MethodReturnClass(mm);
                char const* mn = Il2::MethodName(mm);
                char const* rn = rc ? Il2::ClassName(rc) : nullptr;
                Log::Writef("Info", "[Player]   %-32s -> %s", mn ? mn : "?", rn ? rn : "void");
            }
        }

        Log::Writef("Info", "[Player] chaine : VRC.Player=%s VRCPlayerApi@%d",
                    ch.playerClass ? "ok" : "NON", ch.apiOff);
    }

    // Every live VRC.Player, refreshed at most once a second: the scan is a managed call per
    // player and the roster does not change within a frame.
    std::vector<void*> const& PlayerComponents() {
        static std::vector<void*> cache;
        static double next = -1.0;
        double now = static_cast<double>(GetTickCount64()) / 1000.0;
        if (now < next) return cache;
        next = now + 1.0;
        cache.clear();

        EnsureChain();
        static void* findAll = Il2::FindMethod("UnityEngine.Resources", "FindObjectsOfTypeAll", 1);
        if (!findAll || !ch.playerClass) return cache;
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!cgt || !tgo) return cache;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* ty = reinterpret_cast<CGT>(cgt)(ch.playerClass);
        void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
        if (!sysType) return cache;

        void* a[1] = { sysType };
        void* arr = Il2::Invoke(findAll, nullptr, a);
        int n = Il2::ArrayLength(arr);
        cache.reserve(static_cast<size_t>(n));
        for (int i = 0; i < n; ++i)
            if (void* p = Il2::ArrayAt(arr, i)) cache.push_back(p);
        return cache;
    }

    // The VRC.Player whose VRCPlayerApi field is this api.
    void* PlayerComponentFor(void* api) {
        EnsureChain();
        if (!api || ch.apiOff <= 0) return nullptr;
        for (void* p : PlayerComponents()) {
            void* held = *reinterpret_cast<void**>(static_cast<char*>(p) + ch.apiOff);
            if (held == api) return p;
        }
        return nullptr;
    }

    // Runs once, on a real player: prints what each String getter returns and remembers which
    // one gives the user id and which the avatar id.
    // The VRCPlayer held by a VRC.Player, located by field TYPE (both classes are renamed).
    void* VrcPlayerOf(void* playerComp) {
        static int off = -2;
        if (off == -2) {
            off = -1;
            void* vp = Il2::FindClass("VRCPlayer");
            if (ch.playerClass && vp) {
                void* it = nullptr;
                while (void* f = Il2::NextField(ch.playerClass, &it)) {
                    if (Il2::FieldIsStatic(f)) continue;
                    if (Il2::FieldClass(f) != vp) continue;
                    off = Il2::FieldOffsetOf(f);
                    break;
                }
            }
            Log::Writef("Info", "[Player] VRC.Player -> VRCPlayer @%d", off);
        }
        if (!playerComp || off <= 0) return nullptr;
        return *reinterpret_cast<void**>(static_cast<char*>(playerComp) + off);
    }

    // IDENTIFY THE OBFUSCATED GETTERS BY WHAT THEY RETURN.
    //
    // Every method on VRCPlayer is renamed, so no name says which one is the user id. The VALUES
    // do: "usr_" is a user, "file_" an avatar file, "grp_" a group, "standalonewindows"/"android"
    // a platform, and the trust rank is one of a short known set. Probed once on a live player;
    // the method handles are then kept, so the cost is one pass per session.
    void ProbeStrings(void* playerComp) {
        if (!playerComp || ch.probed >= 2) return;
        ++ch.probed;

        void* vp = VrcPlayerOf(playerComp);
        if (!vp) return;
        void* vpClass = Il2::ClassOfObject(vp);
        void* it = nullptr;
        while (void* mm = Il2::NextMethod(vpClass, &it)) {
            if (Il2::MethodParamCount(mm) != 0) continue;
            void* rc = Il2::MethodReturnClass(mm);
            char const* rn = rc ? Il2::ClassName(rc) : nullptr;
            if (!rn || std::strcmp(rn, "String") != 0) continue;

            std::string v = Il2::ReadString(Il2::Invoke(mm, vp, nullptr));
            if (v.empty()) continue;

            if (!ch.gUserId   && v.rfind("usr_",  0) == 0) { ch.gUserId = mm;   continue; }
            if (!ch.gAvatarId && v.rfind("avtr_", 0) == 0) { ch.gAvatarId = mm; continue; }
            if (!ch.gFileId   && v.rfind("file_", 0) == 0) { ch.gFileId = mm;   continue; }
            if (!ch.gPlatform && (v == "standalonewindows" || v == "android" || v == "ios")) {
                ch.gPlatform = mm; continue;
            }
            if (!ch.gTrust) {
                static char const* kRanks[] = { "VRChat User", "New User", "User", "Known User",
                                                "Trusted User", "Visitor", "Nuisance", "Veteran User" };
                for (char const* r : kRanks)
                    if (v == r) { ch.gTrust = mm; break; }
            }
        }
        Log::Writef("Info", "[Player] getters identifies : userId=%s avatarId=%s fileId=%s plateforme=%s trust=%s",
                    ch.gUserId ? "ok" : "NON", ch.gAvatarId ? "ok" : "NON", ch.gFileId ? "ok" : "NON",
                    ch.gPlatform ? "ok" : "NON", ch.gTrust ? "ok" : "NON");
    }


    // THE WORN AVATAR ID, FOUND BY WHAT IT HOLDS.
    //
    // No string getter on VRCPlayer returns an "avtr_" id on this build -- the probe above proves
    // it, which is why every archive feature was blind. The C# mod's route is the right one and it
    // still exists: the player holds an ApiAvatar OBJECT, and that object's id is the avatar.
    //
    // ApiAvatar's own name is obfuscated here too, so the object is not found by type. It is found
    // the same way every other member was: by VALUE. Each object-typed field on VRCPlayer and on
    // VRC.Player is examined once, and the one carrying a 0-arg String getter that returns an
    // "avtr_" id IS the ApiAvatar. One pass per session; the field offset and the getter are kept.
    void ProbeAvatar(void* playerComp) {
        if (ch.avProbed || !playerComp) return;
        // Paced: scanning every reference field of VRCPlayer and calling its string getters is not
        // free, and the avatar takes seconds to load.
        static double nextTry = 0.0;
        double now = static_cast<double>(GetTickCount64()) / 1000.0;
        if (now < nextTry) return;
        nextTry = now + 1.0;

        struct Candidate { void* obj; void* klass; bool onVrcPlayer; };
        auto tryObject = [&](void* owner, bool onVrcPlayer) -> bool {
            if (!owner) return false;
            void* ownerClass = Il2::ClassOfObject(owner);
            if (!ownerClass) return false;
            void* it = nullptr;
            while (void* f = Il2::NextField(ownerClass, &it)) {
                if (Il2::FieldIsStatic(f)) continue;
                void* fc = Il2::FieldClass(f);
                if (!fc || Il2::ClassIsValueType(fc)) continue;   // only reference fields hold objects
                int off = Il2::FieldOffsetOf(f);
                if (off <= 0) continue;
                void* held = *reinterpret_cast<void**>(static_cast<char*>(owner) + off);
                if (!held) continue;

                void* heldClass = Il2::ClassOfObject(held);
                if (!heldClass) continue;
                void* mit = nullptr;
                void* getId = nullptr, *getName = nullptr;
                while (void* mm = Il2::NextMethod(heldClass, &mit)) {
                    if (Il2::MethodParamCount(mm) != 0) continue;
                    void* rc = Il2::MethodReturnClass(mm);
                    char const* rn = rc ? Il2::ClassName(rc) : nullptr;
                    if (!rn || std::strcmp(rn, "String") != 0) continue;
                    std::string v = Il2::ReadString(Il2::Invoke(mm, held, nullptr));
                    if (v.rfind("avtr_", 0) == 0) { getId = mm; continue; }
                    // The display name sits beside the id on the same object; it is whatever other
                    // non-empty string that is not another VRChat id.
                    if (!getName && !v.empty() && v.rfind("usr_", 0) != 0 &&
                        v.rfind("file_", 0) != 0 && v.rfind("wrld_", 0) != 0)
                        getName = mm;
                }
                if (!getId) continue;

                // CLASSIFY THE REST BY VALUE TOO. id, name and releaseStatus are all getters here
                // and all three are renamed -- a dump proved the class's only string FIELDS are
                // the author id and the asset/image urls. But the VALUES are unmistakable:
                // "public"/"private" is a two-word set, and the name is the remaining non-empty
                // string that is neither a url nor a VRChat id.
                {
                    void* mit2 = nullptr;
                    while (void* mm = Il2::NextMethod(heldClass, &mit2)) {
                        if (Il2::MethodParamCount(mm) != 0) continue;
                        void* rc = Il2::MethodReturnClass(mm);
                        char const* rn = rc ? Il2::ClassName(rc) : nullptr;
                        if (!rn || std::strcmp(rn, "String") != 0) continue;
                        if (mm == getId) continue;
                        std::string v = Il2::ReadString(Il2::Invoke(mm, held, nullptr));
                        if (v.empty()) continue;
                        if (!ch.avGetRelease && (v == "public" || v == "private")) {
                            ch.avGetRelease = mm;
                            continue;
                        }
                        if (v.rfind("http", 0) == 0) continue;      // asset and image urls
                        if (v.rfind("usr_", 0) == 0) continue;      // the author
                        if (v.rfind("avtr_", 0) == 0) continue;
                        if (v.rfind("file_", 0) == 0) continue;
                        if (v.rfind("wrld_", 0) == 0) continue;
                        if (!ch.avGetName) ch.avGetName = mm;       // what is left is the name
                    }
                }

                ch.avFieldOff   = off;
                ch.avOnVrcPlayer = onVrcPlayer;
                ch.avGetId      = getId;
                ch.avGetName    = getName;
                ch.avClass      = heldClass;
                return true;
            }
            return false;
        };

        bool found = tryObject(VrcPlayerOf(playerComp), true) || tryObject(playerComp, false);

        // A MISS IS NOT A FINAL ANSWER. The ApiAvatar is attached when the avatar finishes loading,
        // so a probe that runs during the join finds nothing -- and a one-shot probe then gave up
        // for the whole session, which is how the avatar id went from readable to absent between
        // two runs of the same build. Keep trying for a while; only success, or a long run of
        // misses with a player actually present, settles it.
        if (found) {
            ch.avProbed = true;
            Log::Writef("Info", "[Player] worn avatar: ApiAvatar @%d on %s, id readable.",
                        ch.avFieldOff, ch.avOnVrcPlayer ? "VRCPlayer" : "VRC.Player");
            return;
        }
        if (++ch.avTries >= 20) {
            ch.avProbed = true;
            Log::Warn("[Player] worn avatar: NO object returns an 'avtr_' id after 20 tries.");
        }
    }


    // The field on `ownerClass` that HOLDS an ApiAvatar.
    //
    // Matching the field's DECLARED type against the avatar's RUNTIME class does not work and
    // cost a regression: the field is declared as a base type, so the comparison failed on every
    // owner and the whole roster lost its avatar ids. The declared type is therefore accepted
    // when it is the runtime class OR any ancestor of it -- which is what "holds one" means.
    bool ClassIsOrBaseOf(void* maybeBase, void* k) {
        for (void* c = k; c; c = Il2::ClassParent(c)) if (c == maybeBase) return true;
        return false;
    }

    int AvatarFieldOn(void* ownerClass) {
        if (!ownerClass || !ch.avClass) return -1;
        void* it = nullptr;
        while (void* f = Il2::NextField(ownerClass, &it)) {
            if (Il2::FieldIsStatic(f)) continue;
            void* fc = Il2::FieldClass(f);
            if (!fc || Il2::ClassIsValueType(fc)) continue;
            if (!ClassIsOrBaseOf(fc, ch.avClass)) continue;
            return Il2::FieldOffsetOf(f);
        }
        return -1;
    }

    void EnsureAvatarOffsets(void* playerComp) {
        if (ch.avOffsetsDone || !playerComp) return;
        if (!ch.avClass) return;          // the probe has not identified it yet: try again later
        ch.avOffsetsDone = true;
        if (void* vp = VrcPlayerOf(playerComp)) ch.avOffOnVrcPlayer = AvatarFieldOn(Il2::ClassOfObject(vp));
        ch.avOffOnPlayer = AvatarFieldOn(Il2::ClassOfObject(playerComp));
        for (void* k = ch.avClass; k; k = Il2::ClassParent(k)) {
            if (!ch.avGetRelease) ch.avGetRelease = Il2::FindMethodIn(k, "get_releaseStatus", 0);
            if (ch.avReleaseOff <= 0) ch.avReleaseOff = Il2::FieldOffset(k, "releaseStatus");
            if (ch.avNameOff <= 0)    ch.avNameOff    = Il2::FieldOffset(k, "name");
            if (ch.avGetRelease && ch.avNameOff > 0) break;
        }
        Log::Writef("Info",
                    "[Player] ApiAvatar : VRCPlayer@%d, VRC.Player@%d, releaseStatus=%s, name=%s.",
                    ch.avOffOnVrcPlayer, ch.avOffOnPlayer,
                    ch.avGetRelease ? "getter" : (ch.avReleaseOff > 0 ? "champ" : "NON"),
                    ch.avNameOff > 0 ? "champ" : (ch.avGetName ? "getter" : "NON"));


    }

    // The ApiAvatar this player is wearing: whichever owner actually holds one right now. A
    // player whose avatar is still loading simply has none, which is not the same as "no field".
    void* ApiAvatarOf(void* playerComp) {
        ProbeAvatar(playerComp);
        EnsureAvatarOffsets(playerComp);
        if (!playerComp) return nullptr;

        // THE PROBE'S OWN OFFSET FIRST. It was measured on a live object that actually answered
        // with an avtr_ id, so it is the one answer that is known to be right; the type search
        // below only widens it to the owner the probe did not happen to run on.
        if (ch.avFieldOff > 0) {
            void* owner = ch.avOnVrcPlayer ? VrcPlayerOf(playerComp) : playerComp;
            if (owner) {
                void* av = *reinterpret_cast<void**>(static_cast<char*>(owner) + ch.avFieldOff);
                if (av) return av;
            }
        }
        if (ch.avOffOnVrcPlayer > 0) {
            if (void* vp = VrcPlayerOf(playerComp)) {
                void* av = *reinterpret_cast<void**>(static_cast<char*>(vp) + ch.avOffOnVrcPlayer);
                if (av) return av;
            }
        }
        if (ch.avOffOnPlayer > 0) {
            void* av = *reinterpret_cast<void**>(static_cast<char*>(playerComp) + ch.avOffOnPlayer);
            if (av) return av;
        }
        return nullptr;
    }

    // THE DISPLAY NAME, FOUND BY THE MEMBER NAME.
    //
    // VRCPlayerApi lost get_displayName the same silent way it lost get_isLocal, so the name has
    // to come from the player objects. The CLASS names here are obfuscated but the MEMBER names
    // are not, so a field or getter literally called "displayName" is still the right thing to
    // look for -- and an EMPTY name is not cosmetic: every list that shows players skips a
    // nameless row, so the whole roster reads as "nobody is here".
    void ProbeName(void* playerComp) {
        if (ch.nameProbed || !playerComp) return;
        if (++ch.nameTries > 20) {
            ch.nameProbed = true;
            Log::Warn("[Player] display name not found after 20 tries.");
            return;
        }

        auto tryOwner = [&](void* owner, bool isVrcPlayer) -> bool {
            if (!owner) return false;
            void* k = Il2::ClassOfObject(owner);
            if (!k) return false;

            for (void* c2 = k; c2; c2 = Il2::ClassParent(c2)) {
                int off = Il2::FieldOffset(c2, "displayName");
                if (off <= 0) off = Il2::FieldOffset(c2, "_displayName");
                if (off > 0) {
                    ch.nameOff = off; ch.nameOwnerIsVrcPlayer = isVrcPlayer;
                    ch.nameHeldOff = -1; ch.nameGetter = nullptr;
                    return true;
                }
                if (void* g = Il2::FindMethodIn(c2, "get_displayName", 0)) {
                    ch.nameGetter = g; ch.nameOwnerIsVrcPlayer = isVrcPlayer;
                    ch.nameHeldOff = -1; ch.nameOff = -1;
                    return true;
                }
            }

            // ...or on something it holds: the account object is where it actually lives.
            void* it = nullptr;
            while (void* f = Il2::NextField(k, &it)) {
                if (Il2::FieldIsStatic(f)) continue;
                void* fc = Il2::FieldClass(f);
                if (!fc || Il2::ClassIsValueType(fc)) continue;
                int off = Il2::FieldOffsetOf(f);
                if (off <= 0) continue;
                void* held = *reinterpret_cast<void**>(static_cast<char*>(owner) + off);
                if (!held) continue;
                for (void* c2 = Il2::ClassOfObject(held); c2; c2 = Il2::ClassParent(c2)) {
                    int noff = Il2::FieldOffset(c2, "displayName");
                    if (noff > 0) {
                        ch.nameHeldOff = off; ch.nameOff = noff;
                        ch.nameGetter = nullptr; ch.nameOwnerIsVrcPlayer = isVrcPlayer;
                        return true;
                    }
                    if (void* g = Il2::FindMethodIn(c2, "get_displayName", 0)) {
                        ch.nameHeldOff = off; ch.nameGetter = g;
                        ch.nameOff = -1; ch.nameOwnerIsVrcPlayer = isVrcPlayer;
                        return true;
                    }
                }
            }
            return false;
        };

        bool found = tryOwner(playerComp, false) || tryOwner(VrcPlayerOf(playerComp), true);
        if (!found) return;           // retried: the account object arrives a moment later
        ch.nameProbed = true;
        Log::Writef("Info", "[Player] display name: %s on %s%s.",
                    ch.nameGetter ? "getter get_displayName" : "champ displayName",
                    ch.nameOwnerIsVrcPlayer ? "VRCPlayer" : "VRC.Player",
                    ch.nameHeldOff > 0 ? " (via a held object)" : "");
    }

    std::string NameOf(void* playerComp) {
        ProbeName(playerComp);
        if (!playerComp) return {};
        void* owner = ch.nameOwnerIsVrcPlayer ? VrcPlayerOf(playerComp) : playerComp;
        if (!owner) return {};
        if (ch.nameHeldOff > 0) {
            owner = *reinterpret_cast<void**>(static_cast<char*>(owner) + ch.nameHeldOff);
            if (!owner) return {};
        }
        if (ch.nameGetter) return Il2::ReadString(Il2::Invoke(ch.nameGetter, owner, nullptr));
        if (ch.nameOff > 0)
            return Il2::ReadString(*reinterpret_cast<void**>(static_cast<char*>(owner) + ch.nameOff));
        return {};
    }

    std::string NameFromPlayerObjects(void* api) { return NameOf(PlayerComponentFor(api)); }

    // THE WORN AVATAR, AND THE TWO OBJECTS BETWEEN US AND IT.
    //
    // What the probe finds on the player is the avatar MANAGER, not the model. A listing of its
    // getters settled it: GameObject, Vector3, PerformanceRating, Awake -- and several returning
    // ApiAvatar. It answers with an "avtr_" id because it FORWARDS one, which is why stopping
    // there read the GameObject's tag ('Untagged') as the avatar name and never found the public
    // or private status at all.
    //
    // So the model is one hop further, and ApiAvatar's own CLASS NAME survived obfuscation -- it
    // prints plainly as a return type -- which is what makes the hop findable by TYPE.
    void* ModelFromManager(void* manager) {
        if (!manager) return nullptr;
        static void* getModel = nullptr;
        static bool  looked = false;
        if (!looked) {
            // MATCHED BY NAME, NOT BY CLASS POINTER. FindClass treats the namespace as a
            // constraint and handed back a DIFFERENT ApiAvatar than the one these getters
            // return, so comparing pointers found zero of them.
            void* want = nullptr;
            {
                for (void* k = Il2::ClassOfObject(manager); k && !getModel; k = Il2::ClassParent(k)) {
                    void* it = nullptr;
                    while (void* mm = Il2::NextMethod(k, &it)) {
                        if (Il2::MethodParamCount(mm) != 0) continue;
                        void* rc = Il2::MethodReturnClass(mm);
                        char const* rn = rc ? Il2::ClassName(rc) : nullptr;
                        if (!rn || std::strcmp(rn, "ApiAvatar") != 0) continue;
                        want = rc;
                        // Several getters return one; the first that actually hands an object
                        // back is the one in use. Until one does, nothing is cached -- the
                        // avatar is simply not loaded yet.
                        if (!Il2::Invoke(mm, manager, nullptr)) continue;
                        getModel = mm;
                        break;
                    }
                }
            }
            if (getModel) {
                looked = true;
                Log::Info("[Player] ApiAvatar: model reached from the avatar manager.");
            } else {
                // A silent miss here reads as "no name, no status" downstream, so it is counted
                // and explained: is the CLASS missing, or does nothing on the manager hand one
                // back yet (the avatar is still loading)?
                static int misses = 0;
                if (++misses == 30) {
                    int candidates = 0;
                    if (want) {
                        for (void* k = Il2::ClassOfObject(manager); k; k = Il2::ClassParent(k)) {
                            void* it = nullptr;
                            while (void* mm = Il2::NextMethod(k, &it)) {
                                if (Il2::MethodParamCount(mm) != 0) continue;
                                if (Il2::MethodReturnClass(mm) == want) ++candidates;
                            }
                        }
                    }
                    // Said ONCE, calmly: the worn-avatar NAME and public/private status are
                    // resolved by the desktop client (one VRChat API call with its own session),
                    // not here. The in-game objects only give the avtr_ id, which is published and
                    // is the one thing only the mod can know. So this is a note, not a warning.
                    Log::Writef("Info",
                                "[Player] ApiAvatar : modele natif non expose (%s, %d getter(s)) -- "
                                "name and status come from the client, by design.",
                                want ? "classe trouvee" : "classe cachee", candidates);
                }
            }
        }
        return getModel ? Il2::Invoke(getModel, manager, nullptr) : nullptr;
    }

    // A 0-arg string getter on the model: by NAME first, then by what it RETURNS. ApiAvatar's
    // members have survived so far, and the by-value path is what keeps this working the day
    // they do not.
    void* ModelString(void* model, char const* prop, char const* const* wantValues, int nWant) {
        if (!model) return nullptr;
        for (void* k = Il2::ClassOfObject(model); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, prop, 0)) return m;
        if (!wantValues) return nullptr;
        for (void* k = Il2::ClassOfObject(model); k; k = Il2::ClassParent(k)) {
            void* it = nullptr;
            while (void* mm = Il2::NextMethod(k, &it)) {
                if (Il2::MethodParamCount(mm) != 0) continue;
                void* rc = Il2::MethodReturnClass(mm);
                char const* rn = rc ? Il2::ClassName(rc) : nullptr;
                if (!rn || std::strcmp(rn, "String") != 0) continue;
                std::string v = Il2::ReadString(Il2::Invoke(mm, model, nullptr));
                for (int i = 0; i < nWant; ++i)
                    if (v == wantValues[i]) return mm;
            }
        }
        return nullptr;
    }

    std::string AvatarIdOf(void* playerComp) {
        void* mgr = ApiAvatarOf(playerComp);
        if (!mgr) return {};
        if (void* model = ModelFromManager(mgr)) {
            static void* getId = nullptr;
            if (!getId)
                for (void* k = Il2::ClassOfObject(model); k && !getId; k = Il2::ClassParent(k))
                    getId = Il2::FindMethodIn(k, "get_id", 0);
            if (getId) {
                std::string v = Il2::ReadString(Il2::Invoke(getId, model, nullptr));
                if (v.rfind("avtr_", 0) == 0) return v;
            }
        }
        // The manager forwards the same id, and that is the one the probe proved readable.
        return ch.avGetId ? Il2::ReadString(Il2::Invoke(ch.avGetId, mgr, nullptr)) : std::string();
    }

    std::string AvatarNameOf(void* playerComp) {
        void* model = ModelFromManager(ApiAvatarOf(playerComp));
        if (!model) return {};
        if (!ch.avGetName && ch.avNameTries < 60) {
            ++ch.avNameTries;
            ch.avGetName = ModelString(model, "get_name", nullptr, 0);
            if (ch.avGetName) Log::Info("[Player] avatar name: readable on the ApiAvatar model.");
        }
        if (!ch.avGetName) return {};
        std::string v = Il2::ReadString(Il2::Invoke(ch.avGetName, model, nullptr));
        return LooksObfuscated(v) ? std::string() : v;   // an internal name is not a title
    }

    // "public" or "private". The client needs it to know whether FORCE CLONE can work at all:
    // offering the action on a private avatar only produces a refusal a second later.
    std::string AvatarReleaseOf(void* playerComp) {
        void* model = ModelFromManager(ApiAvatarOf(playerComp));
        if (!model) return {};
        if (!ch.avGetRelease && ch.avReleaseTries < 60) {
            ++ch.avReleaseTries;
            static char const* kStatuses[] = { "public", "private" };
            ch.avGetRelease = ModelString(model, "get_releaseStatus", kStatuses, 2);
            if (ch.avGetRelease)
                Log::Info("[Player] public/private status: readable on the ApiAvatar model.");
        }
        return ch.avGetRelease ? Il2::ReadString(Il2::Invoke(ch.avGetRelease, model, nullptr))
                               : std::string();
    }

    std::string CallGetter(void* api, void* getter) {
        if (!getter) return {};
        void* p = PlayerComponentFor(api);
        if (!p) return {};
        ProbeStrings(p);
        void* vp = VrcPlayerOf(p);
        return vp ? Il2::ReadString(Il2::Invoke(getter, vp, nullptr)) : std::string();
    }

    void* ApiUserOf(void* api) {
        void* p = PlayerComponentFor(api);
        if (!p || !ch.playerToApiUser) return nullptr;
        return Il2::Invoke(ch.playerToApiUser, p, nullptr);
    }
}

std::string UserId(void* api) {
    void* p = PlayerComponentFor(api);
    if (p) ProbeStrings(p);
    return CallGetter(api, ch.gUserId);
}

std::string AvatarId(void* api) {
    void* p = PlayerComponentFor(api);
    if (!p) return {};
    ProbeStrings(p);

    // THE REAL avtr_ ID, AND NOTHING ELSE.
    //
    // A file id used to be returned as a fallback, and that was worse than nothing: the desktop
    // client shows this value as the avatar, builds "vrchat.com/home/avatar/<id>" from it and
    // asks VRChat whether it is public -- all of which are wrong for a file id. A whole page of
    // "file_..." rows with a dead Info button is what that fallback actually produced. An avatar
    // still loading has no id yet, and saying so is the honest answer.
    if (std::string v = AvatarIdOf(p); !v.empty()) return v;
    if (std::string v = CallGetter(api, ch.gAvatarId); v.rfind("avtr_", 0) == 0) return v;
    return {};
}

std::string AvatarRelease(void* api) {
    void* p = PlayerComponentFor(api);
    return p ? AvatarReleaseOf(p) : std::string();
}

void* ApiAvatarClass() {
    // Known only once a real player has been probed: the class is obfuscated, so it is identified
    // by the object that answered with an "avtr_" id, not by any name.
    for (void* p : PlayerComponents()) { ProbeAvatar(p); if (ch.avClass) break; }
    return ch.avClass;
}

std::string AvatarName(void* api) {
    void* p = PlayerComponentFor(api);
    return p ? AvatarNameOf(p) : std::string();
}

std::string Platform(void* api) { return CallGetter(api, ch.gPlatform); }
std::string Trust(void* api)    { return CallGetter(api, ch.gTrust); }

void ZeroVelocity(void* api) {
    Ensure();
    if (!api || !g.setVel) return;
    float v[3] = { 0, 0, 0 };          // Vector3 by value -> pass its address
    void* a[1] = { v };
    Il2::Invoke(g.setVel, api, a);
}

// THE RIG, NOT THE AVATAR.
//
// Fly works by writing a transform every frame, and it has to be the VRCPlayer[Local] ROOT: that
// object carries the character controller and the desktop camera, so moving it moves the player.
// Walking up from the VRC.Player component reaches it; the name is then checked, because a root
// that is not VRCPlayer[Local] means the hierarchy changed and writing it would move the wrong
// thing.
void* LocalRoot() {
    void* go = GameObjectOf(LocalApi());
    if (!Unity::IsAlive(go)) return nullptr;
    void* tr = Unity::Transform(go);
    void* root = tr ? Unity::Root(tr) : nullptr;
    if (!Unity::IsAlive(root)) return nullptr;

    static bool logged = false;
    std::string n = Unity::Name(Unity::GameObjectOf(root));
    if (n.rfind("VRCPlayer[Local]", 0) != 0) {
        if (!logged) {
            logged = true;
            Log::Writef("Warning", "[Player] unexpected local root: '%s' -- flight disabled.",
                        n.c_str());
        }
        return nullptr;
    }
    if (!logged) { logged = true; Log::Writef("Info", "[Player] racine locale : '%s'.", n.c_str()); }
    return root;
}

// THE PLAYER'S GAMEOBJECT, WITHOUT get_gameObject.
//
// That getter is absent from VRCPlayerApi on this build, which quietly disabled every feature that
// needed the player's objects (anti-crash scanning, self hide). The VRC.Player component sitting
// on the same player IS a UnityEngine.Component, so its own gameObject is the one we want.
void* GameObjectOf(void* api) {
    EnsureChain();
    void* comp = PlayerComponentFor(api);
    return comp ? Unity::GameObjectOf(comp) : nullptr;
}

// THE LOCAL PLAYER, WITHOUT get_isLocal.
//
// VRCPlayerApi.get_isLocal does NOT resolve on this build, so the obvious route -- ask each api
// whether it is local -- silently answered "none", and every local feature (fly, speeds, orbit,
// self hide) did nothing at all because LocalApi() always returned null.
//
// The reliable handle is the one the class recovery already leans on: VRCPlayer carries a STATIC
// field typed as itself, holding the local VRCPlayer. The VRC.Player that owns that VRCPlayer is
// the local one, and its VRCPlayerApi field is the answer.
void* LocalApi() {
    Ensure();
    EnsureChain();

    static void* selfField = nullptr;
    static bool  looked = false;
    if (!looked) {
        looked = true;
        if (void* vpClass = Il2::FindClass("VRCPlayer")) {
            void* it = nullptr;
            while (void* f = Il2::NextField(vpClass, &it)) {
                if (!Il2::FieldIsStatic(f)) continue;
                if (Il2::FieldClass(f) != vpClass) continue;
                selfField = f;
                break;
            }
        }
        Log::Writef(selfField ? "Info" : "Warning",
                    selfField ? "[Player] local player: static VRCPlayer field found."
                              : "[Player] local player: static VRCPlayer field NOT FOUND.");
    }
    if (!selfField || ch.apiOff <= 0) return nullptr;

    static void* getStatic = Il2::Export("il2cpp_field_static_get_value");
    if (!getStatic) return nullptr;
    using GSV = void(*)(void*, void*);
    void* localVp = nullptr;
    reinterpret_cast<GSV>(getStatic)(selfField, &localVp);
    if (!Unity::IsAlive(localVp)) return nullptr;

    for (void* p : PlayerComponents()) {
        if (VrcPlayerOf(p) != localVp) continue;
        return *reinterpret_cast<void**>(static_cast<char*>(p) + ch.apiOff);
    }
    return nullptr;
}

std::vector<Info> All() {
    Ensure();
    if (g.reportPending) {
        g.reportPending = false;
        auto ok = [](void* p) { return p ? "ok" : "NON"; };
        Log::Writef("Info",
                    "[Player] VRCPlayerApi : AllPlayers=%s displayName=%s playerId=%s isMaster=%s "
                    "inVR=%s GetPosition=%s TeleportTo=%s",
                    ok(g.getAll), ok(g.getName), ok(g.getId), ok(g.isMaster), ok(g.isVR),
                    ok(g.getPos), ok(g.teleport));
    }
    std::vector<Info> out;
    if (!g.getAll) return out;

    void* list = Il2::Invoke(g.getAll, nullptr, nullptr);
    if (!list) return out;

    // VRCPlayerApi.AllPlayers is a List<VRCPlayerApi>: items array at +0x10, count at +0x18.
    void* items = *reinterpret_cast<void**>(static_cast<char*>(list) + 0x10);
    int   count = *reinterpret_cast<int32_t*>(static_cast<char*>(list) + 0x18);
    if (!items || count <= 0 || count > 256) return out;

    // Resolve the local api ONCE: without get_isLocal it is a scan, and asking per player would
    // turn the roster into a quadratic walk.
    void* localApi = LocalApi();

    out.reserve(static_cast<size_t>(count));
    for (int i = 0; i < count; ++i) {
        void* api = Il2::ArrayAt(items, i);
        if (!api) continue;
        Info info;
        info.api     = api;
        info.id      = Id(api);
        info.name    = DisplayName(api);
        info.isLocal = (api == localApi);
        if (g.isMaster) info.isMaster = Unbox<unsigned char>(Il2::Invoke(g.isMaster, api, nullptr)) != 0;
        if (g.isVR)     info.inVR     = Unbox<unsigned char>(Il2::Invoke(g.isVR, api, nullptr)) != 0;
        info.pos     = Position(api);
        info.hasPos  = true;
        info.userId  = UserId(api);
        info.avatarId  = AvatarId(api);
        info.avatarName = AvatarName(api);
        info.release   = AvatarRelease(api);
        info.trust   = Trust(api);
        {
            std::string pf = Platform(api);
            info.platform = (pf == "standalonewindows") ? "PC" : (pf.empty() ? "" : "Quest");
        }
        out.push_back(std::move(info));
    }
    return out;
}

}
