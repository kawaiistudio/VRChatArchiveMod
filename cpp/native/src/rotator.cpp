#include "rotator.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cmath>

namespace VRCA {

namespace {
    constexpr float kDeg2Rad = 3.14159265358979f / 180.f;
    constexpr float kRad2Deg = 180.f / 3.14159265358979f;

    bool Key(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    bool KeyDown(int vk, bool& was) {
        bool now = Key(vk);
        bool edge = now && !was;
        was = now;
        return edge;
    }
}

// --------------------------------------------------------------------------- quaternion maths
//
// Hand-rolled rather than called through il2cpp: every one of these would otherwise be a managed
// invoke plus a boxed Quaternion allocation, several per frame, for arithmetic that is a dozen
// multiplies. The conventions are Unity's exactly -- (x, y, z, w), left-handed, q * p meaning
// "p then q" is NOT what Unity does: Unity's a * b applies b first in the LOCAL frame, which is
// why Turn() post-multiplies.
namespace {
    using Quat = RotatorModule::Quat;

    Quat QMul(Quat const& a, Quat const& b) {
        return { a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                 a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                 a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                 a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z };
    }
    Quat QInv(Quat const& q) { return { -q.x, -q.y, -q.z, q.w }; }

    Quat QAngleAxis(float deg, float ax, float ay, float az) {
        float len = std::sqrt(ax * ax + ay * ay + az * az);
        if (len < 1e-6f) return { 0, 0, 0, 1 };
        float h = deg * 0.5f * kDeg2Rad, s = std::sin(h) / len;
        return { ax * s, ay * s, az * s, std::cos(h) };
    }

    // Degrees between two rotations -- the shortest way round, which is what "am I tilted?" means.
    float QAngle(Quat const& a, Quat const& b) {
        float d = std::fabs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
        if (d > 1.f) d = 1.f;
        return 2.f * std::acos(d) * kRad2Deg;
    }

    void QToAngleAxis(Quat q, float& deg, float out3[3]) {
        float n = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (n > 1e-6f) { q.x /= n; q.y /= n; q.z /= n; q.w /= n; }
        if (q.w > 1.f) q.w = 1.f;
        if (q.w < -1.f) q.w = -1.f;
        deg = 2.f * std::acos(q.w) * kRad2Deg;
        float s = std::sqrt(1.f - q.w * q.w);
        if (s < 1e-5f) { out3[0] = 1; out3[1] = 0; out3[2] = 0; return; }
        out3[0] = q.x / s; out3[1] = q.y / s; out3[2] = q.z / s;
    }

    // The vector a rotation points forward -- used to recover VRChat's heading from the rig.
    void QForward(Quat const& q, float out3[3]) {
        out3[0] = 2.f * (q.x * q.z + q.w * q.y);
        out3[1] = 2.f * (q.y * q.z - q.w * q.x);
        out3[2] = 1.f - 2.f * (q.x * q.x + q.y * q.y);
    }

    bool GetRotation(void* transform, Quat& out) {
        static void* get = Il2::FindMethod("UnityEngine.Transform", "get_rotation", 0);
        if (!transform || !get) return false;
        void* boxed = Il2::Invoke(get, transform, nullptr);
        if (!boxed) return false;
        auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
        out = { f[0], f[1], f[2], f[3] };
        return true;
    }
    void SetRotation(void* transform, Quat const& q) {
        static void* set = Il2::FindMethod("UnityEngine.Transform", "set_rotation", 1);
        if (!transform || !set) return;
        float v[4] = { q.x, q.y, q.z, q.w };
        void* a[1] = { v };
        Il2::Invoke(set, transform, a);
    }
    // Transform.RotateAround(Vector3 point, Vector3 axis, float angle) -- moves POSITION as well as
    // rotation, which is the whole reason the view comes with you.
    void RotateAround(void* transform, float const point[3], float const axis[3], float deg) {
        static void* m = Il2::FindMethod("UnityEngine.Transform", "RotateAround", 3);
        if (!transform || !m) return;
        float p[3] = { point[0], point[1], point[2] };
        float x[3] = { axis[0], axis[1], axis[2] };
        float d = deg;
        void* a[3] = { p, x, &d };
        Il2::Invoke(m, transform, a);
    }
}

RotatorModule::RotatorModule()
    : Module("Rotator", "tilt your body past vertical, the view follows")
{
    Config::Float("Rotator", "TurnSpeed", "degrees per second on the arrow keys",
                  &m_turnSpeed, 10.f, 360.f);
    Config::Bool ("Rotator", "HoldGravity",
                  "cut your gravity while tilted (otherwise VRChat straightens you)",
                  &m_holdGravity);

    Actions::Register("rotator", [](std::string const&, void* u) {
        auto* m = static_cast<RotatorModule*>(u);
        m->SetEnabled(!m->Enabled());
    }, this);
    Actions::Register("rotatorFlip", [](std::string const&, void* u) {
        static_cast<RotatorModule*>(u)->Flip();
    }, this);
    Actions::Register("rotatorReset", [](std::string const&, void* u) {
        static_cast<RotatorModule*>(u)->ResetUpright();
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<RotatorModule*>(u);
        // How far from upright, in degrees: the tilt quaternion's angle, which is what the user
        // sees on screen and the only number the card can state honestly.
        float w = m->m_tilt.w; if (w > 1.f) w = 1.f; if (w < -1.f) w = -1.f;
        double deg = 2.0 * std::acos(static_cast<double>(w)) * 57.29577951308232;
        if (deg > 180.0) deg = 360.0 - deg;
        out += ",\"rotator\":" + Json::Bool(m->Enabled());
        out += ",\"rotatorStatus\":" + Json::Str(
            !m->Enabled() ? std::string("inactive")
            : deg < 1.0   ? std::string("upright -- arrow keys to tilt")
                          : "tilted by " + Json::Num(deg) + " degres");
    }, this);
}

void* RotatorModule::Rig() { return Player::LocalRoot(); }

// THE PIVOT: a bone, not the feet. Hips first, then head, and the rig's own position plus a metre
// only if the avatar is not humanoid -- a pivot at the origin swings the body like a plank.
bool RotatorModule::Pivot(void* rig, float out3[3]) {
    static void* getBone = Il2::FindMethod("UnityEngine.Animator", "GetBoneTransform", 1);
    static void* isHuman = Il2::FindMethod("UnityEngine.Animator", "get_isHuman", 0);
    void* anim = Unity::GetComponentInChildren(Unity::GameObjectOf(rig), "UnityEngine.Animator", true);
    if (Unity::IsAlive(anim) && getBone && isHuman) {
        void* h = Il2::Invoke(isHuman, anim, nullptr);
        if (h && *reinterpret_cast<unsigned char*>(static_cast<char*>(h) + 0x10)) {
            for (int bone : { 0 /* Hips */, 10 /* Head */ }) {
                int b = bone;
                void* a[1] = { &b };
                void* t = Il2::Invoke(getBone, anim, a);
                if (Unity::IsAlive(t) && Unity::GetPosition(t, out3)) return true;
            }
        }
    }
    if (!Unity::GetPosition(rig, out3)) return false;
    out3[1] += 1.f;
    return true;
}

void RotatorModule::HoldGravity(bool on) {
    void* api = Player::LocalApi();
    if (!api) return;
    if (on && !m_gravityHeld) {
        m_gravityOriginal = Player::GetGravityStrength(api);
        // A world already at zero would otherwise be "restored" to zero later.
        if (m_gravityOriginal <= 0.001f) m_gravityOriginal = 1.f;
        Player::SetGravityStrength(api, 0.f);
        m_gravityHeld = true;
    } else if (!on && m_gravityHeld) {
        Player::SetGravityStrength(api, m_gravityOriginal);
        m_gravityHeld = false;
    }
}

void RotatorModule::RestoreBody() {
    void* rig = Rig();
    if (!rig) { m_restorePending = true; return; }   // retried every frame until it lands
    Quat level = QAngleAxis(m_yaw, 0, 1, 0);
    SetRotation(rig, level);
    m_applied = level;
    m_restorePending = false;
}

void RotatorModule::OnEnable() {
    m_tilt = {};
    m_holdProbed = false;
    Log::Info("[Rotator] ON -- arrows tilt, PgUp/PgDn roll, RShift+F flips.");
}

void RotatorModule::OnDisable() {
    m_tilt = {};
    RestoreBody();
    HoldGravity(false);
    Log::Info("[Rotator] OFF -- upright again.");
}

void RotatorModule::Flip() {
    if (!Enabled()) SetEnabled(true);
    bool wasOver = QAngle(m_tilt, Quat{}) > 90.f;
    if (wasOver) m_tilt = {};
    else m_tilt = QAngleAxis(180.f, 0, 0, 1);
    Log::Writef("Info", "[Rotator] %s", wasOver ? "droit" : "a l'envers");
}

void RotatorModule::ResetUpright() {
    m_tilt = {};
    if (!Enabled()) RestoreBody();
    Log::Info("[Rotator] droit.");
}

void RotatorModule::OnUpdate() {
    // A PENDING RESTORE IS RETRIED UNTIL IT LANDS. The rig is unresolvable exactly around a respawn
    // or an avatar load -- which is also when the rotator tends to be switched off -- and bailing
    // there once left the body tilted for good with the switch reading OFF.
    if (m_restorePending) RestoreBody();

    void* rig = Rig();
    if (!rig) return;

    double now = Engine::Time();
    float dt = (m_lastTick > 0.0) ? static_cast<float>(now - m_lastTick) : 0.016f;
    if (dt > 0.1f) dt = 0.1f;
    m_lastTick = now;

    // Arrows pitch, PgUp/PgDn roll. Applied in the BODY's own frame, so the axes stay meaningful
    // once you are past vertical.
    float step = m_turnSpeed * dt;
    auto turn = [&](float ax, float ay, float az, float deg) {
        if (std::fabs(deg) < 1e-4f) return;
        m_tilt = QMul(m_tilt, QAngleAxis(deg, ax, ay, az));
    };
    if (Key(VK_UP))    turn(1, 0, 0,  step);
    if (Key(VK_DOWN))  turn(1, 0, 0, -step);
    if (Key(VK_PRIOR)) turn(0, 0, 1,  step);
    if (Key(VK_NEXT))  turn(0, 0, 1, -step);

    static bool flipWas = false;
    if (Key(VK_RSHIFT) && KeyDown('F', flipWas)) Flip();
    static bool resetWas = false;
    if (Key(VK_RSHIFT) && KeyDown(VK_BACK, resetWas)) ResetUpright();

    Quat current;
    if (!GetRotation(rig, current)) return;

    // Did VRChat rewrite the rig since our last write? If so that is a fresh heading and we adopt
    // it. If not, what we are reading is our own composed rotation, and re-deriving a yaw from it
    // would drift -- and is degenerate at pitch +-90 anyway.
    if (QAngle(current, m_applied) > 0.05f) {
        float f[3];
        QForward(current, f);
        f[1] = 0.f;
        float len = std::sqrt(f[0] * f[0] + f[2] * f[2]);
        if (len > 0.01f) m_yaw = std::atan2(f[0] / len, f[2] / len) * kRad2Deg;
    } else if (!m_holdProbed) {
        m_holdProbed = true;
        Log::Info("[Rotator] the rig rotation holds frame to frame -- the tilt takes.");
    }

    Quat want = QMul(QAngleAxis(m_yaw, 0, 1, 0), m_tilt);

    // GRAVITY FOLLOWS THE TILT, NOT THE SWITCH: arming the rotator while upright must not silently
    // switch your gravity off.
    bool tilted = QAngle(m_tilt, Quat{}) > 0.5f;
    HoldGravity(tilted && m_holdGravity);

    Quat delta = QMul(want, QInv(current));
    float deg = 0.f, axis[3];
    QToAngleAxis(delta, deg, axis);
    if (deg > 180.f) deg -= 360.f;
    float pivot[3];
    if (std::fabs(deg) > 0.01f && Pivot(rig, pivot)) RotateAround(rig, pivot, axis, deg);
    else SetRotation(rig, want);          // nothing to pivot about: keep the heading exact

    Quat after;
    if (GetRotation(rig, after)) m_applied = after;
}

}
