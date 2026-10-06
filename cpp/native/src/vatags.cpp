#include "vatags.hpp"
#include "http.hpp"
#include "log.hpp"

#include <chrono>
#include <mutex>
#include <unordered_map>

namespace VRCA {

namespace {
    constexpr char const* kUrl = "https://vrchatarchive.org/api/va-tags";

    std::mutex                                   g_lock;
    std::unordered_map<std::string, std::string> g_tags;   // userId -> tags array JSON, verbatim
    int                                          g_count = 0;

    // A tolerant structural scanner. It never trusts field order and skips strings correctly, so a
    // tag's text containing '{', ']' or the word "tags" cannot throw the walk off.
    struct Json {
        char const* s; size_t n, i = 0;
        explicit Json(std::string const& src) : s(src.data()), n(src.size()) {}
        void ws() { while (i < n && (s[i]==' '||s[i]=='\n'||s[i]=='\r'||s[i]=='\t')) ++i; }
        bool eat(char c) { ws(); if (i < n && s[i]==c) { ++i; return true; } return false; }
        bool peek(char c) { ws(); return i < n && s[i]==c; }

        // A JSON string into `out` (only the escapes a user id or ascii needs are decoded; a \u is
        // kept raw, which is fine because ids are ascii and tag text is passed through as a span).
        bool str(std::string& out) {
            ws();
            if (i >= n || s[i] != '"') return false;
            ++i; out.clear();
            while (i < n && s[i] != '"') {
                char c = s[i++];
                if (c == '\\' && i < n) {
                    char e = s[i++];
                    switch (e) {
                        case 'n': out += '\n'; break;
                        case 't': out += '\t'; break;
                        case 'r': out += '\r'; break;
                        case 'u': out += "\\u"; if (i + 4 <= n) { out.append(s + i, 4); i += 4; } break;
                        default:  out += e;
                    }
                } else out += c;
            }
            if (i < n) ++i;      // closing quote
            return true;
        }

        // Skip one value; captures its raw [start,end) span for the caller that wants it verbatim.
        bool skip(size_t* start = nullptr, size_t* end = nullptr) {
            ws();
            if (start) *start = i;
            if (i >= n) return false;
            char c = s[i];
            if (c == '"') { std::string t; str(t); }
            else if (c == '{' || c == '[') {
                char close = (c == '{') ? '}' : ']';
                int depth = 0;
                while (i < n) {
                    char d = s[i];
                    if (d == '"') { std::string t; str(t); continue; }
                    ++i;
                    if (d == c) ++depth;
                    else if (d == close) { --depth; if (depth == 0) break; }
                }
            } else {
                while (i < n && s[i] != ',' && s[i] != '}' && s[i] != ']') ++i;
            }
            if (end) *end = i;
            return true;
        }
    };

    void Parse(std::string const& body) {
        std::unordered_map<std::string, std::string> fresh;
        Json p(body);
        if (!p.eat('{')) return;

        // Top object: find "records":[ ... ]; skip anything else (count, etc.).
        bool done = false;
        while (!done) {
            std::string key;
            if (!p.str(key) || !p.eat(':')) break;
            if (key == "records" && p.peek('[')) {
                p.eat('[');
                if (!p.peek(']')) {
                    while (true) {
                        if (!p.eat('{')) break;          // one record
                        std::string uid, tags;
                        bool haveTags = false;
                        while (true) {
                            std::string rk;
                            if (!p.str(rk) || !p.eat(':')) break;
                            if (rk == "user_id") {
                                p.str(uid);
                            } else if (rk == "tags") {
                                size_t a = 0, b = 0;
                                p.skip(&a, &b);
                                tags.assign(body, a, b - a);
                                haveTags = true;
                            } else {
                                p.skip();
                            }
                            if (!p.eat(',')) break;
                        }
                        p.eat('}');
                        if (!uid.empty() && haveTags && tags.find('{') != std::string::npos)
                            fresh[uid] = tags;
                        if (!p.eat(',')) break;
                    }
                }
                p.eat(']');
                done = true;
            } else {
                p.skip();
                if (!p.eat(',')) break;
            }
        }

        std::lock_guard<std::mutex> lock(g_lock);
        g_tags.swap(fresh);
        g_count = static_cast<int>(g_tags.size());
    }
}

std::string VaTagsModule::ForUser(std::string const& userId) {
    if (userId.empty()) return "[]";
    std::lock_guard<std::mutex> lock(g_lock);
    auto it = g_tags.find(userId);
    return it == g_tags.end() ? std::string("[]") : it->second;
}

VaTagsModule::VaTagsModule()
    : Module("VA Tags", "community tags shown on the players page and nameplates")
{
    m_enabled = true;
    m_worker = new std::thread([this] { Worker(); });
}

VaTagsModule::~VaTagsModule() {
    m_stop.store(true);
    if (m_worker) {
        if (m_worker->joinable()) m_worker->join();
        delete m_worker;
        m_worker = nullptr;
    }
}

void VaTagsModule::OnUpdate() {}   // all the work is on the worker; nothing per frame

void VaTagsModule::Worker() {
    // First fetch shortly after boot, then every two minutes. The table is small and the read is
    // public, so this is light; a failure just keeps the last good copy.
    std::this_thread::sleep_for(std::chrono::seconds(5));
    bool firstOk = false;
    while (!m_stop.load()) {
        auto r = Http::Get(kUrl, 12000);
        if (r.ok && r.status == 200 && !r.body.empty()) {
            Parse(r.body);
            if (!firstOk) {
                firstOk = true;
                int c;
                { std::lock_guard<std::mutex> lock(g_lock); c = g_count; }
                Log::Writef("Info", "[VaTags] database loaded: %d tagged player(s) -- shown on the players page.", c);
            }
        } else if (!firstOk) {
            Log::Writef("Warning", "[VaTags] could not read the tag database (status %d) -- retrying.", r.status);
        }
        for (int slept = 0; slept < 120 && !m_stop.load(); ++slept)
            std::this_thread::sleep_for(std::chrono::seconds(1));
    }
}

}
