#include "world.hpp"
#include "log.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <atomic>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>

namespace VRCA::World {

namespace {
    std::mutex  g_lock;
    std::string g_id, g_name, g_instance;

    std::thread       g_worker;
    std::atomic<bool> g_started{false}, g_stopping{false};

    std::string LogDir() {
        char* up = nullptr; size_t n = 0;
        if (_dupenv_s(&up, &n, "USERPROFILE") != 0 || !up) return {};
        std::string d = std::string(up) + "\\AppData\\LocalLow\\VRChat\\VRChat";
        free(up);
        return d;
    }

    // VRChat keeps several logs; the one being written now is the newest.
    std::string NewestLog(std::string const& dir) {
        WIN32_FIND_DATAA fd{};
        HANDLE h = FindFirstFileA((dir + "\\output_log_*.txt").c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) return {};
        std::string best;
        FILETIME bestTime{};
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            if (best.empty() || CompareFileTime(&fd.ftLastWriteTime, &bestTime) > 0) {
                bestTime = fd.ftLastWriteTime;
                best = dir + "\\" + fd.cFileName;
            }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
        return best;
    }

    bool IsHex(char c) {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    // "wrld_" + a 36-character GUID. Matched by hand rather than with a regex: this runs on every
    // line of a log that grows to tens of megabytes.
    size_t FindWorldId(std::string const& line, std::string& out) {
        size_t at = line.find("wrld_");
        if (at == std::string::npos) return std::string::npos;
        size_t start = at + 5;
        size_t i = start;
        while (i < line.size() && (IsHex(line[i]) || line[i] == '-')) ++i;
        if (i - start < 36) return std::string::npos;
        out = line.substr(at, 5 + 36);
        return at + 5 + 36;
    }

    void Consider(std::string const& line) {
        if (line.find("Joining") == std::string::npos) return;

        // "Joining or Creating Room: <name>" -- the world's name, on its own line.
        constexpr char const* kRoom = "Joining or Creating Room:";
        size_t room = line.find(kRoom);
        if (room != std::string::npos) {
            std::string name = line.substr(room + std::char_traits<char>::length(kRoom));
            while (!name.empty() && (name.front() == ' ' || name.front() == '\t')) name.erase(name.begin());
            while (!name.empty() && (name.back() == '\r' || name.back() == '\n' || name.back() == ' '))
                name.pop_back();
            if (!name.empty() && name.size() < 200) {
                std::lock_guard lock(g_lock);
                g_name = name;
            }
            return;
        }

        std::string id;
        size_t after = FindWorldId(line, id);
        if (after == std::string::npos) return;

        bool changed = false;
        {
            std::lock_guard lock(g_lock);
            if (g_id != id) { g_id = id; g_instance.clear(); changed = true; }
            // "...wrld_xxx:12345~region(eu)" -- the instance number sits between ':' and '~'.
            if (after < line.size() && line[after] == ':') {
                std::string tail = line.substr(after + 1);
                size_t tilde = tail.find('~');
                if (tilde != std::string::npos) tail = tail.substr(0, tilde);
                while (!tail.empty() && (tail.back() == '\r' || tail.back() == '\n' || tail.back() == ' '))
                    tail.pop_back();
                if (!tail.empty() && tail.size() < 40) g_instance = tail;
            }
        }
        if (changed) Log::Writef("Info", "[World] current world read from the VRChat log: %s", id.c_str());
    }

    void Worker() {
        std::string path;
        std::streamoff pos = 0;
        std::string carry;

        while (!g_stopping.load()) {
            Sleep(1000);

            std::string dir = LogDir();
            if (dir.empty()) continue;
            std::string newest = NewestLog(dir);
            if (newest.empty()) continue;
            if (newest != path) {
                // A new session wrote a new log: start from its beginning so the join we already
                // missed is still seen.
                path = newest;
                pos = 0;
                carry.clear();
            }

            // SHARED READ. VRChat holds the log open for writing; opening it exclusively would
            // fail every time and the world would never be known.
            HANDLE h = CreateFileA(path.c_str(), GENERIC_READ,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                   nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h == INVALID_HANDLE_VALUE) continue;

            LARGE_INTEGER size{};
            if (!GetFileSizeEx(h, &size)) { CloseHandle(h); continue; }
            if (size.QuadPart < pos) { pos = 0; carry.clear(); }    // truncated or rotated

            LARGE_INTEGER seek{};
            seek.QuadPart = pos;
            SetFilePointerEx(h, seek, nullptr, FILE_BEGIN);

            std::string buf;
            buf.resize(static_cast<size_t>((std::min)(size.QuadPart - pos, LONGLONG{ 1 << 20 })));
            DWORD got = 0;
            if (!buf.empty() && ReadFile(h, buf.data(), static_cast<DWORD>(buf.size()), &got, nullptr))
                pos += got;
            CloseHandle(h);
            if (!got) continue;
            buf.resize(got);

            carry += buf;
            size_t start = 0;
            for (;;) {
                size_t nl = carry.find('\n', start);
                if (nl == std::string::npos) break;
                Consider(carry.substr(start, nl - start));
                start = nl + 1;
            }
            carry.erase(0, start);
            if (carry.size() > (1 << 16)) carry.clear();   // a line that long is not a join line
        }
    }
}

void Start() {
    if (g_started.exchange(true)) return;
    g_worker = std::thread(&Worker);
    Log::Info("[World] started reading the VRChat log (world name and id).");
}

void Stop() {
    if (!g_started.load()) return;
    g_stopping.store(true);
    if (g_worker.joinable()) g_worker.join();
}

std::string Id()       { std::lock_guard lock(g_lock); return g_id; }
std::string Name()     { std::lock_guard lock(g_lock); return g_name; }
std::string Instance() { std::lock_guard lock(g_lock); return g_instance; }

}
