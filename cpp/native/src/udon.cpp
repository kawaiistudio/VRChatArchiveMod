#include "udon.hpp"
#include "world.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"
#include "unity.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace VRCA {

namespace {
    constexpr char const* kUdon = "VRC.Udon.UdonBehaviour";

    std::string J(std::string const& s) {
        std::string o = "\"";
        for (unsigned char c : s) {
            switch (c) {
                case '"':  o += "\\\""; break;
                case '\\': o += "\\\\"; break;
                case '\n': o += "\\n";  break;
                case '\r': o += "\\r";  break;
                case '\t': o += "\\t";  break;
                default:
                    if (c < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); o += b; }
                    else o += static_cast<char>(c);
            }
        }
        return o + "\"";
    }

    int InstanceIdOf(void* unityObject) {
        static void* m = Il2::FindMethod("UnityEngine.Object", "GetInstanceID", 0);
        if (!unityObject || !m) return 0;
        void* r = Il2::Invoke(m, unityObject, nullptr);
        return r ? *reinterpret_cast<int*>(static_cast<char*>(r) + 0x10) : 0;
    }

    // The full path of a transform in the hierarchy -- what makes one of two hundred identical
    // "Door" scripts identifiable in the client's table.
    std::string PathOf(void* transform) {
        std::vector<std::string> parts;
        int guard = 0;
        for (void* t = transform; t && guard++ < 32; t = Unity::Parent(t))
            parts.push_back(Unity::Name(Unity::GameObjectOf(t)));
        std::string out;
        for (size_t i = parts.size(); i-- > 0; ) {
            if (!out.empty()) out += '/';
            out += parts[i];
        }
        return out;
    }

    std::vector<void*> AllUdon() {
        std::vector<void*> out;
        static void* findAll = Il2::FindMethod("UnityEngine.Resources", "FindObjectsOfTypeAll", 1);
        void* k = Il2::FindClass(kUdon);
        if (!findAll || !k) return out;
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!cgt || !tgo) return out;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* ty = reinterpret_cast<CGT>(cgt)(k);
        void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
        if (!sysType) return out;
        void* a[1] = { sysType };
        void* arr = Il2::Invoke(findAll, nullptr, a);
        int n = Il2::ArrayLength(arr);
        out.reserve(static_cast<size_t>(n));
        for (int i = 0; i < n; ++i) if (void* b = Il2::ArrayAt(arr, i)) out.push_back(b);
        return out;
    }


    // Names the first link of the variable chain that failed, exactly once per session: "no
    // variables" and "no symbol table" look identical from the outside and must not.
    void Step(char const* why) {
        static bool said = false;
        if (said) return;
        said = true;
        Log::Writef("Warning", "[Udon] variables unavailable: %s.", why);
    }


    // AN ARRAY THAT IS NOT AN ARRAY.
    //
    // GetPrograms hands back an ImmutableArray<string>, and GetSymbols does the same on this
    // build: a STRUCT whose single field is the real array. Boxed, that field sits right after the
    // 0x10 object header. Measuring the box itself gives zero, which is how a perfectly good
    // symbol table read as empty. A List<T> is unwrapped the same way (items at +0x10), so one
    // helper covers every shape these calls have taken.
    void* UnwrapArray(void* maybe) {
        if (!maybe) return nullptr;

        // ASK WHAT IT IS, DO NOT GUESS FROM ITS LENGTH. Reading the length field of a boxed
        // ImmutableArray gives whatever bytes happen to sit there, so "is this length plausible?"
        // is not a test -- it answered differently for the same object between two runs. An
        // il2cpp array's class name ends in "[]"; anything else is a wrapper whose single field
        // is the real array, sitting right after the 0x10 object header.
        void* k = Il2::ClassOfObject(maybe);
        char const* n = k ? Il2::ClassName(k) : nullptr;
        size_t len = n ? std::strlen(n) : 0;
        if (len >= 2 && n[len - 2] == '[' && n[len - 1] == ']') return maybe;

        void* inner = *reinterpret_cast<void**>(static_cast<char*>(maybe) + 0x10);
        if (!inner) return nullptr;
        void* ik = Il2::ClassOfObject(inner);
        char const* in = ik ? Il2::ClassName(ik) : nullptr;
        size_t ilen = in ? std::strlen(in) : 0;
        if (ilen >= 2 && in[ilen - 2] == '[' && in[ilen - 1] == ']') return inner;
        return nullptr;          // neither is an array: better nothing than a wild read
    }

    void* NewString(std::string const& s) {
        static void* fn = Il2::Export("il2cpp_string_new");
        if (!fn) return nullptr;
        using F = void*(*)(char const*);
        return reinterpret_cast<F>(fn)(s.c_str());
    }
}

UdonModule::UdonModule()
    : Module("Udon", "see, switch off and trigger the world's Udon scripts")
{
    // PUBLISH THE ANSWER AT THE ROOT, under "udon", and only once per answer: the client treats a
    // missing key as "nothing changed", so an idle sync must leave the page's table alone.
    Actions::RegisterState([](std::string& out, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        if (m->m_out.empty()) return;
        out += ",\"udon\":" + m->m_out;
        m->m_out.clear();
    }, this);

    Actions::Register("udonScan", [](std::string const&, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        m->Rescan();
        m->Answer("items", m->ItemsJson());
    }, this);

    Actions::Register("udonRestore", [](std::string const&, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        m->RestoreAll();
        m->Answer("items", m->ItemsJson());
    }, this);

    // "<instanceId>:1" -- switch one script on or off in THIS client.
    Actions::Register("udonToggle", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        size_t c = v.rfind(':');
        if (c == std::string::npos || c == 0) return;
        int id = std::atoi(v.substr(0, c).c_str());
        bool on = v.substr(c + 1) == "1";
        if (auto* e = m->ById(id)) m->SetBehaviourEnabled(*e, on);
        m->Answer("items", m->ItemsJson());
    }, this);

    // "1:id,id,id" -- the same switch over a selection, in one go.
    Actions::Register("udonMany", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        size_t c = v.find(':');
        if (c == std::string::npos || c == 0) return;
        bool on = v.substr(0, c) == "1";
        std::string rest = v.substr(c + 1);
        size_t start = 0;
        while (start <= rest.size()) {
            size_t comma = rest.find(',', start);
            std::string tok = rest.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
            if (!tok.empty())
                if (auto* e = m->ById(std::atoi(tok.c_str()))) m->SetBehaviourEnabled(*e, on);
            if (comma == std::string::npos) break;
            start = comma + 1;
        }
        m->Answer("items", m->ItemsJson());
    }, this);

    Actions::Register("udonInteract", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        if (auto* e = m->ById(std::atoi(v.c_str()))) m->Interact(*e);
        else {
            Log::Writef("Warning", "[Udon] interact: id %s not found.", v.c_str());
            ScreenUI::Toast("Udon: this object no longer exists");
        }
    }, this);

    Actions::Register("udonEvents", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        m->Answer("events", m->EventsJson(std::atoi(v.c_str())));
    }, this);

    Actions::Register("udonOwn", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        if (auto* e = m->ById(std::atoi(v.c_str()))) m->TakeOwnership(*e);
    }, this);

    // "<id>:<event>" -- run on THIS client only.
    Actions::Register("udonRun", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        size_t c = v.find(':');
        if (c == std::string::npos || c == 0) return;
        if (auto* e = m->ById(std::atoi(v.substr(0, c).c_str()))) m->RunLocal(*e, v.substr(c + 1));
    }, this);

    Actions::Register("udonVars", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        m->Answer("vars", m->VarsJson(std::atoi(v.c_str())));
    }, this);

    Actions::Register("udonEventsAll", [](std::string const&, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        m->Answer("eventsAll", m->AllEventsJson());
    }, this);

    // "<id>|<name>|<value>" -- write a variable. The Sync form pushes it to everyone afterwards.
    Actions::Register("udonSetVar", [](std::string const& v, void* u) {
        size_t p1 = v.find('|');
        if (p1 == std::string::npos || p1 == 0) return;
        size_t p2 = v.find('|', p1 + 1);
        if (p2 == std::string::npos) return;
        auto* m = static_cast<UdonModule*>(u);
        if (auto* e = m->ById(std::atoi(v.substr(0, p1).c_str())))
            m->SetVariable(*e, v.substr(p1 + 1, p2 - p1 - 1), v.substr(p2 + 1));
        else ScreenUI::Toast("Udon: this object no longer exists");
    }, this);
    Actions::Register("udonSetVarSync", [](std::string const& v, void* u) {
        size_t p1 = v.find('|');
        if (p1 == std::string::npos || p1 == 0) return;
        size_t p2 = v.find('|', p1 + 1);
        if (p2 == std::string::npos) return;
        auto* m = static_cast<UdonModule*>(u);
        if (auto* e = m->ById(std::atoi(v.substr(0, p1).c_str()))) {
            if (m->SetVariable(*e, v.substr(p1 + 1, p2 - p1 - 1), v.substr(p2 + 1)))
                m->RequestSerialization(*e);
        } else ScreenUI::Toast("Udon: this object no longer exists");
    }, this);

    // "<id>|<event>" -- meant for the object's OWNER. VRChat has no "send to the owner" primitive,
    // so ownership is TAKEN first and the event is then broadcast: it lands on us as owner, which
    // is what the client's button means.
    Actions::Register("udonRunOwner", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        size_t bar = v.find('|');
        if (bar == std::string::npos || bar == 0) return;
        if (auto* e = m->ById(std::atoi(v.substr(0, bar).c_str()))) {
            m->TakeOwnership(*e);
            m->RunGlobal(*e, v.substr(bar + 1));
        }
    }, this);

    // "<id>|<event>" -- run locally, then report what the variables became. The client diffs it.
    Actions::Register("udonRunDiff", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        size_t bar = v.find('|');
        if (bar == std::string::npos || bar == 0) return;
        int id = std::atoi(v.substr(0, bar).c_str());
        auto* e = m->ById(id);
        if (!e) { ScreenUI::Toast("Udon: this object no longer exists"); return; }
        // Snapshot FIRST: the diff is the point of this button.
        std::vector<Var> before = m->Variables(*e);
        std::string ev = v.substr(bar + 1);
        m->RunLocal(*e, ev);
        m->Answer("runDiff", m->DiffJson(id, ev, before));
    }, this);

    // "<uid>|<scope>|<match>|<event>|<by>|<byVar>|<pattern>". A client that has not been updated
    // sends SIX and lands in the old shape with no byVar -- which is right: the two geometric
    // modes never needed one. The pattern stays last because a world object's name may contain '|'.
    Actions::Register("udonPlayerEvent", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        std::vector<std::string> p;
        size_t start = 0;
        for (int i = 0; i < 6; ++i) {
            size_t bar = v.find('|', start);
            if (bar == std::string::npos) break;
            p.push_back(v.substr(start, bar - start));
            start = bar + 1;
        }
        p.push_back(v.substr(start));
        if (p.size() < 6) return;
        std::string byVar   = p.size() >= 7 ? p[5] : std::string();
        std::string pattern = p.size() >= 7 ? p[6] : p[5];
        m->RunOnPlayer(p[0], p[1], p[2], p[3], p[4], byVar, pattern);
    }, this);

    // "<scope>|<match>|<event>|<pattern>" -- the client's world presets.
    Actions::Register("udonRunMatch", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        std::vector<std::string> p;
        size_t start = 0;
        for (int i = 0; i < 3; ++i) {
            size_t bar = v.find('|', start);
            if (bar == std::string::npos) return;
            p.push_back(v.substr(start, bar - start));
            start = bar + 1;
        }
        p.push_back(v.substr(start));        // the pattern may itself contain a '|', so it is last
        m->RunMatching(p[0], p[1], p[2], p[3]);
    }, this);

    // "<id>:<event>" -- broadcast to the whole instance.
    Actions::Register("udonRunGlobal", [](std::string const& v, void* u) {
        auto* m = static_cast<UdonModule*>(u);
        size_t c = v.find(':');
        if (c == std::string::npos || c == 0) return;
        if (auto* e = m->ById(std::atoi(v.substr(0, c).c_str()))) m->RunGlobal(*e, v.substr(c + 1));
    }, this);
}

void UdonModule::OnDisable() { RestoreAll(); }

void UdonModule::Rescan() {
    void* api = Player::LocalApi();
    Player::Vec3 me = api ? Player::Position(api) : Player::Vec3{};

    // Entries we switched off keep their flag across a rescan, or restore would lose track of
    // which ones were ours and the world would be left half-broken.
    std::vector<Entry> fresh;
    for (void* b : AllUdon()) {
        if (!Unity::IsAlive(b)) continue;
        Entry e;
        e.behaviour = b;
        e.id        = InstanceIdOf(b);
        void* go    = Unity::GameObjectOf(b);
        void* tr    = go ? Unity::Transform(go) : nullptr;
        e.shortName = Unity::Name(go);
        e.path      = tr ? PathOf(tr) : e.shortName;
        float p[3];
        if (tr && Unity::GetPosition(tr, p)) {
            float dx = p[0] - me.x, dy = p[1] - me.y, dz = p[2] - me.z;
            e.dist = std::sqrt(dx * dx + dy * dy + dz * dz);
        }
        e.offByUs = m_ourOff.count(e.id) != 0;
        fresh.push_back(std::move(e));
    }
    std::sort(fresh.begin(), fresh.end(),
              [](Entry const& a, Entry const& b) { return a.dist < b.dist; });
    m_items.swap(fresh);
    m_status = std::to_string(m_items.size()) + " script(s)";
    Log::Writef("Info", "[Udon] %zu script(s) trouves, %zu coupes par nous.",
                m_items.size(), m_ourOff.size());

    // PROVE THE READS ONCE, ON A REAL SCRIPT.
    //
    // Entry points and variables are only ever asked for by the desktop client, so a chain that
    // does not resolve stays invisible until someone clicks a button and gets an empty list. One
    // probe on the nearest script says in the log whether both actually work on this build.
    if (!m_selfTested && !m_items.empty()) {
        m_selfTested = true;
        // Over several scripts, not one: plenty of Udon behaviours legitimately have no
        // variables at all, and judging the whole chain on the nearest one is how a working
        // reader gets reported as broken.
        size_t bestEvents = 0, bestVars = 0;
        std::string bestName;
        for (size_t i = 0; i < m_items.size() && i < 12; ++i) {
            Entry& e = m_items[i];
            if (!Unity::IsAlive(e.behaviour)) continue;
            size_t ev = EntryPoints(e).size();
            size_t vr = Variables(e).size();
            if (vr > bestVars || (bestName.empty() && ev)) {
                bestEvents = ev; bestVars = vr; bestName = e.shortName;
            }
            if (bestVars) break;
        }
        Log::Writef((bestEvents || bestVars) ? "Info" : "Warning",
                    "[Udon] check on '%s': %zu event(s), %zu variable(s).",
                    bestName.empty() ? "?" : bestName.c_str(), bestEvents, bestVars);
    }
}

UdonModule::Entry* UdonModule::ById(int id) {
    for (auto& e : m_items)
        if (e.id == id) return Unity::IsAlive(e.behaviour) ? &e : nullptr;
    return nullptr;   // the world changed underneath the client: the honest answer is "gone"
}

void UdonModule::SetBehaviourEnabled(Entry& e, bool on) {
    if (!Unity::IsAlive(e.behaviour)) return;
    Unity::SetBehaviourEnabled(e.behaviour, on);
    if (on) { m_ourOff.erase(e.id); e.offByUs = false; }
    else    { m_ourOff.insert(e.id); e.offByUs = true; }
    Log::Writef("Info", "[Udon] %s -> %s", e.shortName.c_str(), on ? "ON" : "OFF");
}

void UdonModule::RestoreAll() {
    int n = 0;
    for (auto& e : m_items) {
        if (!e.offByUs || !Unity::IsAlive(e.behaviour)) continue;
        Unity::SetBehaviourEnabled(e.behaviour, true);
        e.offByUs = false;
        ++n;
    }
    m_ourOff.clear();
    if (n) Log::Writef("Info", "[Udon] %d script(s) switched back on -- the world works again.", n);
}

void UdonModule::Interact(Entry& e) {
    static void* m = nullptr;
    static bool looked = false;
    if (!looked) { looked = true; if (void* k = Il2::FindClass(kUdon)) m = Il2::FindMethodIn(k, "Interact", 0); }
    if (!m || !Unity::IsAlive(e.behaviour)) {
        Log::Warn("[Udon] Interact unavailable on this build.");
        return;
    }
    Il2::Invoke(m, e.behaviour, nullptr);
    Log::Writef("Info", "[Udon] Interact on %s", e.path.c_str());
}

// RUNS THE EVENT ON THIS CLIENT ONLY. Nothing is transmitted and the world's networked state stays
// whatever its owner says it is -- which is what makes it a debugging tool: you see what a script
// does from your side, and leaving the instance undoes it.
bool UdonModule::RunLocal(Entry& e, std::string const& ev) {
    static void* m = nullptr;
    static bool looked = false;
    if (!looked) { looked = true; if (void* k = Il2::FindClass(kUdon)) m = Il2::FindMethodIn(k, "SendCustomEvent", 1); }
    if (!m || ev.empty() || !Unity::IsAlive(e.behaviour)) return false;
    void* s = NewString(ev);
    if (!s) return false;
    void* a[1] = { s };
    Il2::Invoke(m, e.behaviour, a);
    Log::Writef("Info", "[Udon] local event '%s' on %s", ev.c_str(), e.path.c_str());
    return true;
}

// RUNS IT FOR EVERYONE, over VRChat's own networking -- the same path a world's own scripts use.
//
// VRChat REFUSES this for '_'-prefixed events: the Udon lifecycle hooks and anything the author
// prefixed can only run on the client that owns them. That refusal is enforced here too rather
// than being left to fail silently in the game.
bool UdonModule::RunGlobal(Entry& e, std::string const& ev) {
    if (ev.empty() || ev[0] == '_') {
        Log::Writef("Warning", "[Udon] '%s' starts with '_': VRChat forbids broadcasting it.",
                    ev.c_str());
        ScreenUI::Toast("VRChat forbids broadcasting a '_' event");
        return false;
    }
    static void* m = nullptr;
    static void* targetClass = nullptr;
    static bool looked = false;
    if (!looked) {
        looked = true;
        if (void* k = Il2::FindClass(kUdon)) m = Il2::FindMethodIn(k, "SendCustomNetworkEvent", 2);
        if (m) targetClass = Il2::MethodParamClass(m, 0);
    }
    if (!m || !Unity::IsAlive(e.behaviour)) {
        Log::Warn("[Udon] SendCustomNetworkEvent unavailable on this build.");
        return false;
    }
    // NetworkEventTarget is an enum, so the argument is its underlying int by value. All = 0.
    (void)targetClass;
    int all = 0;
    void* s = NewString(ev);
    if (!s) return false;
    void* a[2] = { &all, s };
    Il2::Invoke(m, e.behaviour, a);
    Log::Writef("Info", "[Udon] GLOBAL event '%s' on %s", ev.c_str(), e.path.c_str());
    return true;
}

void UdonModule::TakeOwnership(Entry& e) {
    static void* setOwner = nullptr;
    static bool looked = false;
    if (!looked) {
        looked = true;
        if (void* k = Il2::FindClass("VRC.SDKBase.Networking"))
            setOwner = Il2::FindMethodIn(k, "SetOwner", 2);
    }
    if (!setOwner || !Unity::IsAlive(e.behaviour)) {
        Log::Warn("[Udon] Networking.SetOwner unavailable on this build.");
        return;
    }
    void* api = Player::LocalApi();
    void* go  = Unity::GameObjectOf(e.behaviour);
    if (!api || !go) return;
    void* a[2] = { api, go };
    Il2::Invoke(setOwner, nullptr, a);
    Log::Writef("Info", "[Udon] ownership taken on %s", e.path.c_str());
}

std::vector<std::string> UdonModule::EntryPoints(Entry& e) {
    std::vector<std::string> out;
    static void* getPrograms = nullptr;
    static bool looked = false;
    if (!looked) { looked = true; if (void* k = Il2::FindClass(kUdon)) getPrograms = Il2::FindMethodIn(k, "GetPrograms", 0); }
    if (!getPrograms || !Unity::IsAlive(e.behaviour)) return out;

    void* real = UnwrapArray(Il2::Invoke(getPrograms, e.behaviour, nullptr));
    if (!real) return out;
    int n = Il2::ArrayLength(real);
    for (int i = 0; i < n && i < 120; ++i) {
        std::string s = Il2::ReadString(Il2::ArrayAt(real, i));
        if (!s.empty()) out.push_back(std::move(s));
    }
    return out;
}

// THE SYMBOL TABLE, the only safe way to list a script's variables.
//
// UdonBehaviour -> its IUdonProgram -> SymbolTable -> GetSymbols() hands back a plain string[],
// which can be indexed. The public-variable table's VariableSymbols is a Dictionary.KeyCollection,
// and walking THAT through an enumerator is what took the game down in the C# mod. An array
// cannot do that.
//
// The VRC.Udon.* names survive obfuscation (the class lookup for UdonBehaviour proves it), so the
// chain is resolved by name; only the member HOLDING the program is found by RETURN TYPE, because
// that one is renamed.
std::vector<UdonModule::Var> UdonModule::Variables(Entry& e) {
    std::vector<Var> out;
    if (!Unity::IsAlive(e.behaviour)) return out;

    // THE PROGRAM IS A FIELD, NOT A GETTER.
    //
    // UdonBehaviour's member names survive obfuscation on this build, and the dump settled it:
    // there is no 0-arg method returning a program at all -- `_program` is a FIELD of type
    // IUdonProgram at a fixed offset. Looking for a getter found nothing and reported the whole
    // variable reader as broken. It is still located by TYPE rather than by name, so a later
    // build that renames the field keeps working.
    static void* getSymbols  = nullptr;
    static void* getVar      = nullptr;
    static int   programOff  = -1;
    static void* getTable    = nullptr;
    static bool  looked = false;
    if (!looked) {
        looked = true;
        void* ub = Il2::FindClass(kUdon);
        if (ub) {
            getVar = Il2::FindMethodIn(ub, "GetProgramVariable", 1);
            void* fit = nullptr;
            while (void* f = Il2::NextField(ub, &fit)) {
                if (Il2::FieldIsStatic(f)) continue;
                void* fc = Il2::FieldClass(f);
                char const* fn = fc ? Il2::ClassName(fc) : nullptr;
                if (!fn || !std::strstr(fn, "UdonProgram")) continue;
                if (std::strstr(fn, "Source") || std::strstr(fn, "Asset")) continue;  // the ASSET, not the program
                programOff = Il2::FieldOffsetOf(f);
                break;
            }
            Log::Writef("Info", "[Udon] programme : champ IUdonProgram @%d, GetProgramVariable=%s",
                        programOff, getVar ? "ok" : "NON");
        }
    }
    if (programOff <= 0 || !getVar) { Step("IUdonProgram field not found on UdonBehaviour"); return out; }

    void* program = *reinterpret_cast<void**>(static_cast<char*>(e.behaviour) + programOff);
    if (!program) { Step("this script has not loaded its program yet"); return out; }

    if (!getTable) {
        for (void* k = Il2::ClassOfObject(program); k; k = Il2::ClassParent(k))
            if ((getTable = Il2::FindMethodIn(k, "get_SymbolTable", 0)) != nullptr) break;
    }
    if (!getTable) { Step("get_SymbolTable missing from the program"); return out; }
    void* table = Il2::Invoke(getTable, program, nullptr);
    if (!table) { Step("the program has no symbol table"); return out; }

    if (!getSymbols) {
        for (void* k = Il2::ClassOfObject(table); k; k = Il2::ClassParent(k)) {
            getSymbols = Il2::FindMethodIn(k, "GetSymbols", 0);
            if (!getSymbols) getSymbols = Il2::FindMethodIn(k, "GetExportedSymbols", 0);
            if (getSymbols) break;
        }
    }
    if (!getSymbols) { Step("GetSymbols missing from the table"); return out; }

    void* arr = UnwrapArray(Il2::Invoke(getSymbols, table, nullptr));
    int n = Il2::ArrayLength(arr);
    if (n <= 0) Step("empty symbol table");
    for (int i = 0; i < n && i < 400; ++i) {
        std::string name = Il2::ReadString(Il2::ArrayAt(arr, i));
        if (name.empty()) continue;
        Var v;
        v.name = name;
        void* ns = NewString(name);
        void* a[1] = { ns };
        void* val = ns ? Il2::Invoke(getVar, e.behaviour, a) : nullptr;
        if (val) {
            void* vk = Il2::ClassOfObject(val);
            char const* tn = vk ? Il2::ClassName(vk) : nullptr;
            v.type = tn ? tn : "";
            // A boxed primitive carries its payload at 0x10. Anything else is shown by its TYPE:
            // calling ToString() on an arbitrary world object is not worth the risk.
            if (v.type == "String")       v.value = Il2::ReadString(val);
            else if (v.type == "Boolean") v.value = *reinterpret_cast<unsigned char*>(static_cast<char*>(val) + 0x10) ? "True" : "False";
            else if (v.type == "Int32")   v.value = std::to_string(*reinterpret_cast<int*>(static_cast<char*>(val) + 0x10));
            else if (v.type == "Single") {
                char b[32];
                std::snprintf(b, sizeof b, "%.4g",
                              static_cast<double>(*reinterpret_cast<float*>(static_cast<char*>(val) + 0x10)));
                for (char& ch : b) if (ch == ',') ch = '.';
                v.value = b;
            } else {
                v.value = "<" + v.type + ">";
            }
        }
        out.push_back(std::move(v));
    }
    return out;
}

// WRITING A VARIABLE. The value arrives as text, so it is boxed to the type the variable ALREADY
// holds: writing a string into an int variable is how a world's logic gets corrupted rather than
// driven. A variable with no current value cannot be typed, so it is refused out loud.
bool UdonModule::SetVariable(Entry& e, std::string const& name, std::string const& value) {
    if (!Unity::IsAlive(e.behaviour) || name.empty()) return false;
    static void* setVar = nullptr;
    static void* getVar = nullptr;
    static bool looked = false;
    if (!looked) {
        looked = true;
        if (void* ub = Il2::FindClass(kUdon)) {
            setVar = Il2::FindMethodIn(ub, "SetProgramVariable", 2);
            getVar = Il2::FindMethodIn(ub, "GetProgramVariable", 1);
        }
    }
    if (!setVar || !getVar) {
        Log::Warn("[Udon] SetProgramVariable unavailable on this build.");
        return false;
    }

    void* ns = NewString(name);
    if (!ns) return false;
    void* ga[1] = { ns };
    void* current = Il2::Invoke(getVar, e.behaviour, ga);
    if (!current) {
        Log::Writef("Warning", "[Udon] '%s' has no current value -- unknown type, write refused.",
                    name.c_str());
        ScreenUI::Toast("Udon: unknown type for this variable");
        return false;
    }
    void* vk = Il2::ClassOfObject(current);
    char const* tn = vk ? Il2::ClassName(vk) : "";

    void* boxed = nullptr;
    if (std::strcmp(tn, "String") == 0) {
        boxed = NewString(value);
    } else {
        static void* newObj = Il2::Export("il2cpp_object_new");
        using NO = void*(*)(void*);
        boxed = newObj ? reinterpret_cast<NO>(newObj)(vk) : nullptr;
        if (!boxed) return false;
        char* pay = static_cast<char*>(boxed) + 0x10;
        if (std::strcmp(tn, "Boolean") == 0)
            *reinterpret_cast<unsigned char*>(pay) =
                (value == "1" || value == "true" || value == "True") ? 1 : 0;
        else if (std::strcmp(tn, "Int32") == 0)
            *reinterpret_cast<int*>(pay) = std::atoi(value.c_str());
        else if (std::strcmp(tn, "Single") == 0) {
            std::string v = value;
            for (char& ch : v) if (ch == ',') ch = '.';
            *reinterpret_cast<float*>(pay) = static_cast<float>(std::atof(v.c_str()));
        } else {
            Log::Writef("Warning", "[Udon] type '%s' not writable from the client.", tn);
            ScreenUI::Toast("Udon: this type is not writable");
            return false;
        }
    }

    void* sa[2] = { ns, boxed };
    Il2::Invoke(setVar, e.behaviour, sa);
    Log::Writef("Info", "[Udon] %s.%s = %s", e.shortName.c_str(), name.c_str(), value.c_str());
    return true;
}

// A Manual-sync script never pushes on its own: this is what makes a written value reach the other
// clients instead of staying local.
bool UdonModule::RequestSerialization(Entry& e) {
    static void* m = nullptr;
    static bool looked = false;
    if (!looked) { looked = true; if (void* k = Il2::FindClass(kUdon)) m = Il2::FindMethodIn(k, "RequestSerialization", 0); }
    if (!m || !Unity::IsAlive(e.behaviour)) {
        Log::Warn("[Udon] RequestSerialization unavailable on this build.");
        return false;
    }
    Il2::Invoke(m, e.behaviour, nullptr);
    return true;
}

// ONE EVENT ON EVERY SCRIPT WHOSE OBJECT NAME MATCHES -- the engine behind the client's world
// presets. "Break Doors" is one event on every object called Door*.
//
// IT NEVER INVENTS A NAME: the pattern is matched against the names the LAST SCAN actually found,
// so a preset whose object is not in this world simply hits nothing and says so, and can never
// fire the wrong event at the wrong object.
//
// PACED, NOT BURSTED. A preset in a 200-door world is 200 network events, and firing them in one
// frame is the burst that disconnects you. They are queued and fired a few per second.
void UdonModule::RunMatching(std::string const& scope, std::string const& match,
                             std::string const& ev, std::string const& pattern) {
    bool global = (scope == "global");
    auto hit = [&](std::string const& name) {
        if (pattern.empty()) return false;
        if (match == "exact")  return name == pattern;
        if (match == "prefix") return name.rfind(pattern, 0) == 0;
        if (match == "suffix") return name.size() >= pattern.size() &&
                                      name.compare(name.size() - pattern.size(), pattern.size(), pattern) == 0;
        return name.find(pattern) != std::string::npos;   // "contains", the default
    };

    m_pending.clear();
    for (auto const& e : m_items)
        if (Unity::IsAlive(e.behaviour) && hit(e.shortName)) m_pending.push_back(e.id);

    if (m_pending.empty()) {
        Log::Writef("Warning", "[Udon] no object matches '%s' -- nothing sent.", pattern.c_str());
        ScreenUI::Toast("Udon: no object '" + pattern + "' in this world");
        return;
    }
    m_pendingEvent  = ev;
    m_pendingGlobal = global;
    m_pendingTotal  = static_cast<int>(m_pending.size());
    m_pendingDone   = 0;
    Log::Writef("Info", "[Udon] '%s' on %d object(s) matching '%s' (%s), paced.",
                ev.c_str(), m_pendingTotal, pattern.c_str(), global ? "global" : "local");
}

bool UdonModule::RunOnPlayer(std::string const& userId, std::string const& scope,
                             std::string const& match, std::string const& ev,
                             std::string const& by, std::string const& byVar,
                             std::string const& pattern) {
    Player::Info const* who = nullptr;
    auto roster = Player::All();
    for (auto const& p : roster) if (p.userId == userId) { who = &p; break; }
    if (!who) {
        m_status = "this player is no longer in the instance";
        ScreenUI::Toast("Udon: this player has left");
        return false;
    }

    auto fire = [&](Entry& e) {
        if (scope == "interact")   { Interact(e); return true; }
        if (scope == "local")      return RunLocal(e, ev);
        if (scope == "owner")      { TakeOwnership(e); return RunGlobal(e, ev); }
        if (scope == "global")     return RunGlobal(e, ev);
        m_status = "unknown scope '" + scope + "'";
        return false;
    };

    auto nameHit = [&](std::string const& name) {
        if (pattern.empty()) return true;                 // no pattern: every script is a candidate
        if (match == "exact")  return name == pattern;
        if (match == "prefix") return name.rfind(pattern, 0) == 0;
        if (match == "suffix") return name.size() >= pattern.size() &&
                                      name.compare(name.size() - pattern.size(), pattern.size(), pattern) == 0;
        return name.find(pattern) != std::string::npos;
    };

    // BY VARIABLE -- ASK THE OBJECT, DO NOT MEASURE IT.
    //
    // A world that keeps thirty per-player nodes in one folder has them all at effectively one
    // place, each naming its person in a VARIABLE. "Nearest node" there resolves to an arbitrary
    // one, so an event aimed at somebody would hit somebody else AND report success. Either a
    // candidate's variable holds this player's id or none does; if none does, the honest answer
    // is that the world has no node for them right now.
    if (by == "var") {
        if (byVar.empty()) {
            m_status = "'var' mode with no variable name";
            ScreenUI::Toast("Udon: the variable name is missing");
            return false;
        }
        std::string wantId = std::to_string(who->id);
        for (auto& e : m_items) {
            if (!Unity::IsAlive(e.behaviour) || !nameHit(e.shortName)) continue;
            for (auto const& v : Variables(e)) {
                if (v.name != byVar) continue;
                if (v.value != wantId) break;
                bool ok = fire(e);
                Log::Writef("Info", "[Udon] player event %s '%s' -> %s via %s (%s=%s)",
                            scope.c_str(), ev.c_str(), who->name.c_str(), e.shortName.c_str(),
                            byVar.c_str(), v.value.c_str());
                return ok;
            }
        }
        m_status = "no object whose " + byVar + " vaut " + wantId;
        ScreenUI::Toast("Udon: no object for this player");
        return false;
    }

    // GEOMETRIC. A player still resolving after a join reports no position, and aiming at the
    // ORIGIN would hit whatever sits there -- another player's node, a shared manager -- and call
    // it a success. Refused instead.
    if (!who->hasPos) {
        m_status = who->name + " has no position yet";
        ScreenUI::Toast("Udon: player position not known yet");
        return false;
    }
    float tx = who->pos.x, ty = who->pos.y, tz = who->pos.z;

    Entry* best = nullptr;
    float bestD = 1e30f;
    for (auto& e : m_items) {
        if (!Unity::IsAlive(e.behaviour) || !nameHit(e.shortName)) continue;
        void* go = Unity::GameObjectOf(e.behaviour);
        void* tr = go ? Unity::Transform(go) : nullptr;
        float p[3];
        if (!tr || !Unity::GetPosition(tr, p)) continue;
        float dx = p[0] - tx, dy = p[1] - ty, dz = p[2] - tz;
        float d = std::sqrt(dx * dx + dy * dy + dz * dz);
        if (d < bestD) { bestD = d; best = &e; }
    }

    // A DISTANCE FLOOR. "Nearest" with no limit means "the one and only such object, wherever it
    // is": aiming at two different players would hit the same shared node and call both a
    // success. Past this radius the object is not theirs.
    constexpr float kMaxReach = 30.f;
    if (!best || bestD > kMaxReach) {
        m_status = best ? "the nearest object is at " + std::to_string(static_cast<int>(bestD))
                              + " m de " + who->name + " -- not theirs, nothing sent"
                        : "no script in this world near " + who->name;
        Log::Writef("Warning", "[Udon] %s", m_status.c_str());
        ScreenUI::Toast("Udon: no object close enough to this player");
        return false;
    }

    bool ok = fire(*best);
    Log::Writef("Info", "[Udon] player event %s '%s' -> %s via %s (%.1f m)",
                scope.c_str(), ev.c_str(), who->name.c_str(), best->shortName.c_str(),
                static_cast<double>(bestD));
    return ok;
}

void UdonModule::PumpPending() {
    if (m_pending.empty()) return;
    double now = Engine::Time();
    if (now < m_nextFire) return;
    m_nextFire = now + 0.08;          // ~12 a second: fast to watch, far under the burst that drops you

    int id = m_pending.front();
    m_pending.erase(m_pending.begin());
    ++m_pendingDone;
    if (Entry* e = ById(id)) {
        if (m_pendingGlobal) RunGlobal(*e, m_pendingEvent);
        else                 RunLocal(*e, m_pendingEvent);
    }
    if (m_pending.empty())
        Log::Writef("Info", "[Udon] preset termine : %d/%d objet(s).", m_pendingDone, m_pendingTotal);
}

// THE FIELD NAMES ARE THE CLIENT'S, NOT OURS. Each row is read as {id, n, p, d, on, ours, o, m}
// and each variable as {n, t, v, e, s}; anything else arrives as an empty cell, which is exactly
// how a working scan came out as a blank table.
std::string UdonModule::VarsJson(int id) {
    Entry* e = ById(id);
    if (!e) return "{\"id\":" + std::to_string(id) + ",\"canWrite\":false,\"list\":[]}";
    std::string out = "{\"id\":" + std::to_string(id) + ",\"canWrite\":true,\"list\":[";
    bool first = true;
    for (auto const& v : Variables(*e)) {
        if (!first) out += ',';
        first = false;
        // e = the mod will accept a write for it; s = synced, which this build cannot tell apart
        // from a local variable, so it is reported as unknown rather than guessed at.
        out += "{\"n\":" + J(v.name) + ",\"v\":" + J(v.value) + ",\"t\":" + J(v.type)
             + ",\"e\":true,\"s\":false}";
    }
    return out + "]}";
}

// Every script's entry points in one answer -- the client builds its world presets from this.
// GLOBAL ones go in "g", local ones in "l": VRChat refuses to network an event whose name starts
// with '_', so that prefix IS the boundary.
std::string UdonModule::AllEventsJson() {
    std::string out = "[";
    bool first = true;
    for (auto& e : m_items) {
        if (!Unity::IsAlive(e.behaviour)) continue;
        auto evs = EntryPoints(e);
        if (evs.empty()) continue;
        if (!first) out += ',';
        first = false;
        std::string g, l;
        for (auto const& ev : evs) {
            bool global = !ev.empty() && ev[0] != '_';
            std::string& dst = global ? g : l;
            if (!dst.empty()) dst += ',';
            dst += J(ev);
        }
        out += "{\"id\":" + std::to_string(e.id) + ",\"g\":[" + g + "],\"l\":[" + l + "]}";
    }
    return out + "]";
}

std::string UdonModule::ItemsJson() {
    std::string out = "[";
    bool first = true;
    for (auto& e : m_items) {
        if (!Unity::IsAlive(e.behaviour)) continue;
        if (!first) out += ',';
        first = false;
        char d[32];
        std::snprintf(d, sizeof d, "%.1f", static_cast<double>(e.dist));
        for (char& c : d) if (c == ',') c = '.';
        out += "{\"id\":" + std::to_string(e.id)
             + ",\"n\":" + J(e.shortName)
             + ",\"p\":" + J(e.path)
             + ",\"d\":" + std::string(d)
             + ",\"on\":" + (Unity::BehaviourEnabled(e.behaviour) ? "true" : "false")
             // "ours" is the client's word for "this one is off because WE switched it off", which
             // is what tells Restore from a script the world turned off by itself.
             + ",\"ours\":" + (e.offByUs ? "true" : "false")
             + ",\"o\":\"\",\"m\":false}";
    }
    return out + "]";
}

std::string UdonModule::EventsJson(int id) {
    Entry* e = ById(id);
    if (!e) return "{\"id\":" + std::to_string(id) + ",\"list\":[]}";
    std::string out = "{\"id\":" + std::to_string(id) + ",\"list\":[";
    bool first = true;
    for (auto const& ev : EntryPoints(*e)) {
        if (!first) out += ',';
        first = false;
        out += "{\"n\":" + J(ev) + ",\"g\":" + (ev.empty() || ev[0] == '_' ? "false" : "true") + "}";
    }
    return out + "]}";
}

// WHAT THE EVENT ACTUALLY CHANGED. Read the variables before, run, read them after, and report
// only the ones whose value moved -- a full dump of a hundred variables says nothing about which
// one the event touched, which is the whole question the page is asking.
std::string UdonModule::DiffJson(int id, std::string const& ev, std::vector<Var> const& before) {
    std::string out = "{\"id\":" + std::to_string(id) + ",\"event\":" + J(ev) + ",\"changes\":[";
    Entry* e = ById(id);
    if (e) {
        bool first = true;
        for (auto const& a : Variables(*e)) {
            std::string was;
            bool known = false;
            for (auto const& b : before)
                if (b.name == a.name) { was = b.value; known = true; break; }
            if (known && was == a.value) continue;
            if (!first) out += ',';
            first = false;
            out += "{\"n\":" + J(a.name) + ",\"b\":" + J(known ? was : std::string("(nouvelle)"))
                 + ",\"a\":" + J(a.value) + "}";
        }
    }
    return out + "]}";
}

// THE HEADER EVERY ANSWER CARRIES, so the page can state what it is looking at even when the one
// block it asked for is empty.
void UdonModule::Answer(char const* key, std::string const& body) {
    int total = 0, off = 0;
    for (auto& e : m_items) {
        if (!Unity::IsAlive(e.behaviour)) continue;
        ++total;
        if (!Unity::BehaviourEnabled(e.behaviour)) ++off;
    }
    std::string out = "{\"status\":" + J(m_status)
                    + ",\"worldId\":" + J(World::Id())
                    + ",\"worldName\":" + J(World::Name())
                    + ",\"total\":" + std::to_string(total)
                    + ",\"shown\":" + std::to_string(total)
                    + ",\"off\":" + std::to_string(off);
    if (key && *key && !body.empty()) out += ",\"" + std::string(key) + "\":" + body;
    m_out = out + "}";
}

void UdonModule::OnUpdate() {
    PumpPending();     // a matched preset fires a few objects a second, never all at once

    // A slow rescan keeps the client's table honest as the world streams objects in and out,
    // without paying a Resources sweep every frame.
    double now = Engine::Time();
    if (now < m_nextScan) return;
    m_nextScan = now + 15.0;
    Rescan();
}

}
