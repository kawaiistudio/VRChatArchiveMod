#include "trueview.hpp"
#include "bridge.hpp"
#include "player.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

// See trueview.hpp for what this does. Everything this module needs that the shared helpers do not
// offer (transform pose, Animator bones, renderer/material checks, GC handles) lives in this file on
// purpose: unity.cpp is owned by the QuickMenu work and must not be edited from here.
namespace VRCA {

namespace {

constexpr char const* kCloneName  = "Avatar_TrueViewBackup";
constexpr char const* kHolderName = "TrueView_Sleep";
constexpr double      kSurveyPeriod = 0.25;   // each remote player is inspected 4x a second
constexpr double      kPruneEvery   = 1.0;

// ================================================================== GC-safe references
// An object kept across frames must be held through a GC handle. The il2cpp collector does not scan
// this DLL's heap, so a bare pointer to a managed wrapper can be collected under us and the next call
// reads freed memory. A Unity object destroyed by the game stays a valid managed object (a "fake
// null" every call refuses), so a handle never dangles -- calls on it just come back null.
struct GcApi {
    using NewFn  = uint32_t (*)(void*, bool);
    using GetFn  = void* (*)(uint32_t);
    using FreeFn = void (*)(uint32_t);
    NewFn  make = nullptr;
    GetFn  get  = nullptr;
    FreeFn drop = nullptr;
    bool   tried = false;

    bool Ok() {
        if (!tried) {
            tried = true;
            make = reinterpret_cast<NewFn>(Il2::Export("il2cpp_gchandle_new"));
            get  = reinterpret_cast<GetFn>(Il2::Export("il2cpp_gchandle_get_target"));
            drop = reinterpret_cast<FreeFn>(Il2::Export("il2cpp_gchandle_free"));
            if (!make || !get || !drop)
                Log::Warn("[TrueView] gchandle exports missing -- raw references (fragile if the GC runs).");
        }
        return make && get && drop;
    }
};
GcApi g_gc;

class Ref {
public:
    Ref() = default;
    Ref(Ref const&) = delete;
    Ref& operator=(Ref const&) = delete;
    Ref(Ref&& o) noexcept : m_h(o.m_h), m_raw(o.m_raw) { o.m_h = 0; o.m_raw = nullptr; }
    Ref& operator=(Ref&& o) noexcept {
        if (this != &o) { Reset(); m_h = o.m_h; m_raw = o.m_raw; o.m_h = 0; o.m_raw = nullptr; }
        return *this;
    }
    ~Ref() { Reset(); }

    void Set(void* obj) {
        if (obj && obj == Get()) return;
        Reset();
        if (!obj) return;
        if (g_gc.Ok()) m_h = g_gc.make(obj, false);
        else           m_raw = obj;
    }
    [[nodiscard]] void* Get() const { return m_h ? g_gc.get(m_h) : m_raw; }
    void Reset() {
        if (m_h && g_gc.drop) g_gc.drop(m_h);
        m_h = 0;
        m_raw = nullptr;
    }
    explicit operator bool() const { return m_h != 0 || m_raw != nullptr; }

private:
    uint32_t m_h = 0;
    void*    m_raw = nullptr;
};

// ================================================================== small math
// Same memory layout as UnityEngine.Vector3 / Quaternion, so they are passed to Invoke as-is.
struct V3 { float x, y, z; };
struct Q4 { float x, y, z, w; };

Q4 Mul(Q4 a, Q4 b) {
    return {
        a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
        a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
    };
}
Q4 AxisAngle(float ax, float ay, float az, float deg) {
    float h = deg * 3.14159265358979f / 360.f;
    float s = std::sin(h);
    return { ax * s, ay * s, az * s, std::cos(h) };
}
// Quaternion.Euler: Unity applies Z, then X, then Y (q = qY * qX * qZ). Computed here rather than
// called: a trivial forwarder like Euler is exactly what this build stripped elsewhere.
Q4 Euler(float x, float y, float z) {
    return Mul(Mul(AxisAngle(0, 1, 0, y), AxisAngle(1, 0, 0, x)), AxisAngle(0, 0, 1, z));
}

// ================================================================== method handles
struct M {
    // players. On build 25686233 VRCPlayerApi.isLocal / .gameObject are NOT properties (both resolved
    // "NON" in the first in-game test), so each has fallbacks: the local player comes from
    // Networking.get_LocalPlayer, the player's GameObject from a field, or from the VRC.Player
    // component that HOLDS this VRCPlayerApi (the link only goes that way -- see PlayerGameObject).
    void* allPlayers = nullptr, *playerId = nullptr, *isLocal = nullptr, *apiGameObject = nullptr;
    void* localPlayer = nullptr;
    int   apiIsLocalOff = -1, apiGoOff = -1;
    void* findAll = nullptr, *tVrcPlayer = nullptr;
    int   playerApiOff = -1;
    // objects
    void* instanceId = nullptr, *destroyImmediate = nullptr, *instantiateUnder = nullptr;
    void* goCtor = nullptr, *goComponentsInChildren = nullptr;
    // transforms
    void* trFind = nullptr, *trGetPos = nullptr, *trSetPos = nullptr, *trGetRot = nullptr, *trSetRot = nullptr;
    void* trSetLocalPos = nullptr, *trSetLocalRot = nullptr;
    // behaviours / components
    void* behGetEnabled = nullptr, *behSetEnabled = nullptr;
    void* animIsHuman = nullptr, *animBone = nullptr, *animSetCulling = nullptr;
    void* rendGetEnabled = nullptr, *rendSetEnabled = nullptr, *rendSetForceOff = nullptr;
    void* rendSharedMats = nullptr, *rendSharedMat = nullptr, *matShader = nullptr;
    void* colGetEnabled = nullptr, *colSetEnabled = nullptr;
    void* audGetMute = nullptr, *audSetMute = nullptr;
    // System.Type objects for GetComponentsInChildren(Type, bool)
    void* tRenderer = nullptr, *tCollider = nullptr, *tMonoBehaviour = nullptr;
    void* tAudioSource = nullptr, *tCamera = nullptr, *tLight = nullptr;
    bool  ready = false;
};
M g;

char const* SafeName(void* klass) {
    char const* n = klass ? Il2::ClassName(klass) : nullptr;
    return n ? n : "";
}

// The overload of `methodName` on `className` whose parameter classes are exactly `params`
// (short class names). Finding by shape rather than by arity alone keeps generic and same-arity
// overloads (Instantiate<T>, GetComponentsInChildren<T>(bool, List<T>)) out.
void* MethodWithParams(char const* className, char const* methodName, std::vector<char const*> const& params) {
    void* k = Il2::FindClass(className);
    if (!k) return nullptr;
    void* it = nullptr;
    while (void* m = Il2::NextMethod(k, &it)) {
        char const* n = Il2::MethodName(m);
        if (!n || std::strcmp(n, methodName) != 0) continue;
        if (Il2::MethodParamCount(m) != static_cast<int>(params.size())) continue;
        bool same = true;
        for (size_t i = 0; i < params.size() && same; ++i)
            same = std::strcmp(SafeName(Il2::MethodParamClass(m, static_cast<int>(i))), params[i]) == 0;
        if (same) return m;
    }
    return nullptr;
}

void* TypeObject(char const* className) {
    void* k = Il2::FindClass(className);
    static void* cgt = Il2::Export("il2cpp_class_get_type");
    static void* tgo = Il2::Export("il2cpp_type_get_object");
    if (!k || !cgt || !tgo) return nullptr;
    using CGT = void* (*)(void*);
    using TGO = void* (*)(void*);
    void* ty = reinterpret_cast<CGT>(cgt)(k);
    return ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
}

void Ensure() {
    if (g.ready) return;
    g.ready = true;

    constexpr char const* kApi = "VRC.SDKBase.VRCPlayerApi";
    g.allPlayers    = Il2::FindMethod(kApi, "get_AllPlayers", 0);
    g.playerId      = Il2::FindMethod(kApi, "get_playerId", 0);
    // get_isLocal and get_gameObject are GONE from VRCPlayerApi on build 25686233. Both answers
    // come from the player layer instead (static VRCPlayer self-field, and the VRC.Player
    // component's own gameObject), so their absence must not disable the module.
    g.isLocal       = Il2::FindMethod(kApi, "get_isLocal", 0);
    g.apiGameObject = Il2::FindMethod(kApi, "get_gameObject", 0);
    g.localPlayer   = Il2::FindMethod("VRC.SDKBase.Networking", "get_LocalPlayer", 0);
    if (void* apiClass = Il2::FindClass(kApi)) {
        g.apiIsLocalOff = Il2::FieldOffset(apiClass, "isLocal");
        g.apiGoOff      = Il2::FieldOffset(apiClass, "gameObject");
        // VRC.Player (aliased by recovery) holds the VRCPlayerApi: find that field by its TYPE.
        if (void* playerClass = Il2::FindClass("VRC.Player")) {
            void* it = nullptr;
            while (void* f = Il2::NextField(playerClass, &it)) {
                if (Il2::FieldIsStatic(f) || Il2::FieldClass(f) != apiClass) continue;
                g.playerApiOff = Il2::FieldOffsetOf(f);
                break;
            }
            g.tVrcPlayer = TypeObject("VRC.Player");
        }
    }
    g.findAll = MethodWithParams("UnityEngine.Resources", "FindObjectsOfTypeAll", { "Type" });

    g.instanceId       = Il2::FindMethod("UnityEngine.Object", "GetInstanceID", 0);
    g.destroyImmediate = Il2::FindMethod("UnityEngine.Object", "DestroyImmediate", 1);
    g.instantiateUnder = MethodWithParams("UnityEngine.Object", "Instantiate", { "Object", "Transform" });
    g.goCtor           = MethodWithParams("UnityEngine.GameObject", ".ctor", { "String" });
    g.goComponentsInChildren = MethodWithParams("UnityEngine.GameObject", "GetComponentsInChildren", { "Type", "Boolean" });

    g.trFind        = MethodWithParams("UnityEngine.Transform", "Find", { "String" });
    g.trGetPos      = Il2::FindMethod("UnityEngine.Transform", "get_position", 0);
    g.trSetPos      = Il2::FindMethod("UnityEngine.Transform", "set_position", 1);
    g.trGetRot      = Il2::FindMethod("UnityEngine.Transform", "get_rotation", 0);
    g.trSetRot      = Il2::FindMethod("UnityEngine.Transform", "set_rotation", 1);
    g.trSetLocalPos = Il2::FindMethod("UnityEngine.Transform", "set_localPosition", 1);
    g.trSetLocalRot = Il2::FindMethod("UnityEngine.Transform", "set_localRotation", 1);

    g.behGetEnabled  = Il2::FindMethod("UnityEngine.Behaviour", "get_enabled", 0);
    g.behSetEnabled  = Il2::FindMethod("UnityEngine.Behaviour", "set_enabled", 1);
    g.animIsHuman    = Il2::FindMethod("UnityEngine.Animator", "get_isHuman", 0);
    g.animBone       = Il2::FindMethod("UnityEngine.Animator", "GetBoneTransform", 1);
    g.animSetCulling = Il2::FindMethod("UnityEngine.Animator", "set_cullingMode", 1);

    g.rendGetEnabled  = Il2::FindMethod("UnityEngine.Renderer", "get_enabled", 0);
    g.rendSetEnabled  = Il2::FindMethod("UnityEngine.Renderer", "set_enabled", 1);
    g.rendSetForceOff = Il2::FindMethod("UnityEngine.Renderer", "set_forceRenderingOff", 1);
    g.rendSharedMats  = Il2::FindMethod("UnityEngine.Renderer", "get_sharedMaterials", 0);
    if (!g.rendSharedMats) g.rendSharedMats = Il2::FindMethod("UnityEngine.Renderer", "GetSharedMaterialArray", 0);
    g.rendSharedMat   = Il2::FindMethod("UnityEngine.Renderer", "get_sharedMaterial", 0);
    g.matShader       = Il2::FindMethod("UnityEngine.Material", "get_shader", 0);

    g.colGetEnabled = Il2::FindMethod("UnityEngine.Collider", "get_enabled", 0);
    g.colSetEnabled = Il2::FindMethod("UnityEngine.Collider", "set_enabled", 1);
    g.audGetMute    = Il2::FindMethod("UnityEngine.AudioSource", "get_mute", 0);
    g.audSetMute    = Il2::FindMethod("UnityEngine.AudioSource", "set_mute", 1);

    g.tRenderer      = TypeObject("UnityEngine.Renderer");
    g.tCollider      = TypeObject("UnityEngine.Collider");
    g.tMonoBehaviour = TypeObject("UnityEngine.MonoBehaviour");
    g.tAudioSource   = TypeObject("UnityEngine.AudioSource");
    g.tCamera        = TypeObject("UnityEngine.Camera");
    g.tLight         = TypeObject("UnityEngine.Light");

    // One line saying what resolved: on a new build this is what tells a missing method from a
    // feature that silently does nothing.
    auto ok = [](void* p) { return p ? "ok" : "NON"; };
    Log::Writef("Info",
        "[TrueView] players: localPlayer=%s isLocal@%d gameObject@%d VRC.Player.api@%d findAll=%s typeVrcPlayer=%s",
        ok(g.localPlayer), g.apiIsLocalOff, g.apiGoOff, g.playerApiOff, ok(g.findAll), ok(g.tVrcPlayer));
    Log::Writef("Info",
        "[TrueView] resolution : joueurs=%s/%s/%s/%s find=%s pose=%s%s%s%s local=%s%s "
        "animator=%s/%s/%s renderer=%s/%s/%s mats=%s/%s shader=%s instantiate=%s ctor=%s children=%s "
        "destroyImm=%s collider=%s audio=%s types=%s/%s/%s",
        ok(g.allPlayers), ok(g.playerId), ok(g.isLocal), ok(g.apiGameObject), ok(g.trFind),
        ok(g.trGetPos), ok(g.trSetPos), ok(g.trGetRot), ok(g.trSetRot), ok(g.trSetLocalPos), ok(g.trSetLocalRot),
        ok(g.animIsHuman), ok(g.animBone), ok(g.behSetEnabled),
        ok(g.rendGetEnabled), ok(g.rendSetEnabled), ok(g.rendSetForceOff),
        ok(g.rendSharedMats), ok(g.rendSharedMat), ok(g.matShader),
        ok(g.instantiateUnder), ok(g.goCtor), ok(g.goComponentsInChildren), ok(g.destroyImmediate),
        ok(g.colSetEnabled), ok(g.audSetMute), ok(g.tRenderer), ok(g.tCollider), ok(g.tMonoBehaviour));
}

// ================================================================== thin il2cpp wrappers
template <typename T> T Unbox(void* boxed, T fallback = {}) {
    if (!boxed) return fallback;
    T v;
    std::memcpy(&v, static_cast<char*>(boxed) + 0x10, sizeof(T));
    return v;
}

void SetBool(void* method, void* obj, bool value) {
    if (!method || !obj) return;
    bool v = value;
    void* a[1] = { &v };
    Il2::Invoke(method, obj, a);
}
bool GetBool(void* method, void* obj) {
    return method && obj && Unbox<unsigned char>(Il2::Invoke(method, obj, nullptr)) != 0;
}

// Transform.Find: one native call for a whole relative path, and unlike GameObject.Find it sees
// inactive children. Falls back to the shared child-by-name walk if the method is missing.
void* FindRel(void* tr, char const* path) {
    if (!tr || !path) return nullptr;
    if (g.trFind) {
        void* a[1] = { Il2::NewString(path) };
        return Il2::Invoke(g.trFind, tr, a);
    }
    return Unity::FindChild(tr, path);
}

bool GetPose(void* tr, V3& p, Q4& r) {
    if (!tr || !g.trGetPos || !g.trGetRot) return false;
    void* bp = Il2::Invoke(g.trGetPos, tr, nullptr);
    void* br = Il2::Invoke(g.trGetRot, tr, nullptr);
    if (!bp || !br) return false;
    p = Unbox<V3>(bp);
    r = Unbox<Q4>(br);
    return true;
}
void SetPos(void* tr, V3 v)  { if (tr && g.trSetPos) { void* a[1] = { &v }; Il2::Invoke(g.trSetPos, tr, a); } }
void SetRot(void* tr, Q4 q)  { if (tr && g.trSetRot) { void* a[1] = { &q }; Il2::Invoke(g.trSetRot, tr, a); } }
void SetLocalPos(void* tr, V3 v) { if (tr && g.trSetLocalPos) { void* a[1] = { &v }; Il2::Invoke(g.trSetLocalPos, tr, a); } }
void SetLocalRot(void* tr, Q4 q) { if (tr && g.trSetLocalRot) { void* a[1] = { &q }; Il2::Invoke(g.trSetLocalRot, tr, a); } }

int InstanceId(void* obj) {
    return (obj && g.instanceId) ? Unbox<int>(Il2::Invoke(g.instanceId, obj, nullptr)) : 0;
}

// GetComponentsInChildren(type, includeInactive = true). Returns an il2cpp array or null.
void* ComponentsInChildren(void* go, void* typeObj) {
    if (!go || !typeObj || !g.goComponentsInChildren) return nullptr;
    bool inc = true;
    void* a[2] = { typeObj, &inc };
    return Il2::Invoke(g.goComponentsInChildren, go, a);
}

// Calls `fn(material)` for every shared material of a renderer; stops when fn returns true.
template <typename F> bool ForEachMaterial(void* renderer, F&& fn) {
    if (g.rendSharedMats) {
        void* arr = Il2::Invoke(g.rendSharedMats, renderer, nullptr);
        int n = Il2::ArrayLength(arr);
        for (int i = 0; i < n; ++i)
            if (void* mat = Il2::ArrayAt(arr, i); mat && fn(mat)) return true;
        return false;
    }
    if (g.rendSharedMat)
        if (void* mat = Il2::Invoke(g.rendSharedMat, renderer, nullptr); mat && fn(mat)) return true;
    return false;
}

// Is this subtree a REAL loaded avatar, or something that can no longer be drawn? A genuinely
// loaded avatar has at least one renderer holding a live material on a live shader. "Has children
// and no loading beams" is also true of VRChat's stand-in -- that gap is what painted people
// magenta in the first C# build (its probe found "1 renderer, [<destroyed material>]").
bool Drawable(void* renderer) {
    return renderer && ForEachMaterial(renderer, [](void* mat) {
        void* sh = g.matShader ? Il2::Invoke(g.matShader, mat, nullptr) : nullptr;
        if (!sh) return false;
        std::string name = Unity::Name(sh);
        return !name.empty() && name.find("InternalError") == std::string::npos;
    });
}

bool LooksReal(void* go) {
    // Fast path: the first renderer is almost always fine on a real avatar, and asking for one
    // avoids building the whole renderer array 4x a second for every player in the instance.
    if (Drawable(Unity::GetComponentInChildren(go, "UnityEngine.Renderer", true))) return true;
    void* arr = ComponentsInChildren(go, g.tRenderer);
    int n = Il2::ArrayLength(arr);
    for (int i = 0; i < n; ++i)
        if (Drawable(Il2::ArrayAt(arr, i))) return true;   // one drawable surface is enough
    return false;
}

int ShowRenderers(void* go) {
    void* arr = ComponentsInChildren(go, g.tRenderer);
    int n = Il2::ArrayLength(arr);
    for (int i = 0; i < n; ++i) {
        void* r = Il2::ArrayAt(arr, i);
        if (!r) continue;
        if (g.rendGetEnabled && !GetBool(g.rendGetEnabled, r)) SetBool(g.rendSetEnabled, r, true);
        SetBool(g.rendSetForceOff, r, false);
    }
    return n;
}

// Turns every Behaviour of `typeObj` under `go` off (AudioSource, Camera, Light on a copy).
void DisableAll(void* go, void* typeObj) {
    void* arr = ComponentsInChildren(go, typeObj);
    int n = Il2::ArrayLength(arr);
    for (int i = 0; i < n; ++i)
        if (void* c = Il2::ArrayAt(arr, i)) SetBool(g.behSetEnabled, c, false);
}

void* NewGameObject(char const* name) {
    static void* objNew = Il2::Export("il2cpp_object_new");
    void* klass = Il2::FindClass("UnityEngine.GameObject");
    if (!objNew || !klass || !g.goCtor) return nullptr;
    void* go = reinterpret_cast<void* (*)(void*)>(objNew)(klass);
    if (!go) return nullptr;
    void* a[1] = { Il2::NewString(name) };
    Il2::Invoke(g.goCtor, go, a);
    return go;
}

// ================================================================== per-player ledger
// Indices into Touched::ik / Touched::bone.
enum Ik { IkHip, IkChest, IkHead, IkLHand, IkRHand, IkLFoot, IkRFoot, IkCount };
constexpr char const* kIkNames[IkCount] = {
    "HipTarget", "ChestTarget", "HeadEffector", "LeftEffector", "RightEffector", "LeftFootTarget", "RightFootTarget",
};
enum Bone { BHips, BChest, BHead, BLHand, BRHand, BLFoot, BRFoot, BLUpLeg, BRUpLeg, BLLoLeg, BRLoLeg, BoneCount };
// UnityEngine.HumanBodyBones values, in Bone order. Chest falls back to Spine (7) when unmapped.
constexpr int kHumanBone[BoneCount] = { 0, 8, 10, 17, 18, 5, 6, 1, 2, 3, 4 };
constexpr int kHumanSpine = 7;

// What TrueView changed on one player, so it can be put back. Recorded before the change.
struct Touched {
    Ref  root;              // player root Transform (the seated test reads its height)
    Ref  proxy;             // AvatarProxy GameObject
    bool watchProxy  = false;   // VRChat has used the robot for this player: check it every frame
    bool weHidProxy  = false;
    bool cullPending = false;   // the per-frame pass caught the robot coming on: a cull just happened

    Ref  holder;            // inactive GameObject under ForwardDirection: keeps the copy asleep
    Ref  clone;             // the cached copy
    int  cloneOf = 0;       // instance id of the avatar it was copied from
    bool shown   = false;   // copy currently under ForwardDirection, i.e. drawn

    bool proxied = false;   // the real avatar is missing / a placeholder

    bool rigReady = false, rigHuman = false;
    Ref  ik[IkCount];
    Ref  bone[BoneCount];
    Ref  hipsNonHuman;

    bool saidProxied = false, saidShown = false, saidCulled = false;
};

std::unordered_map<int, Touched> g_ledger;
int    g_cursor = 0;
int    g_copiesThisFrame = 0;   // copying an avatar is costly: at most one per frame
double g_lastTick = -1.0, g_lastPrune = 0.0;
bool   g_saidNoPlayers = false, g_saidAvatarName = false;

void Note(int pid, bool& flag, bool now, char const* on, char const* off) {
    if (flag == now) return;
    flag = now;
    Log::Writef("Info", "[TrueView] player %d: %s", pid, now ? on : off);
}

// ================================================================== the copy
// Copied INTO an inactive holder, so the copy is never awake as a duplicate of a live avatar: its
// scripts are destroyed before any Awake can run, and world-facing behaviours (audio, cameras,
// lights, colliders) are switched off. What is left is purely visual -- renderers, meshes, bones,
// the Animator -- which is all a stand-in should be.
void* EnsureHolder(void* fwd, Touched& t) {
    if (void* h = t.holder.Get(); h && Unity::Transform(h)) return h;
    void* h = NewGameObject(kHolderName);
    if (!h) return nullptr;
    Unity::SetActive(h, false);
    Unity::SetParent(Unity::Transform(h), fwd, false);
    t.holder.Set(h);
    return h;
}

void DropCopy(Touched& t) {
    if (void* c = t.clone.Get()) Unity::Destroy(c);
    t.clone.Reset();
    t.cloneOf = 0;
    t.shown = false;
    t.rigReady = false;
}

void MakeCopy(int pid, void* fwd, void* avatarGo, Touched& t) {
    // Entering a world means copying everyone's avatar; spread it so no single frame pays for 30.
    // Returning without touching cloneOf leaves this player for a later survey.
    if (g_copiesThisFrame > 0) return;
    ++g_copiesThisFrame;

    DropCopy(t);   // a copy of the PREVIOUS avatar must not survive an avatar change
    // Recorded before the attempt: a copy that fails must not be retried 4x a second.
    t.cloneOf = InstanceId(avatarGo);
    if (!g.instantiateUnder) return;
    void* holder = EnsureHolder(fwd, t);
    if (!holder) return;

    void* a[2] = { avatarGo, Unity::Transform(holder) };
    void* copy = Il2::Invoke(g.instantiateUnder, nullptr, a);
    if (!copy) return;
    Unity::SetName(copy, kCloneName);

    if (g.destroyImmediate && g.tMonoBehaviour) {
        void* scripts = ComponentsInChildren(copy, g.tMonoBehaviour);
        int n = Il2::ArrayLength(scripts);
        for (int i = 0; i < n; ++i)
            if (void* s = Il2::ArrayAt(scripts, i)) { void* d[1] = { s }; Il2::Invoke(g.destroyImmediate, nullptr, d); }
    }
    DisableAll(copy, g.tAudioSource);
    DisableAll(copy, g.tCamera);
    DisableAll(copy, g.tLight);
    if (void* cols = ComponentsInChildren(copy, g.tCollider)) {
        int n = Il2::ArrayLength(cols);
        for (int i = 0; i < n; ++i) if (void* c = Il2::ArrayAt(cols, i)) SetBool(g.colSetEnabled, c, false);
    }

    t.clone.Set(copy);
    Log::Writef("Info", "[TrueView] player %d: avatar copy cached (asleep).", pid);
}

// Resolves the bones the rig writes and the IK targets it reads. The copy's Animator is used once to
// map its bones, then switched OFF: it is the pump that owns the pose now, and an Animator left
// running would overwrite every bone we set before the frame is drawn.
void ResolveRig(int pid, void* root, void* copy, Touched& t) {
    t.rigReady = false;
    t.rigHuman = false;
    for (auto& r : t.bone) r.Reset();
    t.hipsNonHuman.Reset();

    void* ikRoot = FindRel(root, "AnimationController/HeadAndHandIK");
    if (!ikRoot) {
        Log::Writef("Warning", "[TrueView] player %d: no HeadAndHandIK -- the copy will stay frozen.", pid);
        return;
    }
    for (int i = 0; i < IkCount; ++i) t.ik[i].Set(FindRel(ikRoot, kIkNames[i]));
    if (!t.ik[IkHip]) {
        Log::Writef("Warning", "[TrueView] player %d: no HipTarget -- the copy will stay frozen.", pid);
        return;
    }

    void* anim = Unity::GetComponent(copy, "UnityEngine.Animator");
    if (anim) {
        SetBool(g.behSetEnabled, anim, true);
        if (GetBool(g.animIsHuman, anim) && g.animBone) {
            auto bone = [&](int id) -> void* { int v = id; void* a[1] = { &v }; return Il2::Invoke(g.animBone, anim, a); };
            for (int i = 0; i < BoneCount; ++i) t.bone[i].Set(bone(kHumanBone[i]));
            if (!t.bone[BChest]) t.bone[BChest].Set(bone(kHumanSpine));
            t.rigHuman = static_cast<bool>(t.bone[BHips]);
        }
        SetBool(g.behSetEnabled, anim, false);
    }
    if (!t.rigHuman) t.hipsNonHuman.Set(FindRel(Unity::Transform(copy), "Armature/Hips"));

    t.rigReady = t.rigHuman || static_cast<bool>(t.hipsNonHuman);
    Log::Writef("Info", "[TrueView] player %d: rig %s.", pid,
                t.rigHuman ? "humanoid (bones driven by the player's IK)"
                           : (t.rigReady ? "non humanoide (hanches seulement)" : "not found -- copy frozen"));
}

void ShowCopy(int pid, void* root, void* fwd, Touched& t) {
    void* copy = t.clone.Get();
    void* ctr = copy ? Unity::Transform(copy) : nullptr;
    if (!ctr) { DropCopy(t); return; }
    Unity::SetParent(ctr, fwd, false);                  // out of the sleeping holder = drawn
    SetLocalPos(ctr, V3{ 0, 0, 0 });
    SetLocalRot(ctr, Q4{ 0, 0, 0, 1 });
    int n = ShowRenderers(copy);                        // the original may have been culled when copied
    t.shown = true;
    ResolveRig(pid, root, copy, t);
    if (!t.saidShown) {
        t.saidShown = true;
        Log::Writef("Info", "[TrueView] player %d: copy shown instead of the robot (%d renderer(s)).", pid, n);
    }
}

void HideCopy(Touched& t) {
    if (!t.shown) return;
    t.shown = false;
    t.rigReady = false;
    void* copy = t.clone.Get();
    void* holder = t.holder.Get();
    void* ctr = copy ? Unity::Transform(copy) : nullptr;
    void* htr = holder ? Unity::Transform(holder) : nullptr;
    if (ctr && htr) Unity::SetParent(ctr, htr, false);   // back to sleep, kept for the next swap
    else DropCopy(t);
}

// ================================================================== survey
// The real avatar sits under ForwardDirection as "Avatar" on every build seen so far. If a build
// renames it, take the one child that is neither the robot nor ours, and say so once.
void* AvatarUnder(void* fwd) {
    if (void* a = FindRel(fwd, "Avatar")) return a;
    int n = Unity::ChildCount(fwd);
    for (int i = 0; i < n; ++i) {
        void* c = Unity::ChildAt(fwd, i);
        std::string name = Unity::Name(Unity::GameObjectOf(c));
        if (name.empty() || name == "AvatarProxy" || name == kCloneName || name == kHolderName) continue;
        if (!g_saidAvatarName) {
            g_saidAvatarName = true;
            Log::Writef("Info", "[TrueView] the avatar is not 'Avatar' on this build: child '%s' kept.", name.c_str());
        }
        return c;
    }
    return nullptr;
}

void RestoreAvatar(int pid, void* root, Touched& t) {
    void* fwd = FindRel(root, "ForwardDirection");
    if (!fwd) return;

    // The robot: off the moment it is seen, and watched every frame from then on.
    bool culled = t.cullPending;
    t.cullPending = false;
    if (void* proxyTr = FindRel(fwd, "AvatarProxy")) {
        if (void* pgo = Unity::GameObjectOf(proxyTr)) {
            t.proxy.Set(pgo);
            if (Unity::ActiveSelf(pgo)) {
                Unity::SetActive(pgo, false);
                t.weHidProxy = true;
                t.watchProxy = true;
                culled = true;
            }
        }
    }

    void* avatarTr = AvatarUnder(fwd);
    if (!avatarTr) return;
    void* avatarGo = Unity::GameObjectOf(avatarTr);
    if (!avatarGo) return;

    bool placeholder = FindRel(avatarTr, "part_Beams") != nullptr;
    bool healthy = Unity::ChildCount(avatarTr) > 0 && !placeholder && LooksReal(avatarGo);

    if (healthy) {
        Note(pid, t.saidProxied, false, "", "the real avatar is back");
        t.proxied = false;
        t.saidShown = false;
        HideCopy(t);
        if (!Unity::ActiveSelf(avatarGo)) Unity::SetActive(avatarGo, true);

        // CULLED, not replaced: VRChat kept the avatar but forced its renderers off, stopped its
        // Animator and raised the robot -- all in one transition. Only then is there anything to
        // undo; a healthy avatar VRChat is drawing normally is left alone (re-asserting hundreds of
        // renderers 4x a second for nothing is what made the C# version expensive).
        if (culled) {
            int n = ShowRenderers(avatarGo);
            if (void* anim = Unity::GetComponent(avatarGo, "UnityEngine.Animator")) {
                SetBool(g.behSetEnabled, anim, true);
                if (g.animSetCulling) { int mode = 0; void* a[1] = { &mode }; Il2::Invoke(g.animSetCulling, anim, a); }  // AlwaysAnimate
            }
            if (!t.saidCulled) {
                t.saidCulled = true;
                Log::Writef("Info", "[TrueView] player %d: VRChat-hidden avatar redrawn (%d renderer(s)).", pid, n);
            }
        }

        // Keep one sleeping copy of THIS avatar for the next swap.
        int id = InstanceId(avatarGo);
        if (id != 0 && id != t.cloneOf) MakeCopy(pid, fwd, avatarGo, t);
        return;
    }

    // The real avatar is empty, a shell, or still loading.
    t.proxied = true;
    Note(pid, t.saidProxied, true, "avatar replaced by VRChat (robot / shell)", "");

    if (!t.clone) return;   // joined after the swap: nothing was ever cached for this player
    void* copy = t.clone.Get();
    if (!copy || !LooksReal(copy)) {
        // Measured on the C# build: by the time a copy is needed VRChat has often unloaded the
        // bundle behind it (72 renderers, 93 dead materials). Showing it anyway paints the player
        // magenta. VRChat's own stand-in beats a magenta silhouette.
        DropCopy(t);
        Log::Writef("Info", "[TrueView] player %d: copy abandoned -- VRChat unloaded its materials (it would be magenta).", pid);
        return;
    }
    if (!t.shown) ShowCopy(pid, root, fwd, t);
    else if (!t.rigReady) ResolveRig(pid, root, copy, t);
}

// A replaced player loses the laser hitbox, so they cannot be selected at all. Only replaced
// players are touched: forcing the hitbox on for everyone would fight VRChat's own state.
void RestoreSelectRegion(void* root) {
    void* sr = FindRel(root, "SelectRegion");
    void* go = sr ? Unity::GameObjectOf(sr) : nullptr;
    if (!go) return;
    if (!Unity::ActiveSelf(go)) Unity::SetActive(go, true);
    void* cols = ComponentsInChildren(go, g.tCollider);
    int n = Il2::ArrayLength(cols);
    for (int i = 0; i < n; ++i) {
        void* c = Il2::ArrayAt(cols, i);
        if (c && !GetBool(g.colGetEnabled, c)) SetBool(g.colSetEnabled, c, true);
    }
}

// The voice of a REPLACED player only. VRChat mutes a player locally when it swaps them out; it
// also mutes players YOU muted, and those must stay muted -- the C# version unmuted everyone.
void RestoreAudio(void* root) {
    void* us = FindRel(root, "CameraMount/USpeak");                                   // build 25686233
    if (!us) us = FindRel(root, "AnimationController/HeadAndHandIK/HeadEffector/USpeak");   // 1903
    void* go = us ? Unity::GameObjectOf(us) : nullptr;
    void* src = go ? Unity::GetComponent(go, "UnityEngine.AudioSource") : nullptr;
    if (src && GetBool(g.audGetMute, src)) SetBool(g.audSetMute, src, false);
}

void Inspect(void* api) {
    int pid = Unbox<int>(Il2::Invoke(g.playerId, api, nullptr), -1);
    if (pid < 0) return;
    void* go = g.apiGameObject ? Il2::Invoke(g.apiGameObject, api, nullptr)
                              : Player::GameObjectOf(api);
    if (!go || !Unity::ActiveSelf(go)) return;
    void* root = Unity::Transform(go);
    if (!root) return;

    Touched& t = g_ledger[pid];
    t.root.Set(root);
    RestoreAvatar(pid, root, t);
    if (t.proxied) {
        RestoreSelectRegion(root);
        RestoreAudio(root);
    }
}

// ================================================================== per-frame passes
// The robot must disappear the frame it appears -- but only for players VRChat has already used it
// on, never the whole instance. Catching it also flags a cull for the next survey to undo.
void SuppressWatchedProxies() {
    for (auto& [pid, t] : g_ledger) {
        if (!t.watchProxy) continue;
        void* p = t.proxy.Get();
        if (!p || !Unity::ActiveSelf(p)) continue;
        Unity::SetActive(p, false);
        t.weHidProxy = true;
        t.cullPending = true;
    }
}

// POSE THE COPY. A copy shown in place of a stripped avatar has no controller running on it, so left
// alone it stands frozen in the pose it was copied in. VRChat still moves the PLAYER's IK targets --
// they hang off the player rig, not the avatar, so they survive the swap -- and they are exactly what
// the real avatar would have tracked. Copying them onto the copy's bones makes it walk, turn, reach
// and sit with the player. Runs only for players currently showing a copy.
void DriveRigs() {
    static Q4 const kUpL = Euler(75.f, -5.f, 0.f), kUpR = Euler(75.f, 5.f, 0.f), kLo = Euler(-80.f, 0.f, 0.f);

    for (auto& [pid, t] : g_ledger) {
        if (!t.proxied || !t.shown || !t.rigReady) continue;

        V3 hp; Q4 hr;
        if (!GetPose(t.ik[IkHip].Get(), hp, hr)) { t.rigReady = false; continue; }   // re-resolved by the survey

        if (!t.rigHuman) {
            void* hips = t.hipsNonHuman.Get();
            SetPos(hips, hp);
            SetRot(hips, hr);
            continue;
        }

        auto place = [&](Bone b, Ik k, bool position) {
            V3 p; Q4 r;
            void* bone = t.bone[b].Get();
            if (!bone || !GetPose(t.ik[k].Get(), p, r)) return;
            if (position) SetPos(bone, p);
            SetRot(bone, r);
        };

        if (void* hips = t.bone[BHips].Get()) { SetPos(hips, hp); SetRot(hips, hr); }
        place(BChest, IkChest, false);
        place(BHead,  IkHead,  false);
        place(BLHand, IkLHand, true);
        place(BRHand, IkRHand, true);

        // Hips riding low over the player root = seated or crouched: fold the legs into a sit and
        // leave the feet alone (foot IK under a seated body only fights the pose).
        V3 rp; Q4 rr;
        float hipRel = GetPose(t.root.Get(), rp, rr) ? hp.y - rp.y : 1.f;
        if (hipRel < 0.7f) {
            SetLocalRot(t.bone[BLUpLeg].Get(), kUpL);
            SetLocalRot(t.bone[BRUpLeg].Get(), kUpR);
            SetLocalRot(t.bone[BLLoLeg].Get(), kLo);
            SetLocalRot(t.bone[BRLoLeg].Get(), kLo);
            continue;
        }
        place(BLFoot, IkLFoot, true);
        place(BRFoot, IkRFoot, true);
    }
}

// ================================================================== teardown
// Puts one player back. Every step is independent: an object the game already destroyed must not
// stop the rest. The robot is only raised again where VRChat actually wanted it -- the player is
// still replaced. Raising it over a healthy avatar (what the C# version did) draws a robot on top
// of a real person.
void Undo(Touched& t) {
    DropCopy(t);
    if (void* h = t.holder.Get()) Unity::Destroy(h);
    t.holder.Reset();
    if (t.weHidProxy && t.proxied)
        if (void* p = t.proxy.Get(); p && !Unity::ActiveSelf(p)) Unity::SetActive(p, true);
    t.weHidProxy = false;
}

void RestoreAll(char const* why) {
    size_t n = g_ledger.size();
    for (auto& [pid, t] : g_ledger) Undo(t);
    g_ledger.clear();
    g_cursor = 0;
    Log::Writef("Info", "[TrueView] restored (%s): %zu player(s) put back.", why, n);
}

// Players who left: restore BEFORE forgetting, or their copy and holder become orphans.
void Prune(std::unordered_set<int> const& present) {
    for (auto it = g_ledger.begin(); it != g_ledger.end();) {
        if (present.count(it->first)) { ++it; continue; }
        Undo(it->second);
        it = g_ledger.erase(it);
    }
}

// ================================================================== nameplate probe (READ-ONLY)
// Build 25686233 draws every nameplate GPU-instanced from one NameplateManager: there is no
// GameObject per plate any more (its component holds GraphicsBuffers, NativeArray<Vector4>, an atlas,
// a slot pool). Fixing plates -- for a replaced player here, for tags in general -- means finding the
// per-player record the manager keeps and the instance data it uploads each frame. This maps it ONCE
// per session into the log, reading only: components, fields with types and values, the class of the
// records inside its collections (one level down), and its methods (to find the per-frame writer).
// Delete this block once the map is in hand.
namespace probe {

bool   g_done = false;
double g_worldSince = -1.0;

// Obfuscated names are raw bytes > 0x7E; escape them so the log stays one readable line each.
std::string P(char const* s) {
    std::string out;
    if (!s) return "<null>";
    for (unsigned char const* p = reinterpret_cast<unsigned char const*>(s); *p; ++p) {
        if (*p >= 0x20 && *p < 0x7F) { out += static_cast<char>(*p); continue; }
        char b[8];
        std::snprintf(b, sizeof(b), "\\x%02X", *p);
        out += b;
    }
    return out;
}
std::string CN(void* klass) { return klass ? P(Il2::ClassName(klass)) : "<?>"; }

bool Readable(void const* p, size_t n) {
    if (!p) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(p, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    auto end = static_cast<char const*>(mbi.BaseAddress) + mbi.RegionSize;
    return static_cast<char const*>(p) + n <= end;
}

bool IsValueType(void* klass) {
    static auto fn = reinterpret_cast<bool (*)(void*)>(Il2::Export("il2cpp_class_is_valuetype"));
    return klass && fn && fn(klass);
}
void* ElementClass(void* arrayClass) {
    static auto fn = reinterpret_cast<void* (*)(void*)>(Il2::Export("il2cpp_class_get_element_class"));
    return (arrayClass && fn) ? fn(arrayClass) : nullptr;
}

// Count of a managed collection, by its well-known private fields. -1 if not a collection we know.
int CollectionCount(void* obj) {
    void* k = Il2::ClassOfObject(obj);
    std::string n = CN(k);
    if (n.size() > 2 && n.compare(n.size() - 2, 2, "[]") == 0) return Il2::ArrayLength(obj);
    for (char const* f : { "_size", "_count", "count", "m_Count" }) {
        int off = Il2::FieldOffset(k, f);
        if (off > 0 && Readable(static_cast<char*>(obj) + off, 4))
            return *reinterpret_cast<int*>(static_cast<char*>(obj) + off);
    }
    return -1;
}

// One field's value at `base` (an object, or a struct's payload when `inlineStruct`).
std::string Value(void* fieldClass, char const* base) {
    char b[160];
    std::string t = CN(fieldClass);
    if (!Readable(base, 8)) return "<illisible>";
    if (IsValueType(fieldClass)) {
        if (t == "Boolean")                   return *reinterpret_cast<unsigned char const*>(base) ? "true" : "false";
        if (t == "Int32" || t == "UInt32")    { std::snprintf(b, sizeof(b), "%d", *reinterpret_cast<int const*>(base)); return b; }
        if (t == "Int64" || t == "UInt64")    { std::snprintf(b, sizeof(b), "%lld", *reinterpret_cast<long long const*>(base)); return b; }
        if (t == "Single")                    { std::snprintf(b, sizeof(b), "%g", *reinterpret_cast<float const*>(base)); return b; }
        if (t == "Double")                    { std::snprintf(b, sizeof(b), "%g", *reinterpret_cast<double const*>(base)); return b; }
        if (t == "Vector3") {
            auto f = reinterpret_cast<float const*>(base);
            std::snprintf(b, sizeof(b), "(%g, %g, %g)", f[0], f[1], f[2]);
            return b;
        }
        if (t == "Vector4" || t == "Quaternion" || t == "Color") {
            auto f = reinterpret_cast<float const*>(base);
            std::snprintf(b, sizeof(b), "(%g, %g, %g, %g)", f[0], f[1], f[2], f[3]);
            return b;
        }
        if (t == "NativeArray`1") {
            // { void* m_Buffer; int m_Length; ... } -- show the length and the first Vector4s.
            void* buf = *reinterpret_cast<void* const*>(base);
            int len = *reinterpret_cast<int const*>(base + 8);
            std::snprintf(b, sizeof(b), "NativeArray len=%d buf=%p", len, buf);
            std::string s(b);
            auto f = static_cast<float const*>(buf);
            for (int i = 0; i < len && i < 4 && Readable(f + i * 4, 16); ++i) {
                std::snprintf(b, sizeof(b), " [%d]=(%g,%g,%g,%g)", i, f[i*4], f[i*4+1], f[i*4+2], f[i*4+3]);
                s += b;
            }
            return s;
        }
        int sz = Il2::ClassInstanceSize(fieldClass) - 0x10;
        if (sz <= 0 || sz > 32) sz = 16;
        std::string s = "struct{";
        for (int i = 0; i < sz && Readable(base + i, 1); ++i) { std::snprintf(b, sizeof(b), "%02X", static_cast<unsigned char>(base[i])); s += b; }
        return s + "}";
    }
    void* obj = *reinterpret_cast<void* const*>(base);
    if (!obj) return "null";
    void* ok = Il2::ClassOfObject(obj);
    std::string on = CN(ok);
    if (on == "String") {
        std::string v = Il2::ReadString(obj);
        if (v.size() > 64) v = v.substr(0, 64) + "...";
        return "\"" + P(v.c_str()) + "\"";
    }
    std::snprintf(b, sizeof(b), "%s@%p", on.c_str(), obj);
    std::string s(b);
    if (int c = CollectionCount(obj); c >= 0) s += " count=" + std::to_string(c);
    return s;
}

// Every instance field of `obj` (walking up the class chain to UnityEngine.Object), one line each.
void DumpFields(void* obj, std::string const& indent, int maxFields = 80) {
    int shown = 0;
    for (void* k = Il2::ClassOfObject(obj); k && shown < maxFields; k = Il2::ClassParent(k)) {
        std::string kn = CN(k);
        if (kn == "MonoBehaviour" || kn == "Behaviour" || kn == "Component" || kn == "Object") break;
        void* it = nullptr;
        while (void* f = Il2::NextField(k, &it)) {
            if (Il2::FieldIsStatic(f)) continue;
            void* fc = Il2::FieldClass(f);
            int off = Il2::FieldOffsetOf(f);
            Log::Writef("Info", "[NameplateProbe] %s+0x%X %s : %s = %s", indent.c_str(), off,
                        P(Il2::FieldName(f)).c_str(), CN(fc).c_str(),
                        Value(fc, static_cast<char const*>(obj) + off).c_str());
            if (++shown >= maxFields) break;
        }
    }
}

// The records inside a collection field: the class of the first element and its fields, one level.
void DumpRecords(void* collection, std::string const& label) {
    void* k = Il2::ClassOfObject(collection);
    std::string n = CN(k);
    void* arr = nullptr; int count = 0;
    if (n.size() > 2 && n.compare(n.size() - 2, 2, "[]") == 0) { arr = collection; count = Il2::ArrayLength(arr); }
    else if (n == "List`1") {
        arr   = *reinterpret_cast<void**>(static_cast<char*>(collection) + 0x10);
        count = *reinterpret_cast<int*>(static_cast<char*>(collection) + 0x18);
    }
    if (!arr || count <= 0 || IsValueType(ElementClass(Il2::ClassOfObject(arr)))) return;
    for (int i = 0; i < count && i < 2; ++i) {
        void* e = Il2::ArrayAt(arr, i);
        if (!e) continue;
        Log::Writef("Info", "[NameplateProbe]   %s[%d] : classe %s", label.c_str(), i, CN(Il2::ClassOfObject(e)).c_str());
        DumpFields(e, "      ", 40);
    }
}

void DumpMethods(void* klass) {
    void* it = nullptr; int n = 0;
    while (void* m = Il2::NextMethod(klass, &it)) {
        std::string sig;
        int pc = Il2::MethodParamCount(m);
        for (int i = 0; i < pc; ++i) { if (i) sig += ", "; sig += CN(Il2::MethodParamClass(m, i)); }
        Log::Writef("Info", "[NameplateProbe]   methode %s(%s) -> %s", P(Il2::MethodName(m)).c_str(), sig.c_str(),
                    CN(Il2::MethodReturnClass(m)).c_str());
        if (++n >= 150) break;
    }
}

void Run() {
    void* go = Unity::Find("NameplateManager");
    if (!go) go = Unity::Find("_Application/NameplateManager");
    if (!go) { Log::Warn("[NameplateProbe] GameObject 'NameplateManager' not found."); return; }

    void* getComps = MethodWithParams("UnityEngine.GameObject", "GetComponents", { "Type" });
    void* tComp = TypeObject("UnityEngine.Component");
    if (!getComps || !tComp) { Log::Warn("[NameplateProbe] GetComponents(Type) unavailable."); return; }
    void* a[1] = { tComp };
    void* comps = Il2::Invoke(getComps, go, a);
    int n = Il2::ArrayLength(comps);
    Log::Writef("Info", "[NameplateProbe] NameplateManager : %d composant(s), %d enfant(s).", n,
                Unity::ChildCount(Unity::Transform(go)));

    for (int i = 0; i < n; ++i) {
        void* c = Il2::ArrayAt(comps, i);
        if (!c) continue;
        void* k = Il2::ClassOfObject(c);
        Log::Writef("Info", "[NameplateProbe] composant %d : %s (parent %s, %d octets)", i, CN(k).c_str(),
                    CN(Il2::ClassParent(k)).c_str(), Il2::ClassInstanceSize(k));
        if (CN(k) == "Transform") continue;
        DumpFields(c, "  ");

        // One level into every collection the component holds: that is where per-player records live.
        void* it = nullptr;
        while (void* f = Il2::NextField(k, &it)) {
            if (Il2::FieldIsStatic(f) || IsValueType(Il2::FieldClass(f))) continue;
            void* v = *reinterpret_cast<void**>(static_cast<char*>(c) + Il2::FieldOffsetOf(f));
            if (v && CollectionCount(v) > 0) DumpRecords(v, P(Il2::FieldName(f)));
        }
        DumpMethods(k);
    }
    Log::Info("[NameplateProbe] end of map.");
}

// Once per session, 20 s into a world with at least one other player: plates exist by then.
void Tick(int playerCount, double now) {
    if (g_done) return;
    if (playerCount < 2) { g_worldSince = -1.0; return; }
    if (g_worldSince < 0.0) { g_worldSince = now; return; }
    if (now - g_worldSince < 20.0) return;
    g_done = true;
    Run();
}

}   // namespace probe

}   // namespace

// ================================================================== module
TrueViewModule::TrueViewModule()
    : Module("TrueView", "a player swapped for VRChat's robot stays visible with their real avatar") {
    // The client's TRUE VIEW button.
    Actions::Register("trueView", [](std::string const&, void* u) {
        auto* m = static_cast<TrueViewModule*>(u);
        m->SetEnabled(!m->Enabled());
    }, this);

    m_enabled = true;   // always on: no menu card, no hotkey (owner decision)
}

void TrueViewModule::OnDisable() {
    RestoreAll("module off");
}

void TrueViewModule::OnUpdate() {
    Ensure();
    if (!g.allPlayers || !g.playerId) {
        if (!g_saidNoPlayers) {
            g_saidNoPlayers = true;
            Log::Warn("[TrueView] VRCPlayerApi incomplete on this build -- module inactive.");
        }
        return;
    }

    g_copiesThisFrame = 0;
    double now = Engine::Time();
    double dt = (g_lastTick < 0.0) ? kSurveyPeriod : now - g_lastTick;
    g_lastTick = now;
    if (dt <= 0.0 || dt > kSurveyPeriod) dt = kSurveyPeriod;

    // 1. Per frame, only over players already known: kill the robot, pose the copies.
    if (!g_ledger.empty()) {
        SuppressWatchedProxies();
        DriveRigs();
    }

    // 2. Round-robin survey: each remote player is inspected ~4x a second.
    void* list = Il2::Invoke(g.allPlayers, nullptr, nullptr);
    if (!list) return;
    void* items = *reinterpret_cast<void**>(static_cast<char*>(list) + 0x10);   // List<T>._items
    int   count = *reinterpret_cast<int32_t*>(static_cast<char*>(list) + 0x18); // List<T>._size
    if (!items || count <= 0 || count > 256) return;

    probe::Tick(count, now);   // one-shot, read-only nameplate map (see the probe block)

    int budget = static_cast<int>(std::ceil(count * dt / kSurveyPeriod));
    if (budget < 1) budget = 1;
    if (budget > count) budget = count;
    for (int k = 0; k < budget; ++k) {
        g_cursor = (g_cursor + 1) % count;
        void* api = Il2::ArrayAt(items, g_cursor);
        if (!api) continue;
        if (g.isLocal ? GetBool(g.isLocal, api) : Player::IsLocal(api)) continue;
        Inspect(api);
    }

    // 3. Forget players who left -- after putting back what was changed on them.
    if (now - g_lastPrune >= kPruneEvery) {
        g_lastPrune = now;
        std::unordered_set<int> present;
        for (int i = 0; i < count; ++i)
            if (void* api = Il2::ArrayAt(items, i))
                present.insert(Unbox<int>(Il2::Invoke(g.playerId, api, nullptr), -1));
        Prune(present);
    }
}

}
