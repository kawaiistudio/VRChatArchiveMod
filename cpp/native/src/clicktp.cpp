#include "clicktp.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cmath>

namespace VRCA {

namespace {
    // The camera's position and forward (the crosshair direction). Same resolution Mark uses.
    bool CameraRay(float origin[3], float dir[3]) {
        static void* mainCam = Il2::FindMethod("UnityEngine.Camera", "get_main", 0);
        static void* getTr   = Il2::FindMethod("UnityEngine.Component", "get_transform", 0);
        static void* getFwd  = Il2::FindMethod("UnityEngine.Transform", "get_forward", 0);
        if (!mainCam || !getTr || !getFwd) return false;
        void* cam = Il2::Invoke(mainCam, nullptr, nullptr);
        if (!Unity::IsAlive(cam)) return false;
        void* tr = Il2::Invoke(getTr, cam, nullptr);
        if (!tr || !Unity::GetPosition(tr, origin)) return false;
        void* f = Il2::Invoke(getFwd, tr, nullptr);
        if (!f) return false;
        auto const* ff = reinterpret_cast<float const*>(static_cast<char*>(f) + 0x10);
        dir[0] = ff[0]; dir[1] = ff[1]; dir[2] = ff[2];
        return true;
    }

    // Physics.Raycast(origin, direction, out RaycastHit, maxDistance). RaycastHit's first three
    // floats are the hit point, which is all a teleport needs.
    bool RayHit(float const o[3], float const d[3], float maxDist, float outPoint[3]) {
        static void* m = Il2::FindMethod("UnityEngine.Physics", "Raycast", 4);
        if (!m) return false;
        float origin[3] = { o[0], o[1], o[2] };
        float dir[3]    = { d[0], d[1], d[2] };
        unsigned char hit[128]{};
        float dist = maxDist;
        void* a[4] = { origin, dir, hit, &dist };
        void* r = Il2::Invoke(m, nullptr, a);
        if (!r || !*reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10)) return false;
        auto const* f = reinterpret_cast<float const*>(hit);
        outPoint[0] = f[0]; outPoint[1] = f[1]; outPoint[2] = f[2];
        return true;
    }

    // Cursor.lockState == Locked (1). Desktop VRChat locks the cursor while you look and frees it
    // in a menu, so this is the cleanest "we are in gameplay, not clicking UI" test. Unknown -> do
    // not block the feature.
    bool CursorLocked() {
        static void* m = Il2::FindMethod("UnityEngine.Cursor", "get_lockState", 0);
        if (!m) return true;
        void* r = Il2::Invoke(m, nullptr, nullptr);
        if (!r) return true;
        return *reinterpret_cast<int*>(static_cast<char*>(r) + 0x10) == 1;   // CursorLockMode.Locked
    }

    // The game window is the foreground one. GetAsyncKeyState reads the PHYSICAL button whatever is
    // focused, so without this a left click in another app could warp you.
    bool GameFocused() {
        HWND fg = GetForegroundWindow();
        if (!fg) return false;
        DWORD pid = 0;
        GetWindowThreadProcessId(fg, &pid);
        return pid == GetCurrentProcessId();
    }
}

ClickTpModule::ClickTpModule()
    : Module("Click TP", "hold right mouse and tap left to teleport where you are aiming")
{
    // Bound so the desktop client can turn it off, but it DEFAULTS ON (owner's ask). The module is
    // always enabled; m_on is the real gate, so the config toggle takes effect without re-register.
    Config::Bool ("Click TP", "Enabled", "hold right mouse + tap left to teleport to your aim", &m_on);
    Config::Float("Click TP", "MaxDistance", "maximum ray distance in metres", &m_maxDistance, 5.f, 1000.f);
    m_enabled = true;      // costs nothing until the two-button combo is pressed

    Actions::RegisterState([](std::string& out, void* u) {
        out += ",\"clickTp\":" + Json::Bool(static_cast<ClickTpModule*>(u)->m_on);
    }, this);
}

void ClickTpModule::OnUpdate() {
    if (!m_on) { m_leftWas = false; return; }

    // Right held + left pressed THIS frame. Edge-detect the left button so one tap teleports once.
    bool right = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
    bool left  = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    bool leftEdge = left && !m_leftWas;
    m_leftWas = left;

    if (!right || !leftEdge) return;
    if (!GameFocused() || !CursorLocked()) return;    // in a menu / another app: not a TP

    float o[3], d[3];
    if (!CameraRay(o, d)) return;
    float pt[3];
    if (!RayHit(o, d, m_maxDistance, pt)) return;      // aimed at the sky: nothing to stand on

    void* local = Player::LocalApi();
    if (!local) return;
    pt[1] += 0.15f;                                     // just above the surface, not clipped into it
    Player::TeleportTo(local, { pt[0], pt[1], pt[2] });
    Player::ZeroVelocity(local);

    float dx = pt[0] - o[0], dy = pt[1] - o[1], dz = pt[2] - o[2];
    Log::Writef("Info", "[ClickTP] teleported to %.1f %.1f %.1f (%.0f m).",
                static_cast<double>(pt[0]), static_cast<double>(pt[1]), static_cast<double>(pt[2]),
                std::sqrt(static_cast<double>(dx * dx + dy * dy + dz * dz)));
}

}
