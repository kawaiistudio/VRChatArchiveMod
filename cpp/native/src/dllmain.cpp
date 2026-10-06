#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include "anticrash.hpp"
#include "bridge.hpp"
#include "features.hpp"
#include "engine.hpp"
#include "hooks.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "module.hpp"
#include "avatar.hpp"
#include "clicktp.hpp"
#include "vatags.hpp"
#include "glowesp.hpp"
#include "badapple.hpp"
#include "blocked.hpp"
#include "boxdrop.hpp"
#include "soundboard.hpp"
#include "extras.hpp"
#include "mark.hpp"
#include "mimic.hpp"
#include "netself.hpp"
#include "objects.hpp"
#include "photon.hpp"
#include "pickups.hpp"
#include "udon.hpp"
#include "wings.hpp"
#include "world.hpp"
#include "orbit.hpp"
#include "rotator.hpp"
#include "portals.hpp"
#include "quickmenu.hpp"
#include "recovery.hpp"
#include "trueview.hpp"
#include "uidump.hpp"

#include <atomic>
#include <string>

namespace VRCA { void ObfSelfTest(); }   // src/obf_selftest.cpp

// VRCHAT ARCHIVE MOD -- NATIVE ENGINE ENTRY.
//
// The managed BepInEx loader LoadLibrary's this DLL and then calls VRCA_Start once, AFTER the
// chainloader has run and GameAssembly.dll is up. Real work never happens in DllMain (it runs under
// the loader lock, far too early to touch il2cpp); DllMain only opts out of thread notifications.
namespace {
    std::atomic<bool> g_started{false};
}

extern "C" __declspec(dllexport) int VRCA_Start(char const* dataDir, char const* logDir) {
    if (g_started.exchange(true)) return 1;   // idempotent: a double LoadLibrary must not re-init

    VRCA::Log::Init(logDir ? logDir : ".");
    VRCA::Log::Info("VRChat Archive Mod (C++) -- starting the native engine.");
    VRCA::Log::Writef("Info", "data=%s", dataDir ? dataDir : "?");

    if (!VRCA::Il2::Init()) {
        VRCA::Log::Error("il2cpp engine unavailable -- stopping (GameAssembly missing or exports unresolved).");
        return 0;
    }

    // Foundation smoke test: resolve a class every build has and prove the metadata is walkable.
    if (void* obj = VRCA::Il2::FindClass("System.Object")) {
        VRCA::Log::Writef("Info", "metadata OK : System.Object = 0x%llX",
                          static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(obj)));
    } else {
        VRCA::Log::Warn("metadata: System.Object not found (empty class index?)");
    }

    if (!VRCA::Hooks::Init())
        VRCA::Log::Warn("hook backend unavailable -- pump and hooks disabled.");

    // Prove the compile-time string hiding round-trips on this build before any patch relies on a
    // hidden il2cpp target name resolving.
    VRCA::ObfSelfTest();

    // Recover the renamed VRChat classes now, before any feature asks for them by name.
    VRCA::Recovery::RecoverCoreClasses();

    // A heartbeat witness: logs once a second so the pump is provably alive without any feature yet.
    struct Heartbeat : VRCA::Module {
        Heartbeat() : Module("Heartbeat", "proof that the pump is beating") { m_enabled = true; }
        double last = 0.0; int beats = 0;
        void OnUpdate() override {
            double t = VRCA::Engine::Time();
            if (t - last < 1.0) return;
            last = t;
            if (beats < 10) VRCA::Log::Writef("Info", "pump alive: beat %d at %.0fs", ++beats, t);
        }
    };
    VRCA::Engine::Register<Heartbeat>();
    VRCA::Engine::Register<VRCA::QuickMenuModule>();
    VRCA::Engine::Register<VRCA::WingsModule>();
    VRCA::Engine::Register<VRCA::ClickTpModule>();
    VRCA::Engine::Register<VRCA::VaTagsModule>();
    VRCA::Engine::Register<VRCA::BridgeModule>();
    VRCA::Engine::Register<VRCA::MovementModule>();
    VRCA::Engine::Register<VRCA::FlyModule>();
    VRCA::Engine::Register<VRCA::SelfHideModule>();
    VRCA::Engine::Register<VRCA::GravityModule>();
    VRCA::Engine::Register<VRCA::GhostModule>();
    VRCA::Engine::Register<VRCA::FastSyncModule>();
    VRCA::Engine::Register<VRCA::AntiCrashModule>();
    VRCA::Engine::Register<VRCA::OrbitModule>();
    VRCA::Engine::Register<VRCA::AvatarModule>();
    VRCA::Engine::Register<VRCA::RotatorModule>();
    VRCA::Engine::Register<VRCA::ObjectOrbitModule>();
    VRCA::Engine::Register<VRCA::ElevatorModule>();
    VRCA::Engine::Register<VRCA::FloatObjectsModule>();
    VRCA::Engine::Register<VRCA::MarkModule>();
    VRCA::Engine::Register<VRCA::MimicModule>();
    VRCA::Engine::Register<VRCA::PortalModule>();
    VRCA::Engine::Register<VRCA::BackgroundsModule>();
    VRCA::Engine::Register<VRCA::VideoUrlModule>();
    VRCA::Engine::Register<VRCA::BlockedByModule>();
    VRCA::Engine::Register<VRCA::BoxDropModule>();
    VRCA::Engine::Register<VRCA::SoundboardModule>();
    VRCA::Engine::Register<VRCA::BadAppleModule>();
    VRCA::Engine::Register<VRCA::PhotonModule>();
    VRCA::Engine::Register<VRCA::ForcePickupModule>();
    VRCA::Engine::Register<VRCA::ForceGrabModule>();
    VRCA::Engine::Register<VRCA::UdonModule>();
    VRCA::Engine::Register<VRCA::EspModule>();
    VRCA::Engine::Register<VRCA::GlowEspModule>();
    VRCA::Engine::Register<VRCA::UiDumpModule>();
    VRCA::Engine::Register<VRCA::TrueViewModule>();

    // The world tail reads a FILE on its own thread: it needs no il2cpp and no game thread, so it
    // can start before the pump and already know where we are by the time the client connects.
    VRCA::World::Start();

    if (VRCA::Engine::Start())
        VRCA::Log::Info("VRChat Archive Mod (C++): engine loaded, pump armed.");
    else
        VRCA::Log::Warn("VRChat Archive Mod (C++): engine loaded but pump NOT armed.");
    return 1;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
