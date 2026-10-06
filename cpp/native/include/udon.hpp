#pragma once
#include "module.hpp"

#include <string>
#include <unordered_set>
#include <vector>

namespace VRCA {

    // UDON MANAGER -- see, switch off, and fire the world's own scripts.
    //
    // Every UdonBehaviour in the scene is listed with its path, its distance and who owns it. From
    // the client you can switch one off locally, take ownership, list its entry points and run one.
    //
    // THE HANDLE IS AN INSTANCE ID. The desktop app cannot hold a Behaviour across a socket, so
    // every row it draws is addressed by a number and nothing else. Unity's instance id is unique
    // for the object's lifetime and survives a rescan -- exactly the lifetime a selection needs.
    //
    // GLOBAL vs LOCAL is VRChat's own boundary, not a guess: SendCustomNetworkEvent REFUSES any
    // entry point whose name starts with '_' (the Udon lifecycle hooks and anything the author
    // prefixed), so those can only ever run on the client that owns them. Everything else is
    // network-ELIGIBLE -- "eligible" being the honest word, since whether a world actually
    // broadcasts a given event is a property of its code, not of the name.
    //
    // EVERY LOCAL DISABLE IS REMEMBERED and put back on restore or on world change: leaving a
    // world's scripts switched off is how a world looks broken for the rest of the session.
    class UdonModule : public Module {
    public:
        UdonModule();
        void OnUpdate()  override;
        void OnDisable() override;

        struct Entry {
            int         id = 0;         // Unity instance id -- the client's only handle
            void*       behaviour = nullptr;
            std::string path;
            std::string shortName;
            float       dist = 0.f;
            bool        offByUs = false;
        };

        void Rescan();
        void RestoreAll();
        Entry* ById(int id);

        void SetBehaviourEnabled(Entry& e, bool on);
        void Interact(Entry& e);
        bool RunLocal(Entry& e, std::string const& ev);
        bool RunGlobal(Entry& e, std::string const& ev);
        void TakeOwnership(Entry& e);

        [[nodiscard]] std::vector<std::string> EntryPoints(Entry& e);

        // The script's variables, with their current values. Read through the program's SYMBOL
        // TABLE, which hands back a plain string[]: the public-variable table's VariableSymbols is
        // a Dictionary.KeyCollection, and walking that through an enumerator is what took the game
        // down in the C# mod. An array cannot do that.
        struct Var { std::string name, value, type; };
        [[nodiscard]] std::vector<Var> Variables(Entry& e);

        bool SetVariable(Entry& e, std::string const& name, std::string const& value);
        bool RequestSerialization(Entry& e);   // push a Manual-sync script's values now

        // ONE EVENT AIMED AT ONE PLAYER.
        //
        // Three ways to decide which script is theirs, and the choice matters:
        //   "var"   -- ask the object: a candidate whose variable holds this player's id IS theirs.
        //              The only correct answer for a world that keeps its per-player scripts in one
        //              folder instead of on the players.
        //   "pos"   -- the matching object nearest the player.
        //   "label" -- nearest the world label showing their name.
        // The two geometric modes are meaningless when a world does not put a script on each
        // person, which is why "var" exists and is offered first.
        bool RunOnPlayer(std::string const& userId, std::string const& scope, std::string const& match,
                         std::string const& ev, std::string const& by, std::string const& byVar,
                         std::string const& pattern);

        // One event on every script whose object name matches -- the client's world presets.
        // Queued, never fired in one frame: 200 network events in a frame is the burst that
        // disconnects you.
        void RunMatching(std::string const& scope, std::string const& match,
                         std::string const& ev, std::string const& pattern);
        // THE CLIENT'S UDON MANAGER READS ONE OBJECT, AT THE ROOT OF THE SYNC, CALLED "udon".
        //
        // Not a result in the shared results queue: that queue is drained by whichever page is
        // open, so an answer put in it can be taken by another page and never reach this one --
        // and the field names differ besides. Every builder below therefore returns a FRAGMENT,
        // and Answer() wraps it in the object the client actually parses (status / worldId /
        // worldName / total / shown / off plus the one block that was asked for).
        //
        // A missing "udon" key means UNCHANGED to the client, which is why nothing is published
        // until something was asked for: an idle sync must not make the page rebuild its table.
        [[nodiscard]] std::string ItemsJson();                // -> [ {id,n,p,d,on,ours,o,m} ]
        [[nodiscard]] std::string EventsJson(int id);         // -> {id,list:[{n,g}]}
        [[nodiscard]] std::string VarsJson(int id);           // -> {id,canWrite,list:[{n,t,v,e,s}]}
        [[nodiscard]] std::string AllEventsJson();            // -> [ {id,g:[],l:[]} ]
        [[nodiscard]] std::string DiffJson(int id, std::string const& ev,
                                           std::vector<Var> const& before);
        // key = "items" / "events" / "vars" / "eventsAll" / "runDiff"; body = that fragment.
        void Answer(char const* key, std::string const& body);

        // The live rows, for a feature that needs to walk the world's scripts itself
        // (the video player is found by what a script HOLDS, not by its name).
        [[nodiscard]] std::vector<Entry>& Rows() { return m_items; }

    private:
        std::vector<Entry>     m_items;
        std::unordered_set<int> m_ourOff;   // ids WE switched off, so restore puts back only ours
        double                 m_nextScan = 0.0;
        std::string            m_status;
        std::string            m_out;        // the next "udon" object, drained by the sync

        void PumpPending();
        std::vector<int> m_pending;          // ids a matched preset still has to fire at
        std::string      m_pendingEvent;
        bool             m_pendingGlobal = false;
        int              m_pendingTotal = 0, m_pendingDone = 0;
        double           m_nextFire = 0.0;
        bool             m_selfTested = false;
    };
}
