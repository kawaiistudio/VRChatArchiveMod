#pragma once
#include <string>
#include <vector>

// PLAYING A SOUND, WITHOUT UNITY'S LOADERS.
//
// This build's il2cpp has no DownloadHandlerAudioClip(string, AudioType), so a file cannot simply
// be handed to Unity to decode. Every sound therefore ships as plain 16-bit PCM WAV and is turned
// into an AudioClip here, through AudioClip.Create + SetData -- the same route the C# mod settled
// on, and for the same reason.
//
// Game thread only: everything here touches il2cpp.
namespace VRCA::Audio {

    // Decode a 16-bit PCM WAV into an AudioClip. Null when the bytes are not that -- no guessing,
    // no partial clip: a half-read header played as audio is a noise nobody asked for.
    void* DecodeWav(std::vector<unsigned char> const& bytes, std::string const& name);

    // Play a clip once, at the listener, through an AudioSource the mod owns. Volume 0..1.
    bool PlayOneShot(void* clip, float volume);

    // Read a file and play it. The clip is cached by path, so a button pressed repeatedly decodes
    // once. Returns false when the file is missing or is not a PCM WAV -- and says which.
    bool PlayFile(std::string const& path, float volume);

    // Where the mod keeps its sounds: BepInEx/VRChatArchive/sounds. The client drops files there.
    [[nodiscard]] std::string SoundsDir();

    // Every <name>.wav in the sounds folder, without the extension.
    [[nodiscard]] std::vector<std::string> ListClips();

    // Stop whatever is playing and release the AudioSource.
    void Stop();
}
