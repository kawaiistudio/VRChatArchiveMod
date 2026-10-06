#pragma once
#include <string>
#include <vector>

// THE PLAYER LAYER -- VRCPlayerApi and the VRChat objects behind it.
//
// Everything player-shaped in the mod goes through here: the roster, movement, positions, the
// account behind a player. VRCPlayerApi is a public SDK type whose NAME survives every build, so it
// resolves directly; VRC.Player / VRCPlayer are renamed and come from the recovery layer's aliases.
// Game thread only.
namespace VRCA::Player {

    struct Vec3 { float x = 0, y = 0, z = 0; };

    struct Info {
        void*       api = nullptr;      // VRCPlayerApi
        int         id = -1;
        std::string name;
        std::string userId;
        std::string avatarId;
        std::string avatarName;
        std::string release;        // "public" / "private", empty while the avatar is loading
        std::string trust;
        std::string platform;
        bool        isLocal = false, isMaster = false, isOwner = false, inVR = false, plus = false;
        bool        hasPos = false;
        Vec3        pos;
    };

    // Every player in the instance, read fresh. Empty outside a world.
    std::vector<Info> All();

    // The local player's VRCPlayerApi, or null.
    void* LocalApi();

    // The player's GameObject. VRCPlayerApi.get_gameObject does not exist on this build, so this
    // goes through the VRC.Player COMPONENT, which is a real UnityEngine.Component and therefore
    // always has one. Returns null for a player whose objects are not built yet.
    [[nodiscard]] void* GameObjectOf(void* api);

    [[nodiscard]] std::string DisplayName(void* api);
    [[nodiscard]] int         Id(void* api);
    [[nodiscard]] bool        IsLocal(void* api);
    [[nodiscard]] Vec3        Position(void* api);

    // Movement. Writing these is how speed / gravity / jump features work; a world reasserts its
    // own values on avatar load, which is why features re-apply rather than set once.
    void  SetRunSpeed(void* api, float v);
    void  SetWalkSpeed(void* api, float v);
    void  SetStrafeSpeed(void* api, float v);
    void  SetJumpImpulse(void* api, float v);
    void  SetGravityStrength(void* api, float v);
    [[nodiscard]] float GetRunSpeed(void* api);
    [[nodiscard]] float GetWalkSpeed(void* api);
    [[nodiscard]] float GetJumpImpulse(void* api);
    [[nodiscard]] float GetStrafeSpeed(void* api);
    [[nodiscard]] float GetGravityStrength(void* api);

    // Teleport the LOCAL player. rot may be null to keep the current rotation.
    void  TeleportTo(void* localApi, Vec3 pos);

    // Kill the player's velocity. Fly calls this every frame: zeroing velocity is what stops
    // gravity accumulating, which is why fly does not need to touch world gravity at all.
    void  ZeroVelocity(void* api);

    // The VRCPlayer[Local] ROOT transform -- the rig that carries the capsule and the camera.
    // Fly writes this transform's position; moving anything deeper only drags the mesh.
    [[nodiscard]] void* LocalRoot();

    // The account behind a player (VRC.Player -> APIUser), best effort: APIUser sits behind a
    // getter on this build and a player still loading has none yet.
    [[nodiscard]] std::string UserId(void* api);
    [[nodiscard]] std::string AvatarId(void* api);
    [[nodiscard]] std::string AvatarName(void* api);

    // "public" or "private", straight off the worn ApiAvatar. The desktop client needs it to
    // know whether FORCE CLONE can work at all -- offering the action on a private avatar only
    // produces a refusal a second later.
    [[nodiscard]] std::string AvatarRelease(void* api);

    // The obfuscated ApiAvatar class, learned from the object that answered with an "avtr_" id.
    // Null until at least one player has been probed. Wearing an avatar needs it: the game's own
    // change-avatar call takes an ApiAvatar, so one has to be built.
    [[nodiscard]] void* ApiAvatarClass();
    [[nodiscard]] std::string Platform(void* api);
    [[nodiscard]] std::string Trust(void* api);
}
