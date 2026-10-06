#pragma once
#include "module.hpp"

#include <string>
#include <vector>

namespace VRCA {

    // BAD APPLE IN THE CHATBOX.
    //
    // The frames are pre-baked from the video into a plain text file: a header line of
    // "<width> <height> <intervalMs> <flags>", then one line per frame of width*height characters
    // from a brightness ramp. 15 wide is not arbitrary -- it is VRChat's chatbox wrap.
    //
    // DELIVERY IS PLAIN OSC to VRChat's own local input port, /chatbox/input on UDP 9000, with
    // ",sTF": send now without opening the keyboard, and no notification sound every frame. No
    // il2cpp at all, so playback runs on its own thread and cannot stall the game.
    //
    // OSC MUST BE ON IN THE GAME (radial menu, Options, OSC). Nothing here can switch it on, so a
    // run that produces silence says that rather than looking broken.
    class BadAppleModule : public Module {
    public:
        BadAppleModule();
        ~BadAppleModule() override;
        void OnUpdate()  override;
        void OnDisable() override;

        void Toggle();

        int m_fps = 6;        // how often a frame is actually SENT; the bake is denser than this

    private:
        bool Load();
        void Worker();

        std::vector<std::string> m_frames;
        std::string m_charset;
        int  m_w = 0, m_h = 0, m_bakedIntervalMs = 50;
        bool m_loaded = false;
        volatile bool m_playing = false;
    };
}
