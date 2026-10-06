#pragma once
#include <vector>

// LOOSE WORLD PROPS -- the shared collector behind Object Orbit, the Elevator, Box Drop, Float
// Objects and Force Grab.
//
// REAL ITEMS ONLY, NOT EVERY MESH. Collecting every Renderer handed back thousands of static
// meshes in a detailed world -- furniture, walls, decor -- so a feature would grab the ROOM and
// lag the game. What a player calls an "item" has physics or is grabbable: VRC_Pickup (the author
// made it grabbable) and Rigidbody (a loose prop). Far fewer objects, and the right ones.
//
// Three filters are not negotiable, because each one is a way to break the world or someone else:
//   * never something on a PLAYER -- that is an avatar, not a prop;
//   * never anything the mod itself built;
//   * never a thing bigger than a prop: a 30-metre bound is a building.
namespace VRCA::WorldObjects {

    struct Found {
        void* transform = nullptr;
        float dist2 = 0.f;
    };

    // Nearest first, capped at `want`. `networkableOnly` restricts to grabbable pickups nobody is
    // holding: a synced feature must only take what VRChat can actually broadcast, and a plain
    // Rigidbody is not networkable -- moving one would only desync you.
    std::vector<Found> Collect(float const around[3], float range, int want,
                               bool networkableOnly, float maxSize = 30.f);
}
