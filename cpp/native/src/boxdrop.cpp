#include "boxdrop.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "hooks.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cmath>
#include <cstdlib>
#include <cstring>
#include <string>

namespace VRCA {

namespace {
    // MSVC x64: a Vector3 is 12 bytes, which is not 1/2/4/8, so it is passed BY REFERENCE. The
    // two Vector3 parameters therefore arrive as POINTERS we can write through in place -- which
    // is the whole reason the offset can be applied without rebuilding the call.
    //   rcx = this, rdx = Player*, r8 = Vector3* , r9 = Vector3*, stack: String*, int32, MethodInfo*
    using SendFn = void (*)(void* self, void* player, float* a, float* b,
                            void* str, int32_t n, void* method);

    SendFn g_original = nullptr;
    bool   g_active   = false;
    bool   g_hooked   = false;
    float  g_offset[3] = { 0.f, -12.f, 0.f };   // straight down, under the floor
    long long g_mutated = 0;
    int    g_recon = 0;
    int    g_posArg = -1;      // which of the two Vector3s is the position; -1 until proven

    // Applied in its own function: __try may not share a scope with anything that unwinds, and a
    // bad pointer here must leave the send untouched rather than take the frame down.
    bool OffsetGuarded(float* v) {
        __try {
            v[0] += g_offset[0];
            v[1] += g_offset[1];
            v[2] += g_offset[2];
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    bool ReadGuarded(float const* v, float out[3]) {
        __try {
            out[0] = v[0]; out[1] = v[1]; out[2] = v[2];
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    void SendDetour(void* self, void* player, float* a, float* b,
                    void* str, int32_t n, void* method) {
        // RECON FIRST. Which Vector3 is the position is READ, not assumed: a position moves with
        // the player and sits at world scale, while a velocity hovers near zero when standing
        // still. The first few sends are logged, and the one that looks like a world position is
        // the one that gets offset.
        if (g_recon < 5) {
            ++g_recon;
            float va[3]{}, vb[3]{};
            bool oka = a && ReadGuarded(a, va);
            bool okb = b && ReadGuarded(b, vb);
            Log::Writef("Info", "[BoxDrop] envoi %d : A=(%.2f %.2f %.2f) B=(%.2f %.2f %.2f)",
                        g_recon,
                        static_cast<double>(oka ? va[0] : 0), static_cast<double>(oka ? va[1] : 0),
                        static_cast<double>(oka ? va[2] : 0),
                        static_cast<double>(okb ? vb[0] : 0), static_cast<double>(okb ? vb[1] : 0),
                        static_cast<double>(okb ? vb[2] : 0));
            if (g_posArg < 0 && oka && okb) {
                float ma = std::fabs(va[0]) + std::fabs(va[1]) + std::fabs(va[2]);
                float mb = std::fabs(vb[0]) + std::fabs(vb[1]) + std::fabs(vb[2]);
                // The bigger magnitude over several standing-still sends is the world position.
                if (g_recon >= 3) {
                    g_posArg = (ma >= mb) ? 0 : 1;
                    Log::Writef("Info", "[BoxDrop] the position field is argument %c.",
                                g_posArg == 0 ? 'A' : 'B');
                }
            }
        }

        if (g_active && g_posArg >= 0) {
            float* v = (g_posArg == 0) ? a : b;
            if (v && OffsetGuarded(v)) ++g_mutated;
        }

        g_original(self, player, a, b, str, n, method);
    }

    // The seam, by SIGNATURE: a 5-argument instance method taking
    // (Player, Vector3, Vector3, String, Int32). Never a per-build name.
    void* FindSeam() {
        void* ser = Il2::FindClass("VRC.Networking.FlatBufferNetworkSerializer");
        if (!ser) ser = Il2::FindClass("FlatBufferNetworkSerializer");
        if (!ser) return nullptr;

        for (void* k = ser; k; k = Il2::ClassParent(k)) {
            void* it = nullptr;
            while (void* m = Il2::NextMethod(k, &it)) {
                if (Il2::MethodParamCount(m) != 5) continue;
                char const* p0 = nullptr;
                void* c0 = Il2::MethodParamClass(m, 0);
                if (c0) p0 = Il2::ClassName(c0);
                if (!p0 || !std::strstr(p0, "Player")) continue;

                auto named = [&](int i, char const* want) {
                    void* c = Il2::MethodParamClass(m, i);
                    char const* n = c ? Il2::ClassName(c) : nullptr;
                    return n && std::strcmp(n, want) == 0;
                };
                if (!named(1, "Vector3") || !named(2, "Vector3")) continue;
                if (!named(3, "String") || !named(4, "Int32")) continue;
                return m;
            }
        }
        return nullptr;
    }
}

BoxDropModule::BoxDropModule()
    : Module("Box Drop", "your network box drops under the map while you play normally")
{
    Actions::Register("boxDrop", [](std::string const&, void* u) {
        auto* m = static_cast<BoxDropModule*>(u);
        m->SetEnabled(!m->Enabled());
        ScreenUI::Toast(m->Enabled() ? "Box drop ON" : "Box drop OFF");
    }, this);
    Actions::Register("boxDropX", [](std::string const& v, void*) { SetAxis('x', static_cast<float>(atof(v.c_str()))); });
    Actions::Register("boxDropY", [](std::string const& v, void*) { SetAxis('y', static_cast<float>(atof(v.c_str()))); });
    Actions::Register("boxDropZ", [](std::string const& v, void*) { SetAxis('z', static_cast<float>(atof(v.c_str()))); });

    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<BoxDropModule*>(u);
        out += ",\"boxDrop\":" + Json::Bool(m->Enabled());
        out += ",\"boxDropStatus\":" + Json::Str(
            !m->Enabled() ? std::string("inactive")
                          : "box sent to " + Json::Num(g_offset[0]) + " / "
                            + Json::Num(g_offset[1]) + " / " + Json::Num(g_offset[2]));
    }, this);

    Config::Float("Box Drop", "OffsetX", "offset sent on X", &g_offset[0], -200.f, 200.f);
    Config::Float("Box Drop", "OffsetY", "offset sent on Y (negative = under the map)", &g_offset[1], -200.f, 200.f);
    Config::Float("Box Drop", "OffsetZ", "offset sent on Z", &g_offset[2], -200.f, 200.f);
}

void BoxDropModule::SetAxis(char axis, float v) {
    switch (axis) {
        case 'x': g_offset[0] = v; break;
        case 'y': g_offset[1] = v; break;
        case 'z': g_offset[2] = v; break;
        default: return;
    }
    Log::Writef("Info", "[BoxDrop] offset (%.1f %.1f %.1f)", static_cast<double>(g_offset[0]),
                static_cast<double>(g_offset[1]), static_cast<double>(g_offset[2]));
}

void BoxDropModule::OnEnable()  { g_active = true;  Log::Info("[BoxDrop] ON"); }

void BoxDropModule::OnDisable() {
    // The hook STAYS. A detour removed and re-added is the risky operation, not a branch that is
    // not taken: with g_active false the detour costs one test and forwards.
    g_active = false;
    Log::Writef("Info", "[BoxDrop] OFF -- %lld envoi(s) avaient ete decales.", g_mutated);
}

void BoxDropModule::OnUpdate() {
    if (g_hooked) return;
    double now = Engine::Time();
    if (now < m_next) return;
    m_next = now + 1.0;
    if (++m_attempts > 30) {
        if (m_attempts > 31) { SetEnabled(false); return; }
        if (m_attempts == 31) {
            // EVIDENCE BEFORE GUESSWORK. The signature the C# mod hooked does not exist here, so
            // list what the serializer actually offers with a Vector3 in it -- that is what names
            // the real seam instead of another guess at a parameter list.
            Log::Warn("[BoxDrop] (Player,Vector3,Vector3,String,Int32) not found. Serialiser methods:");
            void* ser = Il2::FindClass("VRC.Networking.FlatBufferNetworkSerializer");
            if (!ser) ser = Il2::FindClass("FlatBufferNetworkSerializer");
            if (ser) {
                int n = 0;
                for (void* k = ser; k; k = Il2::ClassParent(k)) {
                char const* kn = Il2::ClassName(k);
                if (kn && (std::strcmp(kn, "MonoBehaviour") == 0 || std::strcmp(kn, "Behaviour") == 0 ||
                           std::strcmp(kn, "Component") == 0 || std::strcmp(kn, "Object") == 0))
                    break;
                Log::Writef("Info", "[BoxDrop] -- classe '%s' --", kn ? kn : "?");
                void* it = nullptr;
                while (void* m = Il2::NextMethod(k, &it)) {
                    int pc = Il2::MethodParamCount(m);
                    if (pc < 1 || pc > 8) continue;
                    std::string sig;
                    bool hasVec = false;
                    for (int i = 0; i < pc; ++i) {
                        void* pcl = Il2::MethodParamClass(m, i);
                        char const* pn = pcl ? Il2::ClassName(pcl) : "?";
                        if (pn && std::strcmp(pn, "Vector3") == 0) hasVec = true;
                        if (i) sig += ", ";
                        sig += pn ? pn : "?";
                    }
                    if (!hasVec) continue;
                    Log::Writef("Info", "[BoxDrop]   %s(%s)", Il2::MethodName(m), sig.c_str());
                    if (++n >= 25) break;
                }
                if (n >= 25) break;
                }
                Log::Writef("Info", "[BoxDrop] %d method(s) with a Vector3 across the whole chain.", n);
                if (n == 0)
                    Log::Warn("[BoxDrop] the pose no longer goes through a managed method taking "
                              "Vector3 on this build -- the seam is gone, module disabled.");
            } else {
                Log::Warn("[BoxDrop] the serialiser itself was not found.");
            }
        }
        SetEnabled(false);      // nothing to hook: do not pretend to be on
        return;
    }

    void* seam = FindSeam();
    if (!seam) return;
    void* code = *static_cast<void**>(seam);         // MethodInfo::methodPointer
    if (!code) return;

    void* tramp = nullptr;
    if (!Hooks::Create(code, reinterpret_cast<void*>(&SendDetour), &tramp)) {
        Log::Warn("[BoxDrop] send-method detour refused.");
        m_attempts = 99;
        return;
    }
    g_original = reinterpret_cast<SendFn>(tramp);
    g_hooked = true;
    Log::Writef("Info", "[BoxDrop] seam hooked: %s. The first sends are logged to "
                        "confirm the position field instead of guessing it.",
                Il2::MethodName(seam));
}

}
