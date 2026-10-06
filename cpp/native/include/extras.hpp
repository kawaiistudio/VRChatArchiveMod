#pragma once
#include "module.hpp"

#include <string>
#include <vector>

namespace VRCA {

    // MENU BACKGROUNDS -- the QuickMenu / Main Menu backgrounds VRChat hides behind VRChat Plus,
    // animated ones included.
    //
    // WHAT IS TOUCHED, AND WHY ONLY THIS. Each background is a BackgroundOption carrying an id, a
    // name, a preview, a material name and ONE bool: _isVRCPlus, meaning "this is a VRC+ one". So
    // the unlock is setting it to FALSE -- setting them all true would lock every background
    // instead. The parallax is not a separate thing to switch on: an animated background is just
    // an option whose visual comes from a material, gated by that same bool.
    //
    // IT DELIBERATELY DOES NOT SPOOF VRC+ ITSELF. APIUser.isSupporter looks like a tempting single
    // point, but it is a field of an ApiModel -- the same base that carries Save() and Put() -- so
    // a value forged there can be serialised back to VRChat by any path that saves the model,
    // which turns a local display tweak into modified data arriving at the server. One bool on a
    // local ScriptableObject cannot leave this machine.
    class BackgroundsModule : public Module {
    public:
        BackgroundsModule();
        void OnUpdate()  override;
        void OnDisable() override;
    private:
        struct Flipped { void* option; int off; };
        std::vector<Flipped> m_flipped;
        double m_next = 0.0;
        bool   m_warned = false;
    };

    // VIDEO URL -- put a link into the world's own video player.
    //
    // A video player is an UdonBehaviour holding a VRCUrl variable; playing something means
    // writing that variable and firing the world's own play event. So this does not drive a
    // player it built: it asks the WORLD's script, the same way the world's own buttons do.
    //
    // THE SCRIPT IS FOUND BY WHAT IT HOLDS -- a symbol whose current value is a VRCUrl -- not by
    // its name: every world names its video player differently.
    class VideoUrlModule : public Module {
    public:
        VideoUrlModule();
        void OnUpdate() override;

        bool Play(std::string const& url);
    private:
        std::string m_status;
    };
}
