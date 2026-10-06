#include "bridge.hpp"
#include "obf.hpp"
#include "vatags.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "feed.hpp"
#include "http.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "player.hpp"
#include "screenui.hpp"
#include "wings.hpp"
#include "world.hpp"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <unordered_set>
#include <vector>

namespace VRCA {

namespace {
    // One JSON string escaper for the whole bridge: the action registry answers the client too,
    // so it cannot live below the sync builder.
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
                    if (c < 0x20) { char b[8]; std::snprintf(b, sizeof(b), "\\u%04x", c); o += b; }
                    else o += static_cast<char>(c);
            }
        }
        return o + "\"";
    }
}

namespace Json {
    std::string Str(std::string const& s) { return J(s); }
    std::string Bool(bool b) { return b ? "true" : "false"; }
    // NUMBERS GO OUT WITH A DOT, whatever the machine's locale says; the client parses invariant.
    std::string Num(double v) {
        char b[32];
        std::snprintf(b, sizeof(b), "%.4g", v);
        for (char* p = b; *p; ++p) if (*p == ',') *p = '.';
        return b;
    }
    std::string Int(long long v) { return std::to_string(v); }
}

namespace Actions {
namespace {
    struct Slot { std::string id; Handler h; void* user; };
    std::vector<Slot>& Table() { static std::vector<Slot> t; return t; }
    struct StateSlot { StateFn fn; void* user; };
    std::vector<StateSlot>& StateTable() { static std::vector<StateSlot> t; return t; }
}

void RegisterState(StateFn fn, void* user) {
    if (!fn) return;
    for (auto& s : StateTable()) if (s.fn == fn && s.user == user) return;   // idempotent
    StateTable().push_back({ fn, user });
}

// Game thread, inside BuildSync. One contributor that throws must not cost the whole sync -- the
// client would read EVERY other feature as absent, i.e. off, which is the exact failure this
// channel exists to end.
void EmitState(std::string& out) {
    for (auto const& s : StateTable()) {
        try { s.fn(out, s.user); }
        catch (...) {}
    }
}

void Register(char const* id, Handler h, void* user) {
    if (!id || !h) return;
    for (auto& s : Table())
        if (s.id == id) { s.h = h; s.user = user; return; }
    Table().push_back({ id, h, user });
}

bool Dispatch(std::string const& id, std::string const& value) {
    for (auto const& s : Table())
        if (s.id == id) { s.h(value, s.user); return true; }
    return false;
}

namespace {
    struct Result { std::string kind, id, text; };
    std::vector<Result> g_results;
    std::mutex          g_resultsLock;
}

void PostResult(char const* kind, std::string const& id, std::string const& text) {
    std::lock_guard lock(g_resultsLock);
    // A pane can only read so fast, and an unbounded queue is how a chatty feature turns into a
    // memory leak the user never sees.
    if (g_results.size() >= 64) g_results.erase(g_results.begin());
    g_results.push_back({ kind ? kind : "text", id, text });
}

std::string DrainResults() {
    std::lock_guard lock(g_resultsLock);
    if (g_results.empty()) return {};
    std::string out;
    for (auto const& r : g_results) {
        if (!out.empty()) out += ',';
        // THE FIELD IS "json", NOT "text". The client reads Str(e, "json") out of every result,
        // so a result sent under any other name arrives as an empty string -- which is how the
        // avatar dump, the UI dump and "who blocked me" all came back blank while the log said
        // the answer had been posted.
        out += "{\"kind\":" + J(r.kind) + ",\"id\":" + J(r.id) + ",\"json\":" + J(r.text) + "}";
    }
    g_results.clear();
    return out;
}
}

namespace {
    constexpr char const* kModVersion = "vrchatarchive-cpp-3.0";
    // The client holds /mod/sync open until it has something to say, so the timeout must outlast
    // that hold; anything shorter turns a healthy idle poll into a stream of timeouts.
    constexpr int kSyncTimeoutMs = 20000;


    // NUMBERS GO OUT WITH A DOT. The client parses invariant and compares the text it gets back
    // against the text it asked for; "1,5" never matches, so it resends until it gives up.
    std::string Num(float v) {
        char b[32];
        std::snprintf(b, sizeof(b), "%.4g", static_cast<double>(v));
        for (char* p = b; *p; ++p) if (*p == ',') *p = '.';
        return b;
    }
    std::string B(bool v) { return v ? "true" : "false"; }

    // A reader for the one reply shape this channel has.
    struct Reader {
        std::string const& s; size_t i = 0;
        explicit Reader(std::string const& src) : s(src) {}
        void Skip() { while (i < s.size() && (s[i]==' '||s[i]=='\t'||s[i]=='\n'||s[i]=='\r')) ++i; }
        bool Eat(char c)  { Skip(); if (i < s.size() && s[i]==c) { ++i; return true; } return false; }
        bool Peek(char c) { Skip(); return i < s.size() && s[i]==c; }
        bool Str(std::string& out) {
            Skip();
            if (i >= s.size() || s[i] != '"') return false;
            ++i; out.clear();
            while (i < s.size() && s[i] != '"') {
                if (s[i] == '\\' && i + 1 < s.size()) {
                    ++i;
                    switch (s[i]) {
                        case 'n': out += '\n'; break;
                        case 'r': out += '\r'; break;
                        case 't': out += '\t'; break;
                        case 'u': if (i + 4 < s.size()) { i += 4; out += '?'; } break;
                        default:  out += s[i];
                    }
                } else out += s[i];
                ++i;
            }
            if (i >= s.size()) return false;
            ++i; return true;
        }
        void SkipValue() {
            Skip();
            if (i >= s.size()) return;
            if (s[i] == '"') { std::string t; Str(t); return; }
            if (s[i] == '{' || s[i] == '[') {
                char open = s[i], close = (open=='{') ? '}' : ']';
                int depth = 0;
                while (i < s.size()) {
                    if (s[i] == '"') { std::string t; Str(t); continue; }
                    if (s[i] == open) ++depth;
                    else if (s[i] == close) { --depth; if (!depth) { ++i; return; } }
                    ++i;
                }
                return;
            }
            while (i < s.size() && s[i] != ',' && s[i] != '}' && s[i] != ']') ++i;
        }
        bool Number(long long& out) {
            Skip();
            size_t st = i;
            while (i < s.size() && (std::isdigit(static_cast<unsigned char>(s[i])) || s[i]=='-')) ++i;
            if (i == st) return false;
            out = 0; bool neg = false;
            for (size_t k = st; k < i; ++k) { if (s[k]=='-') { neg = true; continue; } out = out*10 + (s[k]-'0'); }
            if (neg) out = -out;
            return true;
        }
    };
}

BridgeModule::BridgeModule()
    : Module("Client Bridge", "the channel the desktop app uses to drive the mod")
{
    // On by default: without it the client's pages see nothing, and a bridge nobody thought to
    // switch on looks exactly like a broken one.
    m_enabled = true;
    // The worker is NOT started here: this ctor runs during registration, and the worker enumerates
    // the module list to build the schema -- reading it while registration is still pushing into it
    // is a data race that kills the worker silently. It starts on the first OnUpdate.
}

BridgeModule::~BridgeModule() {
    m_stopping.store(true);
    if (m_worker.joinable()) m_worker.join();
}

std::string BridgeModule::BuildSchema() const {
    std::string out = "{\"schema\":[";
    bool first = true;

    // Every module as an on/off switch under "Modules"...
    for (Module* m : Engine::Modules()) {
        if (!first) out += ',';
        first = false;
        out += "{\"section\":\"Modules\",\"key\":" + J(std::string(m->Name()))
             + ",\"type\":\"Boolean\",\"desc\":" + J(std::string(m->Desc()))
             + ",\"range\":\"\"}";
    }

    // ...then everything the modules themselves registered. This is what lets the client draw a
    // real control panel -- sliders with the mod's own bounds, per-channel switches -- instead of
    // a flat list of module toggles. The bridge names none of them.
    for (auto const& e : Config::All()) {
        if (!first) out += ',';
        first = false;
        out += "{\"section\":" + J(e.section) + ",\"key\":" + J(e.key)
             + ",\"type\":" + J(e.type) + ",\"desc\":" + J(e.desc)
             + ",\"range\":" + J(e.range) + "}";
    }
    return out + "]}";
}

std::string BridgeModule::BuildSync() {
    std::string out = "{\"modVersion\":" + J(kModVersion);

    out += ",\"values\":{";
    bool first = true;
    for (Module* m : Engine::Modules()) {
        if (!first) out += ',';
        first = false;
        out += J("Modules/" + std::string(m->Name())) + ":" + J(m->Enabled() ? "True" : "False");
    }
    for (auto const& e : Config::All()) {
        if (!first) out += ',';
        first = false;
        out += J(e.section + "/" + e.key) + ":" + J(Config::ValueOf(e));
    }
    out += "}";

    // NOTE (open-source build): the plaintext-cache decryptor and the Dex auto-unlock are
    // NOT part of this repository. They are server-gated paid features, delivered by
    // injection in the private build, so their sync fields are omitted here.

    out += ",\"roster\":[";
    first = true;
    for (auto const& p : Player::All()) {
        if (!first) out += ',';
        first = false;
        out += "{\"name\":" + J(p.name)
             + ",\"userId\":" + J(p.userId)
             + ",\"avatarId\":" + J(p.avatarId)
             + ",\"avatarName\":" + J(p.avatarName)
             + ",\"release\":" + J(p.release)
             + ",\"platform\":" + J(p.platform)
             + ",\"trust\":" + J(p.trust)
             + ",\"plus\":" + B(p.plus)
             + ",\"playerId\":" + std::to_string(p.id)
             + ",\"isLocal\":" + B(p.isLocal)
             + ",\"isMaster\":" + B(p.isMaster)
             + ",\"isOwner\":" + B(p.isOwner)
             + ",\"inVR\":" + B(p.inVR) + ",\"vrKnown\":true"
             + ",\"hasPos\":" + B(p.hasPos)
             + ",\"px\":" + Num(p.pos.x) + ",\"py\":" + Num(p.pos.y) + ",\"pz\":" + Num(p.pos.z)
             + ",\"tags\":" + VaTagsModule::ForUser(p.userId)
             + "}";
    }
    // PROVE THE ROSTER ONCE, INCLUDING THE LOCAL PLAYER.
    //
    // Force Clone and every other player list skips a row with no NAME, so a roster of nameless
    // entries reads as "nobody is here" rather than as a broken getter -- which is exactly how
    // this went unnoticed. The local player always exists, so the check does not need company.
    {
        static bool said = false;
        if (!said) {
            int named = 0, withId = 0, total = 0;
            std::string sample;
            for (auto const& p : Player::All()) {
                ++total;
                if (!p.name.empty()) ++named;
                if (p.avatarId.rfind("avtr_", 0) == 0) ++withId;
                if (sample.empty() && !p.name.empty())
                    sample = p.name + (p.isLocal ? " (toi)" : "");
            }
            if (total > 0) {
                said = true;
                Log::Writef(named == total ? "Info" : "Warning",
                            "[Bridge] roster: %d/%d named, %d with an avtr_ id. First: '%s'.",
                            named, total, withId, sample.c_str());
            }
        }
    }

    out += "]";            // CLOSE THE ROSTER HERE. Everything below is a sibling field, and
                           // leaving the array open put "results" and the world INSIDE it --
                           // invalid JSON, which the client answered with HTTP 400.
    {
        std::string extra = Actions::DrainResults();
        std::lock_guard lock(m_payloadLock);
        std::string body;
        if (!m_pendingDump.empty()) {
            body = "{\"kind\":\"user\",\"json\":" + J(m_pendingDump) + "}";
            m_pendingDump.clear();
        }
        if (!extra.empty()) { if (!body.empty()) body += ','; body += extra; }
        if (!body.empty()) out += ",\"results\":[" + body + "]";
    }

    // THE WING SELECTION. A row clicked on the in-game panel selects that player on the
    // client's PLAYERS page; the sequence climbs per click so the client acts once per new one.
    {
        auto sel = WingsModule::Selected();
        if (sel.seq > 0 && !sel.userId.empty())
            out += ",\"wingSelect\":{\"seq\":" + std::to_string(sel.seq)
                 + ",\"userId\":" + J(sel.userId)
                 + ",\"name\":" + J(sel.name) + "}";
    }

    // WHICH LOG THE INSTANCE LOG PAGE IS SHOWING, and the mod's own last status line. Both are
    // the bridge's to report: the feed is a bridge-level buffer, and the status is whatever the
    // engine last said out loud, wherever it said it from.
    out += ",\"archiveFeed\":" + Json::Str(Feed::Showing() == Feed::Kind::Cache ? "cache" : "archiver");
    out += ",\"lastStatus\":"  + Json::Str(ScreenUI::LastToast());

    // EVERY FEATURE'S OWN STATE: what the client's cards read to know they are on (see
    // Actions::RegisterState). Appended last so a contributor can never break the roster above it.
    Actions::EmitState(out);

    // THE WORLD, when the log has told us. The client treats a MISSING field as "unchanged", so an
    // empty one is never sent: wiping the name it already has would be worse than saying nothing.
    {
        std::string wn = World::Name(), wid = World::Id(), wi = World::Instance();
        if (!wn.empty())  out += ",\"world\":" + J(wn);
        if (!wid.empty()) out += ",\"worldId\":" + J(wid);
        if (!wi.empty())  out += ",\"instanceId\":" + J(wi);
    }
    out += "}";

    // SAY ONCE WHAT THIS BUILD PUBLISHES.
    //
    // A client card reads a key it never receives as off / empty / zero, which is indistinguishable
    // from a feature that is broken -- and that is exactly how a whole page of working features
    // came to look dead. One line naming every top-level key makes the contract checkable from the
    // log instead of from the symptom. Keys only, never their values: the roster is in there.
    {
        static bool said = false;
        if (!said) {
            said = true;
            std::string keys;
            int depth = 0, n = 0;
            for (size_t i = 0; i < out.size(); ++i) {
                char c = out[i];
                if (c == '"') {                       // skip over a string, escapes included
                    size_t j = i + 1;
                    std::string tok;
                    while (j < out.size() && out[j] != '"') { if (out[j] == '\\') ++j; tok += out[j]; ++j; }
                    if (depth == 1 && j + 1 < out.size() && out[j + 1] == ':') {
                        if (!keys.empty()) keys += ", ";
                        keys += tok; ++n;
                    }
                    i = j;
                    continue;
                }
                if (c == '{' || c == '[') ++depth;
                else if (c == '}' || c == ']') --depth;
            }
            Log::Writef("Info", "[Bridge] sync publie %d cle(s) : %s", n, keys.c_str());
        }
    }
    return out;
}

void BridgeModule::Worker() {
    while (!m_stopping.load()) {
        if (!Enabled()) {
            m_connected.store(false);
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
            continue;
        }
        std::string const host = m_host;

        if (m_schemaPending.load()) {
            auto r = Http::PostJson(host + "/mod/schema", BuildSchema(), 5000);
            if (r.ok) {
                m_schemaPending.store(false);
                Log::Writef("Info", "[Bridge] schema accepted by the client (%s).", host.c_str());
                std::lock_guard lock(m_payloadLock); m_status = "schema accepte";
            } else {
                m_connected.store(false);
                { std::lock_guard lock(m_payloadLock);
                  m_status = "client injoignable (" + (r.error.empty() ? std::string("?") : r.error) + ")"; }
                std::this_thread::sleep_for(std::chrono::seconds(2));
                continue;
            }
        }

        std::string payload;
        { std::lock_guard lock(m_payloadLock); payload = m_payload; }
        if (payload.empty()) { std::this_thread::sleep_for(std::chrono::milliseconds(200)); continue; }

        auto r = Http::PostJson(host + "/mod/sync", payload, kSyncTimeoutMs);
        if (!r.ok) {
            if (m_connected.exchange(false))
                Log::Writef("Warning", "[Bridge] link lost: %s", r.error.empty() ? "?" : r.error.c_str());
            m_schemaPending.store(true);   // the client resets per-session state on a new schema
            std::this_thread::sleep_for(std::chrono::seconds(2));
            continue;
        }
        if (!m_connected.exchange(true))
            Log::Info("[Bridge] connected to the desktop app.");

        // (open-source build) The decryptor and Dex-patcher arming handshakes are removed:
        // both are server-gated paid features and are not shipped in this repository.

        bool hot = false;
        std::vector<Command> got;
        Reader rd(r.body);
        if (rd.Eat('{')) {
            while (true) {
                std::string key;
                if (!rd.Str(key) || !rd.Eat(':')) break;
                if (key == "hot") {
                    rd.Skip();
                    hot = (rd.i < r.body.size() && r.body[rd.i] == 't');
                    rd.SkipValue();
                } else if (key == "commands" && rd.Eat('[')) {
                    while (!rd.Peek(']')) {
                        if (!rd.Eat('{')) break;
                        Command c;
                        while (true) {
                            std::string f;
                            if (!rd.Str(f) || !rd.Eat(':')) break;
                            if      (f == "seq")   rd.Number(c.seq);
                            else if (f == "kind")  rd.Str(c.kind);
                            else if (f == "id")    rd.Str(c.id);
                            else if (f == "value") rd.Str(c.value);
                            else rd.SkipValue();
                            if (rd.Eat(',')) continue;
                            break;
                        }
                        rd.Eat('}');
                        if (!c.kind.empty()) got.push_back(std::move(c));
                        if (rd.Eat(',')) continue;
                        break;
                    }
                    rd.Eat(']');
                } else rd.SkipValue();
                if (rd.Eat(',')) continue;
                break;
            }
        }
        m_hot.store(hot);
        if (!got.empty()) {
            std::lock_guard lock(m_queueLock);
            for (auto& c : got) m_queue.push_back(std::move(c));
        }
        { std::lock_guard lock(m_payloadLock); m_status = hot ? "connecte (page ouverte)" : "connecte"; }
    }
}

void BridgeModule::Apply(Command const& c) {
    if (c.kind == "set") {
        std::string const prefix = "Modules/";
        if (c.id.rfind(prefix, 0) == 0) {
            std::string const name = c.id.substr(prefix.size());
            for (Module* m : Engine::Modules()) {
                if (std::string(m->Name()) != name) continue;
                bool want = (c.value == "True" || c.value == "true" || c.value == "1");
                if (m->Enabled() != want) m->SetEnabled(want);
                return;
            }
        }
        // Anything else is a registered setting. A refusal is SAID: the client clears its queue
        // the moment it hands a command over, so a silent drop is the last trace it ever existed.
        if (!Config::Set(c.id, c.value))
            Log::Writef("Warning", "[Bridge] reglage inconnu '%s' = '%s' -- ignore.",
                        c.id.c_str(), c.value.c_str());
        return;
    }
    if (c.kind == "action") {
        if (c.id == "teleport") {
            void* local = Player::LocalApi();
            if (!local) return;
            for (auto const& p : Player::All())
                if (!p.isLocal && p.userId == c.value) { Player::TeleportTo(local, p.pos); return; }
            return;
        }
        if (c.id == "resetMovement") {
            if (void* local = Player::LocalApi()) {
                Player::SetRunSpeed(local, 4.f);  Player::SetWalkSpeed(local, 2.f);
                Player::SetStrafeSpeed(local, 2.f); Player::SetJumpImpulse(local, 3.f);
                Player::SetGravityStrength(local, 1.f);
            }
            return;
        }
        // DUMP USER -- the client's "Metadata: user" button. The answer goes back in the next
        // sync as a typed block, not through the shared results queue: that queue is drained
        // globally by whichever page is open, so anything put in it can be stolen by another page.
        if (c.id == "dumpUser") {
            for (auto const& p : Player::All()) {
                if (p.userId != c.value) continue;
                char buf[512];
                std::snprintf(buf, sizeof(buf),
                              "nom: %s\nuserId: %s\nplayerId: %d\nlocal: %s  master: %s  VR: %s\nposition: %.2f %.2f %.2f",
                              p.name.c_str(), p.userId.c_str(), p.id,
                              p.isLocal ? "oui" : "non", p.isMaster ? "oui" : "non",
                              p.inVR ? "oui" : "non",
                              static_cast<double>(p.pos.x), static_cast<double>(p.pos.y),
                              static_cast<double>(p.pos.z));
                std::lock_guard lock(m_payloadLock);
                m_pendingDump = buf;
                Log::Writef("Info", "[Bridge] dumpUser '%s' prepare.", p.name.c_str());
                return;
            }
            Log::Writef("Info", "[Bridge] dumpUser: player '%s' not found.", c.value.c_str());
            return;
        }

        // THE ARCHIVE FEED, handled HERE rather than by a module.
        //
        // The uploader emits a line per file, several a second. Routing it through the action
        // registry would be fine, but it must never take the paced frame queue: in the C# mod that
        // toll put the console 42 s behind and then filled a 4096-deep queue, which DROPPED 1119
        // commands -- exactly what a user reads as "the toggle does nothing". Appending a string to
        // a locked ring buffer touches no il2cpp, so it runs inline.
        if (c.id == "archiveLog") {
            size_t cut = c.value.find('|');
            std::string kindStr = cut == std::string::npos ? "archiver" : c.value.substr(0, cut);
            std::string text    = cut == std::string::npos ? c.value : c.value.substr(cut + 1);
            Feed::Add(kindStr == "cache" || kindStr == "Cache" ? Feed::Kind::Cache
                                                               : Feed::Kind::Archiver,
                      text);
            return;
        }
        if (c.id == "archiveLogToggle") {
            Feed::Show(Feed::Showing() == Feed::Kind::Cache ? Feed::Kind::Archiver
                                                            : Feed::Kind::Cache);
            return;
        }

        // A module that claimed this action handles it.
        if (Actions::Dispatch(c.id, c.value)) return;

        // Unported actions are named ONCE per id, never per occurrence: the client streams
        // archive-status actions several times a second and a line each would bury the log.
        static std::unordered_set<std::string> seen;
        if (seen.insert(c.id).second)
            Log::Writef("Info", "[Bridge] action '%s' received -- not ported yet.", c.id.c_str());
    }
}

void BridgeModule::OnUpdate() {
    if (!m_started.exchange(true))
        m_worker = std::thread([this] { Worker(); });

    double now = Engine::Time() * 1000.0;

    double period = m_hot.load() ? 200.0 : 1000.0;
    if (now - m_lastBuild >= period) {
        m_lastBuild = now;
        std::string p = BuildSync();
        if (!p.empty()) { std::lock_guard lock(m_payloadLock); m_payload.swap(p); }
    }

    // PACED DRAIN: one step per 10 ms of credit, always at least one so a lone command never waits,
    // credit reset when the queue is empty so idling cannot bank a burst.
    double elapsed = (m_lastPump > 0.0) ? (std::min)(now - m_lastPump, 80.0) : 0.0;
    m_lastPump = now;
    size_t pending;
    { std::lock_guard lock(m_queueLock); pending = m_queue.size(); }
    if (!pending) { m_credit = 0.0; return; }

    m_credit += elapsed;
    int steps = 1 + static_cast<int>(m_credit / 10.0);
    m_credit -= (steps - 1) * 10.0;
    for (int i = 0; i < steps; ++i) {
        Command c;
        { std::lock_guard lock(m_queueLock);
          if (m_queue.empty()) break;
          c = m_queue.front(); m_queue.pop_front(); }
        Apply(c);
        ++m_applied;
    }
}

}
