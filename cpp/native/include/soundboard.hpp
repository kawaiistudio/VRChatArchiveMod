#pragma once
#include "module.hpp"

#include <string>

namespace VRCA {

    // SOUNDBOARD -- a button is pressed, the sound plays here.
    //
    // WHAT CROSSES THE NETWORK IS A NAME, never audio and never a URL. The sound must already be
    // on this machine for a trigger to play it. Broadcasting a URL instead would let anyone make
    // every mod user's client fetch an arbitrary file at once -- and hand out their IP doing it.
    //
    // THE LISTENER KEEPS THE LAST WORD, because this plays audio in somebody else's headset:
    //   * an Listen switch they can turn off at any time, and a sound that arrives while it is
    //     off simply does not play;
    //   * a volume that is THEIRS, not the sender's.
    //
    // Sounds are WAV files in BepInEx/VRChatArchive/sounds, so the owner can add their own and the
    // desktop client can drop files there. The name is stripped of anything path-shaped before it
    // is used: a key arriving from the network must never be able to name a file outside it.
    class SoundboardModule : public Module {
    public:
        SoundboardModule();
        void OnUpdate()  override;
        void OnDisable() override;

        bool Play(std::string const& key, bool preview);

        float m_volume = 0.7f;
        bool  m_listen = true;

    private:
        bool   m_selfTested = false;
        double m_next = 0.0;
    };
}
