#include "feed.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdio>
#include <mutex>

namespace VRCA::Feed {

namespace {
    // Deliberately small. This is a LIVE feed on a panel that shows a handful of lines, not an
    // archive of its own -- the client keeps the real history.
    constexpr size_t kCapacity = 200;

    std::mutex         g_lock;
    std::vector<Entry> g_archiver, g_cache;
    Kind               g_showing = Kind::Archiver;
    int                g_version = 0;

    std::string Clock() {
        SYSTEMTIME t{};
        GetLocalTime(&t);
        char b[16];
        std::snprintf(b, sizeof b, "%02d:%02d:%02d", t.wHour, t.wMinute, t.wSecond);
        return b;
    }
}

void Add(Kind k, std::string const& line) {
    if (line.empty()) return;
    std::lock_guard lock(g_lock);
    auto& list = (k == Kind::Cache) ? g_cache : g_archiver;
    list.push_back({ Clock(), k, line });
    if (list.size() > kCapacity) list.erase(list.begin(), list.begin() + (list.size() - kCapacity));
    ++g_version;
}

void Show(Kind k) {
    std::lock_guard lock(g_lock);
    if (g_showing == k) return;
    g_showing = k;
    ++g_version;      // switching must redraw even though no line arrived
}

Kind Showing() { std::lock_guard lock(g_lock); return g_showing; }

std::vector<Entry> Current() {
    std::lock_guard lock(g_lock);
    return (g_showing == Kind::Cache) ? g_cache : g_archiver;
}

int Version() { std::lock_guard lock(g_lock); return g_version; }

}
