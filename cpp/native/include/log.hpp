#pragma once
#include <string>

// A tiny file logger. The native DLL runs under BepInEx but writes its own log so a problem is
// readable without untangling it from the chainloader's output, exactly like the old mod's habit.
// Thread-safe (a lock around the append): the HTTP worker and the game thread both log.
namespace VRCA::Log {

    // Opens <logDir>/VRChatArchive.log (truncating), remembering the path for every later line.
    void Init(std::string const& logDir);

    void Write(char const* level, std::string const& msg);

    // printf-style, for the common "name = 0x%llX" lines; caps the formatted result.
    void Writef(char const* level, char const* fmt, ...);

    inline void Info(std::string const& m)  { Write("Info",    m); }
    inline void Warn(std::string const& m)  { Write("Warning", m); }
    inline void Error(std::string const& m) { Write("Error",   m); }
}
