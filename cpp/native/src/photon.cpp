#include "photon.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "hooks.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "screenui.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <atomic>
#include <cstring>
#include <string>

namespace VRCA {

namespace {
    using OnEventFn = void (*)(void* self, void* ev, void* method);

    OnEventFn g_original = nullptr;
    int  g_codeOff   = -1;        // EventData.Code   (byte)
    int  g_senderOff = -1;        // EventData.Sender (int)

    // Read by the detour on the NETWORK thread, written from the game thread. Plain scalars,
    // written whole, so a torn read is not possible; atomics make the intent explicit.
    std::atomic<bool> g_guard{ false };
    std::atomic<int>  g_perSecond{ 60 };

    // Counts per code, and the rate window. Flat arrays rather than a map: this is touched
    // hundreds of times a second and a hash lookup under a lock would be the cost of the feature.
    std::atomic<long long> g_count[256];
    std::atomic<long long> g_dropped[256];

    // The rate window: one second of counts per (actor, code), with actors folded into 64 slots.
    // Folding is deliberate -- a perfect table would be 256 x actors and the collision only ever
    // makes the limit SLIGHTLY stricter for two actors who happen to share a slot.
    constexpr int kActorSlots = 64;
    std::atomic<int>       g_window[kActorSlots][256];
    std::atomic<long long> g_windowStart{ 0 };

    // Codes that must NEVER be rate-limited: room state and the core streams. Limiting these is
    // how a guard mutes the whole instance instead of one spammer.
    bool NeverRate(unsigned char code) {
        switch (code) {
            case 0: case 1: case 2: case 3: case 4: case 5: case 6:
            case 209: case 210: case 226: case 227: case 228: case 229:
            case 230: case 253: case 254: case 255:
                return true;
            default:
                return false;
        }
    }

    void OnEventDetour(void* self, void* ev, void* method) {
        // NOTHING IS KEPT: Photon recycles this object, so everything needed is read right now.
        if (!ev || g_codeOff < 0) { g_original(self, ev, method); return; }

        unsigned char code = *reinterpret_cast<unsigned char*>(static_cast<char*>(ev) + g_codeOff);
        int sender = (g_senderOff >= 0)
                   ? *reinterpret_cast<int*>(static_cast<char*>(ev) + g_senderOff)
                   : -1;

        g_count[code].fetch_add(1, std::memory_order_relaxed);

        if (g_guard.load(std::memory_order_relaxed) && sender > 0 && !NeverRate(code)) {
            long long now = static_cast<long long>(GetTickCount64());
            long long start = g_windowStart.load(std::memory_order_relaxed);
            if (now - start >= 1000) {
                // A new second: clear the window. A racing thread may clear twice, which costs a
                // slightly longer window and nothing else.
                g_windowStart.store(now, std::memory_order_relaxed);
                for (auto& row : g_window)
                    for (auto& cell : row) cell.store(0, std::memory_order_relaxed);
            }
            int slot = (sender & (kActorSlots - 1));
            int n = g_window[slot][code].fetch_add(1, std::memory_order_relaxed) + 1;
            if (n > g_perSecond.load(std::memory_order_relaxed)) {
                g_dropped[code].fetch_add(1, std::memory_order_relaxed);
                return;                       // dropped: simply not forwarded
            }
        }

        g_original(self, ev, method);
    }

    // The class by SHAPE: whatever declares a method called OnEvent taking one parameter whose
    // type is named like Photon's EventData. VRCNetworkingClient is renamed on this build, the
    // method name is not.
    void* FindOnEvent(void** outEventClass) {
        for (char const* asmName : { "VRChat", "Assembly-CSharp", "VRCCore-Standalone",
                                     "Photon3Unity3D", "PhotonRealtime" }) {
            for (void* k : Il2::AssemblyClasses(asmName)) {
                void* it = nullptr;
                while (void* m = Il2::NextMethod(k, &it)) {
                    char const* mn = Il2::MethodName(m);
                    if (!mn || std::strcmp(mn, "OnEvent") != 0) continue;
                    if (Il2::MethodParamCount(m) != 1) continue;
                    void* pc = Il2::MethodParamClass(m, 0);
                    char const* pn = pc ? Il2::ClassName(pc) : nullptr;
                    if (!pn || !std::strstr(pn, "EventData")) continue;
                    if (outEventClass) *outEventClass = pc;
                    return m;
                }
            }
        }
        return nullptr;
    }
}

PhotonModule::PhotonModule()
    : Module("Photon", "see and limit the instance's network events")
{
    Config::Bool("Photon", "Guard",
                 "drop whatever exceeds the per-player limit (world state is never limited)",
                 &m_guard);
    Config::Int ("Photon", "PerSecond", "events per second per player", &m_perSecond, 5, 500);

    Actions::Register("photonGuardReset", [](std::string const&, void* u) {
        static_cast<PhotonModule*>(u)->Reset();
    }, this);

    m_enabled = true;      // the counters are the point; the guard is a separate switch
}

void PhotonModule::Reset() {
    for (int i = 0; i < 256; ++i) {
        g_count[i].store(0, std::memory_order_relaxed);
        g_dropped[i].store(0, std::memory_order_relaxed);
    }
    for (auto& row : g_window)
        for (auto& cell : row) cell.store(0, std::memory_order_relaxed);
    Log::Info("[Photon] compteurs remis a zero.");
    ScreenUI::Toast("Photon: counters reset");
}

std::string PhotonModule::CountsJson() const {
    std::string out = "{\"installed\":";
    out += m_installed ? "true" : "false";
    out += ",\"guard\":";
    out += g_guard.load() ? "true" : "false";
    out += ",\"codes\":[";
    bool first = true;
    for (int i = 0; i < 256; ++i) {
        long long n = g_count[i].load(std::memory_order_relaxed);
        if (!n) continue;
        if (!first) out += ',';
        first = false;
        out += "{\"code\":" + std::to_string(i) + ",\"n\":" + std::to_string(n)
             + ",\"dropped\":" + std::to_string(g_dropped[i].load(std::memory_order_relaxed)) + "}";
    }
    return out + "]}";
}

bool PhotonModule::Install() {
    void* evClass = nullptr;
    void* m = FindOnEvent(&evClass);
    if (!m) return false;

    // THE OFFSETS ARE RESOLVED HERE, on the game thread, and read as raw bytes later. Calling a
    // property getter from the network thread is the thing this design exists to avoid.
    if (evClass) {
        g_codeOff   = Il2::FieldOffset(evClass, "Code");
        g_senderOff = Il2::FieldOffset(evClass, "Sender");
        if (g_senderOff < 0) g_senderOff = Il2::FieldOffset(evClass, "sender");
        Log::Writef("Info", "[Photon] EventData '%s' : Code @%d, Sender @%d.",
                    Il2::ClassName(evClass), g_codeOff, g_senderOff);
    }
    if (g_codeOff < 0) {
        // Without the code every event reads as 0 from nobody, and a limit on that would mute the
        // entire room. Unidentifiable means untouched, so the hook is not installed at all.
        Log::Warn("[Photon] EventData.Code unreadable -- nothing hooked, no risk taken.");
        return false;
    }

    void* code = *static_cast<void**>(m);       // MethodInfo::methodPointer
    if (!code) return false;
    void* tramp = nullptr;
    if (!Hooks::Create(code, reinterpret_cast<void*>(&OnEventDetour), &tramp)) {
        Log::Warn("[Photon] OnEvent detour refused.");
        return false;
    }
    g_original = reinterpret_cast<OnEventFn>(tramp);
    g_windowStart.store(static_cast<long long>(GetTickCount64()));
    m_installed = true;
    Log::Info("[Photon] OnEvent(EventData) hooked -- counting active, guard on demand.");
    return true;
}

void PhotonModule::OnDisable() {
    // The hook STAYS: removing a detour on a method the network thread is inside is the risky
    // operation, not an un-taken branch. With the guard off the detour only counts.
    g_guard.store(false);
}

void PhotonModule::OnUpdate() {
    g_guard.store(m_guard, std::memory_order_relaxed);
    g_perSecond.store(m_perSecond, std::memory_order_relaxed);

    if (m_installed) return;
    double now = Engine::Time();
    if (now < m_next) return;
    m_next = now + 2.0;
    if (++m_attempts > 20) {
        if (m_attempts == 21)
            Log::Warn("[Photon] OnEvent(EventData) still not found -- network layer inactive.");
        return;
    }
    Install();
}

}
