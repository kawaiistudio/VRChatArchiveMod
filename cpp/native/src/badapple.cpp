#include "badapple.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "log.hpp"
#include "screenui.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include <cstdio>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "ws2_32.lib")

namespace VRCA {

namespace {
    // OSC strings are null-terminated and padded to a multiple of four.
    void OscString(std::vector<char>& out, std::string const& s) {
        out.insert(out.end(), s.begin(), s.end());
        out.push_back('\0');
        while (out.size() % 4) out.push_back('\0');
    }

    // ONE SOCKET FOR THE WHOLE RUN. Opening and closing a UDP socket per frame is how a sender
    // ends up exhausting ephemeral ports on a long clip.
    class OscSender {
    public:
        bool Open() {
            WSADATA wsa{};
            if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
            m_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
            if (m_sock == INVALID_SOCKET) return false;
            m_addr.sin_family = AF_INET;
            m_addr.sin_port = htons(9000);
            inet_pton(AF_INET, "127.0.0.1", &m_addr.sin_addr);
            return true;
        }
        void Close() {
            if (m_sock != INVALID_SOCKET) closesocket(m_sock);
            m_sock = INVALID_SOCKET;
            WSACleanup();
        }
        // /chatbox/input ,sTF <text>: T = send it now instead of opening the keyboard, F = no
        // notification sound, which would otherwise fire on every single frame.
        void Chatbox(std::string const& text) {
            if (m_sock == INVALID_SOCKET) return;
            std::vector<char> pkt;
            OscString(pkt, "/chatbox/input");
            OscString(pkt, ",sTF");
            OscString(pkt, text);
            sendto(m_sock, pkt.data(), static_cast<int>(pkt.size()), 0,
                   reinterpret_cast<sockaddr*>(&m_addr), sizeof m_addr);
        }
    private:
        SOCKET      m_sock = INVALID_SOCKET;
        sockaddr_in m_addr{};
    };

    std::string FramesPath() {
        char buf[MAX_PATH]{};
        GetModuleFileNameA(nullptr, buf, MAX_PATH);
        std::string exe = buf;
        size_t cut = exe.find_last_of("\\/");
        std::string pack = cut == std::string::npos ? "." : exe.substr(0, cut);
        return pack + "\\BepInEx\\VRChatArchive\\badapple.frames";
    }
}

BadAppleModule::BadAppleModule()
    : Module("Bad Apple", "play Bad Apple in your chatbox (OSC must be enabled in-game)")
{
    Config::Int("Bad Apple", "Fps", "frames sent per second", &m_fps, 1, 10);

    Actions::Register("badApple", [](std::string const&, void* u) {
        static_cast<BadAppleModule*>(u)->Toggle();
    }, this);

    Actions::RegisterState([](std::string& out, void* u) {
        out += ",\"badApple\":" + Json::Bool(static_cast<BadAppleModule*>(u)->m_playing);
    }, this);
    m_enabled = true;      // nothing runs until it is started
}

BadAppleModule::~BadAppleModule() { m_playing = false; }

void BadAppleModule::OnUpdate() {}

void BadAppleModule::OnDisable() { m_playing = false; }

bool BadAppleModule::Load() {
    if (m_loaded) return !m_frames.empty();
    m_loaded = true;

    std::string path = FramesPath();
    std::ifstream f(path);
    if (!f) {
        Log::Writef("Warning", "[BadApple] bake not found: %s", path.c_str());
        return false;
    }

    // Header: "<w> <h> <intervalMs> <flags>".
    std::string header;
    if (!std::getline(f, header)) return false;
    if (std::sscanf(header.c_str(), "%d %d %d", &m_w, &m_h, &m_bakedIntervalMs) < 3) {
        Log::Writef("Warning", "[BadApple] en-tete illisible : '%s'", header.c_str());
        return false;
    }
    if (m_w <= 0 || m_h <= 0 || m_bakedIntervalMs <= 0) return false;

    std::string line;
    while (std::getline(f, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
        if (line.empty()) continue;
        m_frames.push_back(line);
    }
    Log::Writef("Info", "[BadApple] bake loaded: %zu frame(s), %dx%d, %d ms.",
                m_frames.size(), m_w, m_h, m_bakedIntervalMs);
    return !m_frames.empty();
}

void BadAppleModule::Toggle() {
    if (m_playing) {
        m_playing = false;
        Log::Info("[BadApple] stopped.");
        ScreenUI::Toast("Bad Apple stopped");
        return;
    }
    if (!Load()) {
        ScreenUI::Toast("Bad Apple: bake missing from BepInEx/VRChatArchive");
        return;
    }
    m_playing = true;
    ScreenUI::Toast("Bad Apple -- OSC must be enabled in-game");
    std::thread(&BadAppleModule::Worker, this).detach();
}

void BadAppleModule::Worker() {
    OscSender osc;
    if (!osc.Open()) {
        Log::Warn("[BadApple] UDP socket failed -- nothing sent.");
        m_playing = false;
        return;
    }

    int fps = m_fps < 1 ? 1 : (m_fps > 10 ? 10 : m_fps);
    int stepMs = 1000 / fps;

    // The bake is DENSER than playback, so each send picks the frame whose baked time is nearest
    // to the elapsed time rather than walking the list: that keeps the run the right LENGTH
    // instead of stretching it by however many frames were skipped.
    DWORD start = GetTickCount();
    size_t sent = 0;
    while (m_playing) {
        DWORD elapsed = GetTickCount() - start;
        size_t idx = static_cast<size_t>(elapsed) / static_cast<size_t>(m_bakedIntervalMs);
        if (idx >= m_frames.size()) break;

        // One chatbox line per row: VRChat wraps at the bake's own width, which is why it is 15.
        std::string const& fr = m_frames[idx];
        std::string text;
        text.reserve(fr.size() + static_cast<size_t>(m_h));
        for (int row = 0; row < m_h; ++row) {
            size_t at = static_cast<size_t>(row) * static_cast<size_t>(m_w);
            if (at >= fr.size()) break;
            text.append(fr, at, static_cast<size_t>(m_w));
            if (row + 1 < m_h) text.push_back('\n');
        }
        osc.Chatbox(text);
        ++sent;
        Sleep(static_cast<DWORD>(stepMs));
    }

    osc.Chatbox("");            // leave the chatbox clean rather than frozen on a frame
    osc.Close();
    m_playing = false;
    Log::Writef("Info", "[BadApple] termine : %zu image(s) envoyee(s).", sent);
}

}
