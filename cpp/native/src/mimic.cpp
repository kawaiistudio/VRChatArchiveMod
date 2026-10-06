#include "mimic.hpp"
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
    // UnityEngine.HumanBodyBones, 0..54. The order is part of Unity's API and does not move, so
    // the index IS the shared vocabulary that lets a pose cross between two different avatars.
    constexpr int kBoneCount = 55;

    // Left/right partner of each humanoid bone. Everything on the centre line maps to itself.
    //
    // THE FINGERS ARE NOT INTERLEAVED. Unity alternates left/right only up to the jaw (23); from
    // 24 the whole LEFT hand comes first (24..38) and the whole RIGHT hand after it (39..53), so
    // a partner there is fifteen apart, not one. The previous table kept pairing neighbours all
    // the way up, which mirrored LeftThumbProximal onto LeftThumbIntermediate -- the mirrored pose
    // had the body right and both hands wrong.
    constexpr int kMirror[kBoneCount] = {
         0,  2,  1,  4,  3,  6,  5,  7,  8,  9, 10, 12, 11, 14, 13, 16, 15, 18, 17, 20,
        19, 22, 21, 23,
        39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,      // left hand  -> right
        24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38,      // right hand -> left
        54
    };

    void* BoneOf(void* animator, int bone) {
        static void* getBone = Il2::FindMethod("UnityEngine.Animator", "GetBoneTransform", 1);
        if (!animator || !getBone) return nullptr;
        int b = bone;
        void* a[1] = { &b };
        void* t = Il2::Invoke(getBone, animator, a);
        return Unity::IsAlive(t) ? t : nullptr;
    }

    bool IsHuman(void* animator) {
        static void* m = Il2::FindMethod("UnityEngine.Animator", "get_isHuman", 0);
        if (!animator || !m) return false;
        void* r = Il2::Invoke(m, animator, nullptr);
        return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10) != 0;
    }

    bool GetLocalRot(void* tr, float out4[4]) {
        static void* m = Il2::FindMethod("UnityEngine.Transform", "get_localRotation", 0);
        if (!tr || !m) return false;
        void* b = Il2::Invoke(m, tr, nullptr);
        if (!b) return false;
        auto const* f = reinterpret_cast<float const*>(static_cast<char*>(b) + 0x10);
        out4[0] = f[0]; out4[1] = f[1]; out4[2] = f[2]; out4[3] = f[3];
        return true;
    }
    void SetLocalRot(void* tr, float const in4[4]) {
        static void* m = Il2::FindMethod("UnityEngine.Transform", "set_localRotation", 1);
        if (!tr || !m) return;
        float v[4] = { in4[0], in4[1], in4[2], in4[3] };
        void* a[1] = { v };
        Il2::Invoke(m, tr, a);
    }

    void* AvatarRootUnder(void* playerGo) {
        if (!playerGo) return nullptr;
        void* d = Unity::GetComponentInChildren(
            playerGo, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", true);
        return d ? Unity::GameObjectOf(d) : nullptr;
    }

    void* AnimatorOf(void* api) {
        void* go = Player::GameObjectOf(api);
        void* root = AvatarRootUnder(go);
        if (!Unity::IsAlive(root)) return nullptr;
        void* a = Unity::GetComponentInChildren(root, "UnityEngine.Animator", true);
        return (Unity::IsAlive(a) && IsHuman(a)) ? a : nullptr;
    }
}

MimicModule::MimicModule()
    : Module("Mimic", "wear another player's pose, bone by bone")
{
    Actions::Register("mimic", [](std::string const& v, void* u) {
        static_cast<MimicModule*>(u)->Start(v, false);
    }, this);
    Actions::Register("mimicMirror", [](std::string const& v, void* u) {
        static_cast<MimicModule*>(u)->Start(v, true);
    }, this);
    Actions::Register("mimicStop", [](std::string const&, void* u) {
        static_cast<MimicModule*>(u)->SetEnabled(false);
    }, this);

    // WHAT THE CLIENT SHOWS: the PLAYERS page marks the row whose user id equals mimicTarget, so
    // the id goes out, and only while the copy is actually running.
    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<MimicModule*>(u);
        bool on = m->Enabled() && !m->m_target.empty();
        out += ",\"mimicTarget\":" + Json::Str(on ? m->m_target : std::string());
        out += ",\"mimicMirror\":" + Json::Bool(on && m->m_mirror);
    }, this);
}

void MimicModule::Start(std::string const& userId, bool mirror) {
    if (userId.empty()) { ScreenUI::Toast("Mimic: no player selected"); return; }

    // The same target again is a stop, which is how the client's button behaves.
    if (Enabled() && m_target == userId && m_mirror == mirror) { SetEnabled(false); return; }

    m_target = userId;
    m_mirror = mirror;
    m_localAnim = m_targetAnim = nullptr;
    m_nextResolve = 0.0;
    m_reported = false;
    SetEnabled(true);
    Log::Writef("Info", "[Mimic] posing from '%s'%s.", userId.c_str(), mirror ? " (miroir)" : "");
    ScreenUI::Toast(mirror ? "Mirror mimic" : "Mimic");
}

void MimicModule::Stop() { SetEnabled(false); }

void* MimicModule::LocalAnimator() {
    if (Unity::IsAlive(m_localAnim)) return m_localAnim;
    m_localAnim = AnimatorOf(Player::LocalApi());
    return m_localAnim;
}

void* MimicModule::TargetAnimator() {
    if (Unity::IsAlive(m_targetAnim)) return m_targetAnim;
    for (auto const& p : Player::All())
        if (!p.isLocal && p.userId == m_target) { m_targetAnim = AnimatorOf(p.api); break; }
    return m_targetAnim;
}

// VRChat serialises the local pose from its own IK output, so an IK that keeps solving overwrites
// the copy before the send and nobody else sees it. Everything switched off is remembered, and
// put back exactly -- an avatar left with its IK disabled is a broken avatar for the session.
void MimicModule::SetIk(bool on) {
    if (on) {
        for (void* c : m_ikOff)
            if (Unity::IsAlive(c)) Unity::SetBehaviourEnabled(c, true);
        m_ikOff.clear();
        return;
    }
    void* root = AvatarRootUnder(Player::GameObjectOf(Player::LocalApi()));
    if (!Unity::IsAlive(root)) return;
    for (char const* type : { "RootMotion.FinalIK.VRIK", "RootMotion.FinalIK.IKSolver",
                              "RootMotion.FinalIK.FullBodyBipedIK" }) {
        for (void* c : Unity::ComponentsInChildren(root, type, true)) {
            if (!Unity::IsAlive(c) || !Unity::BehaviourEnabled(c)) continue;
            Unity::SetBehaviourEnabled(c, false);
            m_ikOff.push_back(c);
        }
    }
    if (!m_ikOff.empty())
        Log::Writef("Info", "[Mimic] %zu IK component(s) switched off so others see the pose.",
                    m_ikOff.size());
}

void MimicModule::OnDisable() {
    SetIk(true);
    m_target.clear();
    m_localAnim = m_targetAnim = nullptr;
    m_copied = 0;
    Log::Info("[Mimic] stopped.");
}

void MimicModule::OnLateUpdate() {
    if (m_target.empty()) { SetEnabled(false); return; }

    // Re-resolved on a slow tick: an avatar change rebuilds both rigs, and a stale Animator is a
    // pointer into freed memory rather than a missing pose.
    double now = Engine::Time();
    if (now >= m_nextResolve) {
        m_nextResolve = now + 1.0;
        if (!Unity::IsAlive(m_localAnim))  m_localAnim  = nullptr;
        if (!Unity::IsAlive(m_targetAnim)) m_targetAnim = nullptr;
        if (m_ikOff.empty()) SetIk(false);     // a fresh avatar brings its IK back enabled
    }

    // KEEP THEM PAUSED. VRChat switches its own IK back on by itself -- calibration, sitting in a
    // station, an avatar reset -- and one component flipped back on silently undoes the copy for
    // everyone else while our own view still looks right. Cheap: a handful of booleans a frame.
    for (void* c : m_ikOff) {
        if (!Unity::IsAlive(c) || !Unity::BehaviourEnabled(c)) continue;
        Unity::SetBehaviourEnabled(c, false);
        if (!m_ikRelogged) {
            m_ikRelogged = true;
            Log::Info("[Mimic] VRChat re-enabled an IK component; paused it again.");
        }
    }

    void* mine  = LocalAnimator();
    void* their = TargetAnimator();
    if (!mine || !their) {
        if (!m_reported) {
            m_reported = true;
            Log::Writef("Warning", "[Mimic] rig humanoide manquant (toi=%s, cible=%s) -- en attente.",
                        mine ? "ok" : "NON", their ? "ok" : "NON");
        }
        return;
    }
    if (m_reported) { m_reported = false; Log::Info("[Mimic] both rigs are here, copying."); }

    int copied = 0;
    // 0 is Hips: skipped, it carries the root pose and copying it would move us to them. The
    // upper bound is the full count -- the old `- 1` dropped UpperChest (54), the one bone that
    // carries most of a leaning torso.
    for (int b = 1; b < kBoneCount; ++b) {
        void* src = BoneOf(their, m_mirror ? kMirror[b] : b);
        void* dst = BoneOf(mine, b);
        if (!src || !dst) continue;
        float q[4];
        if (!GetLocalRot(src, q)) continue;
        if (m_mirror) { q[1] = -q[1]; q[2] = -q[2]; }   // flip about the sagittal plane
        SetLocalRot(dst, q);
        ++copied;
    }
    if (copied != m_copied) {
        m_copied = copied;
        Log::Writef("Info", "[Mimic] %d bone(s) copied.", copied);
    }
}

}
