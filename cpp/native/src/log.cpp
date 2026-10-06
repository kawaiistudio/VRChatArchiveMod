#include "log.hpp"

#include <cstdarg>
#include <cstdio>
#include <ctime>
#include <mutex>
#include <string>

namespace VRCA::Log {

namespace {
    std::mutex  g_lock;
    std::string g_path;
    bool        g_ready = false;

    std::string Stamp() {
        std::time_t tt = std::time(nullptr);
        std::tm tm{};
        localtime_s(&tm, &tt);
        char b[16];
        std::snprintf(b, sizeof(b), "%02d:%02d:%02d", tm.tm_hour, tm.tm_min, tm.tm_sec);
        return b;
    }
}

void Init(std::string const& logDir) {
    std::lock_guard lock(g_lock);
    g_path = logDir;
    if (!g_path.empty() && g_path.back() != '\\' && g_path.back() != '/') g_path += '\\';
    g_path += "VRChatArchive.log";
    if (FILE* f = nullptr; fopen_s(&f, g_path.c_str(), "w") == 0 && f) {
        std::fprintf(f, "[%s] [Info] VRChat Archive (C++) log open.\n", Stamp().c_str());
        std::fclose(f);
        g_ready = true;
    }
}

void Write(char const* level, std::string const& msg) {
    std::lock_guard lock(g_lock);
    if (!g_ready) return;
    if (FILE* f = nullptr; fopen_s(&f, g_path.c_str(), "a") == 0 && f) {
        std::fprintf(f, "[%s] [%s] %s\n", Stamp().c_str(), level, msg.c_str());
        std::fclose(f);
    }
}

void Writef(char const* level, char const* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    Write(level, buf);
}

}
