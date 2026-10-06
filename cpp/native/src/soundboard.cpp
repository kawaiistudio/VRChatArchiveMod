#include "soundboard.hpp"
#include "audio.hpp"
#include "bridge.hpp"
#include "config.hpp"
#include "log.hpp"
#include "engine.hpp"
#include "screenui.hpp"

#include <fstream>
#include <vector>

namespace VRCA {

SoundboardModule::SoundboardModule()
    : Module("Soundboard", "play a sound from the sounds folder, triggered from the client")
{
    Config::Float("Soundboard", "Volume", "volume of played sounds", &m_volume, 0.f, 1.f);
    Config::Bool ("Soundboard", "Listen",
                  "accept sounds triggered by others (otherwise only yours play)",
                  &m_listen);

    // THE LISTENER KEEPS THE LAST WORD. A soundboard plays audio in somebody else's headset, so
    // the volume is THEIRS and not the sender's, and the master switch is off-able at any time.
    // A triggered sound that the listener switched off simply does not play, and says so once.
    Actions::Register("sbPlay", [](std::string const& v, void* u) {
        static_cast<SoundboardModule*>(u)->Play(v, false);
    }, this);

    // PREVIEW is the owner's own press: it plays whatever the master switch says, because it is
    // not somebody else's sound arriving.
    Actions::Register("sbPreview", [](std::string const& v, void* u) {
        static_cast<SoundboardModule*>(u)->Play(v, true);
    }, this);

    m_enabled = true;      // nothing runs until a key arrives
}

// PROVE THE DECODER ONCE, WITHOUT MAKING A SOUND.
//
// AudioClip.Create, the managed float[] and SetData are the parts that can fail quietly on a new
// build, and they are only ever exercised when somebody presses a button -- so a broken decoder
// would stay invisible until the first press produced silence. This decodes one real clip at
// startup and says whether it worked. It never plays it.
void SoundboardModule::OnUpdate() {
    if (m_selfTested) return;
    double now = Engine::Time();
    if (now < m_next) return;
    m_next = now + 5.0;

    auto clips = Audio::ListClips();
    if (clips.empty()) {
        if (now > 25.0) {
            m_selfTested = true;
            Log::Writef("Info", "[Soundboard] no .wav in %s.", Audio::SoundsDir().c_str());
        }
        return;
    }
    m_selfTested = true;

    std::string path = Audio::SoundsDir() + "\\" + clips.front() + ".wav";
    std::ifstream f(path, std::ios::binary);
    if (!f) return;
    std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(f)),
                                      std::istreambuf_iterator<char>());
    void* clip = Audio::DecodeWav(bytes, clips.front());
    Log::Writef(clip ? "Info" : "Warning",
                clip ? "[Soundboard] %zu sound(s) available; decoder verified on '%s'."
                     : "[Soundboard] %zu sound(s) found but '%s' could not be decoded.",
                clips.size(), clips.front().c_str());
}

void SoundboardModule::OnDisable() { Audio::Stop(); }

bool SoundboardModule::Play(std::string const& key, bool preview) {
    if (key.empty()) { ScreenUI::Toast("Soundboard: no sound named"); return false; }

    if (!preview && !m_listen) {
        static bool said = false;
        if (!said) {
            said = true;
            Log::Info("[Soundboard] a sound was triggered but listening is off -- nothing played.");
        }
        return false;
    }

    // ONLY A NAME EVER CROSSES THE NETWORK, never audio and never a URL: a trigger can only play
    // something this machine already has. A url would let anyone make every mod user fetch an
    // arbitrary file at once, and hand out their IP doing it. So the key is resolved to a LOCAL
    // file, and a name that is not there is a refusal, not a download.
    std::string safe;
    for (char c : key) {
        if (c == '/' || c == '\\' || c == ':' || c == '.') continue;   // no path, ever
        safe += c;
    }
    if (safe.empty()) { ScreenUI::Toast("Soundboard: invalid sound name"); return false; }

    std::string path = Audio::SoundsDir() + "\\" + safe + ".wav";
    if (!Audio::PlayFile(path, m_volume)) {
        Log::Writef("Warning", "[Soundboard] '%s' not found or not a 16-bit PCM WAV.", safe.c_str());
        ScreenUI::Toast("Soundboard : '" + safe + "' missing from the sounds folder");
        return false;
    }
    Log::Writef("Info", "[Soundboard] %s '%s'.", preview ? "apercu" : "joue", safe.c_str());
    return true;
}

}
