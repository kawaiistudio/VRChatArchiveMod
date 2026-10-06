#pragma once
#include "module.hpp"

#include <string>

namespace VRCA {

    // AVATARS -- wear an id, clone what someone is wearing, read an avatar's metadata.
    //
    // THE ROUTE IS THE GAME'S OWN. VRChat changes avatar through a static method on its avatar
    // page that takes (ApiAvatar, string). Both the class and the method are renamed every build,
    // so neither is looked up by name:
    //   * ApiAvatar is whatever class the player's worn-avatar object turned out to be -- the one
    //     that answered with an "avtr_" id when the player layer probed it;
    //   * the change method is the one STATIC void method in Assembly-CSharp taking exactly
    //     (that class, String). An ambiguous match is refused rather than guessed at.
    //
    // A fresh ApiAvatar is built and its id written, which is all the game needs to go and fetch
    // the rest. Nothing is sent to VRChat's API by hand.
    class AvatarModule : public Module {
    public:
        AvatarModule();
        void OnUpdate() override;

        // Switch into this avatar id. Refuses anything that is not an avtr_ id, and refuses the
        // one already worn -- VRChat reloads it from scratch otherwise.
        bool Wear(std::string const& avatarId);

        // Switch into whatever the named player is wearing.
        bool Clone(std::string const& userId);

        // A .vrca the client just put in VRChat's test-avatar folder. VRChat lists those under
        // the id "local:sdk_<name>", so this is Wear with a different kind of id -- NOT a file
        // load: the game has already built the avatar, we only ask it to switch.
        bool WearLocal(std::string const& name);

        // Everything the game knows about an avatar, as JSON for the client's metadata pane.
        [[nodiscard]] std::string Dump(std::string const& avatarId);

        [[nodiscard]] std::string LastStatus() const { return m_status; }

    private:
        bool  Resolve();
        void* NewApiAvatar(std::string const& avatarId);

        void*       m_change = nullptr;     // the static (ApiAvatar, String) -> void
        void*       m_setId = nullptr;      // ApiAvatar.set_id, declared on its ApiModel base
        bool        m_resolved = false;
        std::string m_status;
    };
}
