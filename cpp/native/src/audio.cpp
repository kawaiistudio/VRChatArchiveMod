#include "audio.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstring>
#include <fstream>
#include <unordered_map>

namespace VRCA::Audio {

namespace {
    void* g_source = nullptr;                              // the AudioSource the mod owns
    std::unordered_map<std::string, void*> g_cache;        // path -> AudioClip

    int ReadI32(unsigned char const* d, size_t at) {
        return static_cast<int>(static_cast<unsigned int>(d[at]) |
                                (static_cast<unsigned int>(d[at + 1]) << 8) |
                                (static_cast<unsigned int>(d[at + 2]) << 16) |
                                (static_cast<unsigned int>(d[at + 3]) << 24));
    }
    short ReadI16(unsigned char const* d, size_t at) {
        return static_cast<short>(static_cast<unsigned short>(d[at]) |
                                  (static_cast<unsigned short>(d[at + 1]) << 8));
    }

    void* NewString(std::string const& s) {
        static void* fn = Il2::Export("il2cpp_string_new");
        if (!fn) return nullptr;
        using F = void*(*)(char const*);
        return reinterpret_cast<F>(fn)(s.c_str());
    }

    // A managed float[] the clip can be filled from. Allocated through il2cpp so the GC owns it.
    void* NewFloatArray(int count) {
        static void* fn = Il2::Export("il2cpp_array_new");
        void* fc = Il2::FindClass("System.Single");
        if (!fn || !fc || count <= 0) return nullptr;
        using F = void*(*)(void*, unsigned long long);
        return reinterpret_cast<F>(fn)(fc, static_cast<unsigned long long>(count));
    }

    // The mod's own AudioSource, on an object that survives a scene change: a source parented to
    // the world is destroyed with it, and the next sound would silently play nowhere.
    void* Source() {
        if (Unity::IsAlive(g_source)) return g_source;

        static void* ctor = Il2::FindMethod("UnityEngine.GameObject", ".ctor", 1);
        static void* dontDestroy = Il2::FindMethod("UnityEngine.Object", "DontDestroyOnLoad", 1);
        static void* newObj = Il2::Export("il2cpp_object_new");
        void* goClass = Il2::FindClass("UnityEngine.GameObject");
        if (!ctor || !newObj || !goClass) return nullptr;
        using NO = void*(*)(void*);
        void* go = reinterpret_cast<NO>(newObj)(goClass);
        if (!go) return nullptr;
        void* nm = NewString("VRCA_Audio");
        void* a[1] = { nm };
        Il2::Invoke(ctor, go, a);
        if (dontDestroy) { void* d[1] = { go }; Il2::Invoke(dontDestroy, nullptr, d); }

        g_source = Unity::AddComponent(go, "UnityEngine.AudioSource");
        if (!Unity::IsAlive(g_source)) { g_source = nullptr; return nullptr; }

        // 2D: a soundboard clip is for the listener, not a point in the world.
        if (void* m = Il2::FindMethod("UnityEngine.AudioSource", "set_spatialBlend", 1)) {
            float v = 0.f; void* b[1] = { &v };
            Il2::Invoke(m, g_source, b);
        }
        Log::Info("[Audio] mod audio source created.");
        return g_source;
    }
}

std::string SoundsDir() {
    char buf[MAX_PATH]{};
    GetModuleFileNameA(nullptr, buf, MAX_PATH);
    std::string exe = buf;
    size_t cut = exe.find_last_of("\\/");
    std::string pack = cut == std::string::npos ? "." : exe.substr(0, cut);
    return pack + "\\BepInEx\\VRChatArchive\\sounds";
}

std::vector<std::string> ListClips() {
    std::vector<std::string> out;
    WIN32_FIND_DATAA fd{};
    HANDLE h = FindFirstFileA((SoundsDir() + "\\*.wav").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        std::string n = fd.cFileName;
        if (n.size() > 4) n.resize(n.size() - 4);          // drop ".wav"
        out.push_back(n);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return out;
}

void* DecodeWav(std::vector<unsigned char> const& d, std::string const& name) {
    if (d.size() < 44) return nullptr;
    if (std::memcmp(d.data(), "RIFF", 4) != 0) return nullptr;

    int channels = 0, sampleRate = 0, bits = 0;
    size_t dataAt = 0;
    int dataLen = 0;
    size_t pos = 12;                                        // past "RIFF####WAVE"
    while (pos + 8 <= d.size()) {
        char id[5] = { static_cast<char>(d[pos]), static_cast<char>(d[pos + 1]),
                       static_cast<char>(d[pos + 2]), static_cast<char>(d[pos + 3]), 0 };
        int size = ReadI32(d.data(), pos + 4);
        size_t body = pos + 8;
        if (size < 0 || body + static_cast<size_t>(size) > d.size())
            size = static_cast<int>(d.size() - body);
        if (std::strcmp(id, "fmt ") == 0 && body + 16 <= d.size()) {
            channels   = ReadI16(d.data(), body + 2);
            sampleRate = ReadI32(d.data(), body + 4);
            bits       = ReadI16(d.data(), body + 14);
        } else if (std::strcmp(id, "data") == 0) {
            dataAt = body;
            dataLen = size;
        }
        pos = body + static_cast<size_t>(size) + (static_cast<size_t>(size) & 1);  // word-aligned
    }
    if (!dataAt || channels <= 0 || sampleRate <= 0 || bits != 16) {
        Log::Writef("Warning", "[Audio] '%s' is not a 16-bit PCM WAV (channels=%d, %d Hz, %d bits).",
                    name.c_str(), channels, sampleRate, bits);
        return nullptr;
    }

    int count = dataLen / 2;
    if (count <= 0) return nullptr;

    static void* create = Il2::FindMethod("UnityEngine.AudioClip", "Create", 5);
    static void* setData = Il2::FindMethod("UnityEngine.AudioClip", "SetData", 2);
    if (!create || !setData) {
        Log::Warn("[Audio] AudioClip.Create/SetData unavailable on this build.");
        return nullptr;
    }

    void* nm = NewString(name);
    int lengthSamples = count / channels;
    int ch = channels, sr = sampleRate;
    bool stream = false;
    void* ca[5] = { nm, &lengthSamples, &ch, &sr, &stream };
    void* clip = Il2::Invoke(create, nullptr, ca);
    if (!clip) return nullptr;

    void* arr = NewFloatArray(count);
    if (!arr) return nullptr;
    auto* samples = reinterpret_cast<float*>(static_cast<char*>(arr) + 0x20);   // elements at +0x20
    for (int i = 0; i < count; ++i)
        samples[i] = ReadI16(d.data(), dataAt + static_cast<size_t>(i) * 2) / 32768.f;

    int offset = 0;
    void* sa[2] = { arr, &offset };
    Il2::Invoke(setData, clip, sa);
    return clip;
}

bool PlayOneShot(void* clip, float volume) {
    void* src = Source();
    if (!src || !clip) return false;
    static void* play = Il2::FindMethod("UnityEngine.AudioSource", "PlayOneShot", 2);
    if (!play) { Log::Warn("[Audio] AudioSource.PlayOneShot unavailable."); return false; }
    float v = volume < 0.f ? 0.f : (volume > 1.f ? 1.f : volume);
    void* a[2] = { clip, &v };
    Il2::Invoke(play, src, a);
    return true;
}

bool PlayFile(std::string const& path, float volume) {
    auto hit = g_cache.find(path);
    if (hit != g_cache.end() && Unity::IsAlive(hit->second)) return PlayOneShot(hit->second, volume);

    std::ifstream f(path, std::ios::binary);
    if (!f) {
        Log::Writef("Warning", "[Audio] file not found: %s", path.c_str());
        return false;
    }
    std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(f)),
                                      std::istreambuf_iterator<char>());
    size_t slash = path.find_last_of("\\/");
    std::string name = slash == std::string::npos ? path : path.substr(slash + 1);

    void* clip = DecodeWav(bytes, name);
    if (!clip) return false;
    g_cache[path] = clip;                 // decoded once: a button pressed twice costs nothing
    Log::Writef("Info", "[Audio] '%s' decode (%zu octets).", name.c_str(), bytes.size());
    return PlayOneShot(clip, volume);
}

void Stop() {
    if (!Unity::IsAlive(g_source)) { g_source = nullptr; return; }
    if (void* m = Il2::FindMethod("UnityEngine.AudioSource", "Stop", 0))
        Il2::Invoke(m, g_source, nullptr);
}

}
