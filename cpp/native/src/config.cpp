#include "config.hpp"
#include "log.hpp"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>

namespace VRCA::Config {

namespace {
    std::vector<Entry> g_entries;
    std::mutex         g_lock;

    bool IEq(std::string const& a, std::string const& b) {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i) {
            char x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
            if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
            if (x != y) return false;
        }
        return true;
    }

    // A number the client can read back and we can parse again, whatever the machine's locale:
    // always a DOT, never a comma. The C# bridge learned this the hard way.
    std::string Num(double v) {
        char buf[64];
        std::snprintf(buf, sizeof buf, "%.4g", v);
        for (char& c : buf) if (c == ',') c = '.';
        return buf;
    }

    void Push(Entry e) {
        std::lock_guard lock(g_lock);
        for (auto const& x : g_entries)
            if (IEq(x.section, e.section) && IEq(x.key, e.key)) {
                Log::Writef("Warning", "[Config] '%s/%s' already registered -- duplicate ignored.",
                            e.section.c_str(), e.key.c_str());
                return;
            }
        g_entries.push_back(std::move(e));
    }
}

void Bool(char const* section, char const* key, char const* desc, bool* storage) {
    Entry e; e.section = section; e.key = key; e.desc = desc ? desc : "";
    e.type = "Boolean"; e.b = storage;
    Push(std::move(e));
}

void Float(char const* section, char const* key, char const* desc, float* storage,
           float lo, float hi) {
    Entry e; e.section = section; e.key = key; e.desc = desc ? desc : "";
    e.type = "Single"; e.f = storage;
    e.range = Num(lo) + " - " + Num(hi);
    Push(std::move(e));
}

void Int(char const* section, char const* key, char const* desc, int* storage, int lo, int hi) {
    Entry e; e.section = section; e.key = key; e.desc = desc ? desc : "";
    e.type = "Int32"; e.i = storage;
    e.range = std::to_string(lo) + " - " + std::to_string(hi);
    Push(std::move(e));
}

void Text(char const* section, char const* key, char const* desc, std::string* storage) {
    Entry e; e.section = section; e.key = key; e.desc = desc ? desc : "";
    e.type = "String"; e.s = storage;
    Push(std::move(e));
}

std::vector<Entry> const& All() { return g_entries; }

std::string ValueOf(Entry const& e) {
    if (e.b) return *e.b ? "True" : "False";
    if (e.f) return Num(static_cast<double>(*e.f));
    if (e.i) return std::to_string(*e.i);
    if (e.s) return *e.s;
    return "";
}

bool Set(std::string const& id, std::string const& value) {
    size_t slash = id.find('/');
    if (slash == std::string::npos || slash == 0) return false;
    std::string section = id.substr(0, slash), key = id.substr(slash + 1);

    std::lock_guard lock(g_lock);
    for (auto& e : g_entries) {
        if (!IEq(e.section, section) || !IEq(e.key, key)) continue;

        if (e.b) {
            *e.b = IEq(value, "true") || value == "1";
            return true;
        }
        if (e.f) {
            // The client sends a dot; a comma would mean a locale slipped through on its side, and
            // strtof would stop at it and silently store the integer part.
            std::string v = value;
            for (char& c : v) if (c == ',') c = '.';
            char* end = nullptr;
            float parsed = std::strtof(v.c_str(), &end);
            if (end == v.c_str()) return false;
            *e.f = parsed;
            return true;
        }
        if (e.i) {
            char* end = nullptr;
            long parsed = std::strtol(value.c_str(), &end, 10);
            if (end == value.c_str()) return false;
            *e.i = static_cast<int>(parsed);
            return true;
        }
        if (e.s) { *e.s = value; return true; }
        return false;
    }
    return false;
}

}
