#pragma once

// RECOVERING RENAMED VRCHAT CLASSES.
//
// VRChat renames its own class NAMES every release, so "VRC.Player" is not in the metadata and a
// loose lookup once returned SteamVR's Player instead. The obfuscator does not touch the SHAPE of a
// class (its field types, the mutual references between classes, a method's parameter types), so a
// renamed class is found by its shape and then aliased back to the name the modules use. Each finder
// REFUSES an ambiguous match rather than guessing -- a wrong class would be silently wrong everywhere.
namespace VRCA::Recovery {

    // Finds VRC.Player and VRCPlayer by structure (both hold a VRCPlayerApi, reference each other,
    // and VRCPlayer alone has a static field typed as itself), and aliases them. Then aliases the
    // classes reachable from them by surviving field name (USpeaker, the serializer...) and by method
    // signature (HighlightsFX). Logs each alias. Call once after il2cpp is ready.
    void RecoverCoreClasses();

    // Aliases `il2cppName` to the class of a named field on `owner`, when the build kept the FIELD
    // name but renamed its TYPE ("_USpeaker" on VRC.Player). No-op if the name already resolves.
    bool AliasFromFieldType(char const* il2cppName, char const* owner, char const* fieldName);

    // The one class in `assemblyBare` whose method parameter types match `paramList` (';'-separated
    // class names). Refuses an ambiguous match. Used for HighlightsFX (Renderer;Color;Boolean).
    void* FindClassByMethodParams(char const* assemblyBare, char const* paramList);
}
