#pragma once
#include <cstdint>
#include <string>
#include <vector>

// THE IL2CPP ENGINE, WRITTEN FRESH FOR VRCHAT ARCHIVE (C++).
//
// Pure native: it talks to GameAssembly.dll's exported il2cpp_* API directly and reads il2cpp
// structures by our own measured offsets. It needs neither Il2CppInterop nor the StructFix -- those
// exist only to make a MANAGED plugin work. Everything here runs on real metadata, so a member
// VRChat renamed or removed resolves to null instead of a corrupted pointer, which is the whole
// reason the project is moving off C#.
//
// Exports are obfuscated per build, so Resolve() tries the standard name first (works on a
// non-obfuscated build) and falls back to the validated remap table. Classes and methods are found
// by name in the live metadata; when VRChat renamed the NAME too, the recovery layer (separate)
// re-finds the class by its shape and registers an alias here.
namespace VRCA::Il2 {

    // --- bootstrap -------------------------------------------------------------------------------
    // Finds GameAssembly.dll, resolves the il2cpp_* exports (standard name, then remap), attaches
    // this thread to the runtime. False if GameAssembly is not loaded or its exports cannot be
    // resolved -- the one hard failure that means "not running under il2cpp yet".
    bool Init();
    [[nodiscard]] bool Ready();

    // Attaches the CALLING thread to the il2cpp domain. Any thread that will touch managed objects
    // (the HTTP worker never does; the game thread already is attached) must call this first.
    void AttachThread();

    // --- raw export access -----------------------------------------------------------------------
    // The resolved address of an il2cpp_* export, by its STANDARD name. null if this build does not
    // ship it (e.g. il2cpp_class_get_bitmap on Unity 6). Caches hits and misses.
    [[nodiscard]] void* Export(char const* standardName);

    // --- classes & methods -----------------------------------------------------------------------
    // Looks up a class by "Namespace.Name" (or "Name" with empty namespace) in the live metadata,
    // honouring any alias registered by the recovery layer. null if absent. Namespace-qualified
    // lookups never fall back to a short name in another namespace (that is how SteamVR's Player
    // gets mistaken for VRC.Player).
    [[nodiscard]] void* FindClass(char const* fullName);

    // Registers klass under name, so a later FindClass(name) returns it. Used by recovery to map an
    // obfuscated class back to the name the modules ask for.
    void RegisterAlias(char const* name, void* klass);

    // Every class in an assembly (bare image name, e.g. "Assembly-CSharp"), for the recovery layer
    // that must scan a whole assembly to find a renamed class by its shape.
    [[nodiscard]] std::vector<void*> AssemblyClasses(char const* assemblyBare);

    // The one class in `assemblyBare` declaring a field with this name. Member NAMES often survive
    // obfuscation on this build even when the CLASS name does not, so this finds a renamed type by
    // something it still says about itself. Refuses an ambiguous match: two classes with the same
    // field name is not an identification.
    [[nodiscard]] void* FindClassWithField(char const* assemblyBare, char const* fieldName);

    // A method by class + name + argument count (-1 = any arity). Resolves the class by name first.
    [[nodiscard]] void* FindMethod(char const* className, char const* methodName, int argc = -1);
    [[nodiscard]] void* FindMethodIn(void* klass, char const* methodName, int argc = -1);

    // Invoke through il2cpp_runtime_invoke. `obj` is null for a static. `args` is an array of
    // POINTERS to each argument (value types point at the value, reference types at the pointer).
    // Returns the boxed/ref result, or null. Never throws into il2cpp.
    void* Invoke(void* method, void* obj, void** args);

    // The compiled entry point of a method (MethodInfo::methodPointer, offset 0) -- what a native
    // detour hooks. null if the method was stripped.
    [[nodiscard]] void* MethodPointer(void* method);

    // --- fields ----------------------------------------------------------------------------------
    [[nodiscard]] void* FindField(void* klass, char const* fieldName);
    [[nodiscard]] int   FieldOffset(void* klass, char const* fieldName);   // -1 if absent
    [[nodiscard]] int   FieldOffsetOf(void* field);
    [[nodiscard]] void* FieldClass(void* field);                           // the field's declared type class
    [[nodiscard]] bool  FieldIsStatic(void* field);
    [[nodiscard]] char const* FieldName(void* field);

    // Iterate a class's fields: start with *iter = nullptr, call until it returns null.
    [[nodiscard]] void* NextField(void* klass, void** iter);
    // Iterate a class's methods the same way.
    [[nodiscard]] void* NextMethod(void* klass, void** iter);

    // --- class introspection (by our own measured offsets) ---------------------------------------
    [[nodiscard]] char const* ClassName(void* klass);
    [[nodiscard]] char const* ClassNamespace(void* klass);
    [[nodiscard]] void*       ClassParent(void* klass);

    // True for a struct: a value-type field holds its bytes inline, not a pointer, so reading one
    // as an object reference would hand a garbage address to the next call.
    [[nodiscard]] bool        ClassIsValueType(void* klass);
    [[nodiscard]] int         ClassInstanceSize(void* klass);
    [[nodiscard]] void*       ClassOfObject(void* obj);   // Il2CppObject::klass at offset 0

    // --- method introspection --------------------------------------------------------------------
    [[nodiscard]] char const* MethodName(void* method);
    [[nodiscard]] int         MethodParamCount(void* method);
    [[nodiscard]] void*       MethodParamClass(void* method, int index);
    // The class a method returns -- how an obfuscated getter is identified when its NAME is
    // gone but the SDK type it hands back is not.
    [[nodiscard]] void*       MethodReturnClass(void* method);

    // A static method takes no `this`: calling one with an instance, or an instance one without,
    // corrupts the argument registers.
    [[nodiscard]] bool        MethodIsStatic(void* method);

    // --- strings ---------------------------------------------------------------------------------
    [[nodiscard]] void*       NewString(char const* utf8);
    [[nodiscard]] std::string ReadString(void* il2cppString);   // UTF-16 -> UTF-8, empty if null

    // --- arrays (Il2CppArray: length at +0x18, elements from +0x20) ------------------------------
    [[nodiscard]] int   ArrayLength(void* arr);
    [[nodiscard]] void* ArrayAt(void* arr, int i);
}
