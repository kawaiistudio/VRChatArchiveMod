#pragma once
#include "module.hpp"

#include <atomic>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

namespace VRCA {

    // THE ACTION REGISTRY.
    //
    // The desktop client sends ~75 distinct action ids (wear, orbit, markPut, udonRun...), each
    // belonging to a feature. Hard-coding them all in the bridge would make it the one file every
    // feature has to touch; instead a module CLAIMS its actions at construction and the bridge
    // dispatches by name. A feature that is not ported yet simply has no claimant, and the bridge
    // says so once -- never silently.
    namespace Actions {
        using Handler = void (*)(std::string const& value, void* user);
        void Register(char const* id, Handler h, void* user = nullptr);
        // Returns false when nothing claimed that id.
        bool Dispatch(std::string const& id, std::string const& value);

        // ANSWER THE CLIENT. A module that was asked a question -- dump this avatar, list these
        // variables -- posts its answer here and the bridge carries it in the next sync. Keeping
        // it in the registry rather than in the bridge means a new feature never has to touch the
        // bridge to be able to reply.
        void PostResult(char const* kind, std::string const& id, std::string const& text);

        // Game thread, bridge only: takes every pending result as a JSON array body, or empty.
        std::string DrainResults();

        // THE STATE CHANNEL -- the mirror image of the action one.
        //
        // An action travels client -> mod. Everything the client SHOWS travels the other way, and
        // it is not optional: the Fun page reads `rotator`, `ghostStatus`, `elevatorCount` and
        // fifty-odd more out of every sync, and a key that is absent reads as off / empty / zero.
        // That is what made the ported features look dead even when the action behind the button
        // worked -- the button fired, the mod did it, and the card went on saying OFF because
        // nothing ever reported back.
        //
        // So a module publishes its own state the same way it claims its own actions: it registers
        // a contributor at construction and appends its keys. The bridge stays a transport and
        // never learns what a feature is called.
        //
        // The contributor runs on the GAME THREAD, inside BuildSync, so it may read live state
        // directly -- and for the same reason it must never block. It appends `,"key":value` pairs
        // (leading comma, Json:: builds the values).
        using StateFn = void (*)(std::string& out, void* user);
        void RegisterState(StateFn fn, void* user = nullptr);
        void EmitState(std::string& out);
    }

    // JSON VALUES, one implementation. Three files had grown their own escaper; a module that
    // reports state needs one too, and a fourth copy is how two of them end up disagreeing about
    // control characters.
    namespace Json {
        std::string Str(std::string const& s);
        std::string Bool(bool b);
        std::string Num(double v);     // always with a dot, never the locale's comma
        std::string Int(long long v);
    }


    // THE CLIENT BRIDGE -- the channel the desktop app drives the mod through.
    //
    // The client is the SERVER (LocalBridge on 127.0.0.1:8791) and the mod is the HTTP CLIENT: the
    // game process never opens a port, so it polls. POST /mod/schema once per session declares
    // every setting as {section,key,type,desc,range} (the client keys them "section/key"); POST
    // /mod/sync carries the values and the roster and is answered with
    // {"ok","lp","hot","commands":[{seq,kind,id,value}]} -- a LONG POLL the client holds open.
    //
    // Two rules carried over from the C# bridge, both learned the hard way:
    //  1. The HTTP runs on a worker thread, so NOTHING a reply asks for may touch il2cpp there.
    //     Commands are queued and applied from the game thread.
    //  2. That queue is PACED. Draining it whole in one frame turned the client's deliberate
    //     spacing into a burst, and a fixed cap silently dropped the overflow.
    class BridgeModule : public Module {
    public:
        BridgeModule();
        ~BridgeModule() override;
        void OnUpdate() override;

        char m_host[128] = "http://127.0.0.1:8791";

    private:
        struct Command { long long seq = 0; std::string kind, id, value; };

        void        Worker();
        std::string BuildSchema() const;
        std::string BuildSync();              // game thread only: reads live players
        void        Apply(Command const& c);  // game thread only

        std::thread       m_worker;
        std::atomic<bool> m_started{false}, m_stopping{false};
        std::atomic<bool> m_schemaPending{true}, m_hot{false}, m_connected{false};

        std::mutex  m_payloadLock;
        std::string m_payload;
        std::string m_status = "stopped";
        std::string m_pendingDump;   // answer to a dump action, sent in the next sync
        std::string m_decStatus = "non verifie";   // what the server said about the cache decryptor

        std::mutex          m_queueLock;
        std::deque<Command> m_queue;

        std::string m_dexStatus = "non verifie";   // what the server said about the Dex patcher

        double m_credit = 0.0, m_lastPump = 0.0, m_lastBuild = 0.0;
        int    m_applied = 0, m_decAttempts = 0, m_dexAttempts = 0;
    };
}
