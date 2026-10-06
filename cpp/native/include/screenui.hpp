#pragma once
#include "player.hpp"

#include <string>

// DRAWING WITH VRCHAT'S OWN UI.
//
// No ImGui and no D3D hook: everything we draw on screen is a CLONE of a real VRChat UI element,
// parented under the game's always-on HUD canvas and moved by world->screen projection. Cloning is
// what makes it legible -- a TextMeshPro built from scratch has no font asset assigned and renders
// invisible, while a clone inherits the font, material and sizing the game already uses.
//
// Everything here is game-thread only.
namespace VRCA::ScreenUI {

    // Finds the HUD canvas to parent under and a text element to clone. Safe to call every frame;
    // it only does the work once and returns false until the HUD exists (i.e. until in a world).
    bool Ready();

    // A pooled on-screen label. Handles stay valid for the session; releasing hides the element and
    // returns it to the pool rather than destroying it (Instantiate every frame would churn).
    using Label = int;
    constexpr Label kNoLabel = -1;

    Label AcquireLabel();
    void  ReleaseLabel(Label h);
    void  ReleaseAll();

    void SetText(Label h, std::string const& utf8);
    void SetColor(Label h, float r, float g, float b, float a);
    // Screen pixels, origin bottom-left (Unity's own convention for WorldToScreenPoint).
    void SetScreenPos(Label h, float x, float y);
    void SetVisible(Label h, bool on);

    // Camera.main world->screen. `onScreen` is false when the point is behind the camera, which is
    // the case every label must skip rather than draw mirrored at the wrong place.
    Player::Vec3 WorldToScreen(Player::Vec3 world, bool& onScreen);

    // Screen size in pixels.
    void ScreenSize(float& w, float& h);

    // A SHORT-LIVED STATUS PILL AT THE TOP OF THE SCREEN.
    //
    // A card or a client command that REFUSED -- "no player selected", "that is not an avatar id",
    // "the server did not authorise this" -- used to write its reason into a string nobody could
    // see, so every refusal looked like a button that does nothing. This is that line, drawn where
    // the user is looking for a few seconds, then gone.
    //
    // Toast() only stores the string, so it is safe from ANY thread: the bridge applies client
    // commands on its worker. The clock and the drawing happen on the game thread, in Pump().
    void Toast(std::string const& utf8);

    // THE LAST THING THE MOD SAID. The desktop client shows it as the mod's status line, and
    // every refusal and confirmation in the engine already goes through Toast -- so this is the
    // one place that knows it, and the client does not need a second channel to be told twice.
    [[nodiscard]] std::string LastToast();

    // Game thread, once per frame: ages the pill and draws or hides it.
    void PumpToast();
}
