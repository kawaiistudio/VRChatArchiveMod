#pragma once
#include <cstddef>
#include <string>
#include <vector>

// UNITY OBJECT HELPERS -- the engine-side calls the UI needs.
//
// All of it goes through il2cpp method resolution (VRCA::Il2), so nothing here hard-codes an offset.
// This is what lets the mod clone VRChat's own menu pages: find the tab strip, Instantiate a
// disabled donor page, reparent it, strip the obfuscated controller, set a label. Every call is
// null-tolerant and must run on the GAME THREAD (Unity refuses object work off it).
namespace VRCA::Unity {

    // UNITY'S FAKE NULL.
    //
    // A destroyed or absent UnityEngine.Object is NOT a null pointer: GetComponent/Find hand back a
    // real managed object whose internal native pointer is zero, and C# hides this behind an
    // overloaded ==. In native code `if (!p)` is therefore WRONG and silently true-ish -- that is
    // how an AddComponent fallback never ran and a bogus component got registered for clicks.
    // Every accessor below returns nullptr for such an object; use this for anything you hold.
    [[nodiscard]] bool IsAlive(void* unityObject);

    // GameObject.Find("A/B/C") -- an absolute scene path. null if not found or inactive at a step
    // (Find does not see inactive objects; for those, walk from a found ancestor via ChildByName).
    void* Find(char const* path);

    // get_transform / get_gameObject.
    void* Transform(void* gameObject);
    void* GameObjectOf(void* component);

    // Transform navigation.
    int   ChildCount(void* transform);
    void* ChildAt(void* transform, int i);
    void* Parent(void* transform);
    void* Root(void* transform);
    // Direct child by name (sees inactive children, unlike GameObject.Find). null if none.
    void* ChildByName(void* transform, char const* name);
    // Descendant by a '/'-separated path, inactive-tolerant at every step.
    void* FindChild(void* rootTransform, char const* path);

    // The first descendant called `name`, at any depth, inactive ones included. A fixed PATH
    // breaks the moment VRChat reorders a prefab; a name survives that.
    void* FindDeep(void* rootTransform, char const* name);

    // Names.
    std::string Name(void* unityObject);
    void SetName(void* unityObject, char const* name);

    // Active state.
    bool ActiveSelf(void* gameObject);
    void SetActive(void* gameObject, bool active);

    // Components: by class (resolved name). null if absent. GetComponentInChildren includes inactive.
    void* GetComponent(void* gameObject, char const* classFullName);

    // Every component of a type under this object, inactive ones included. Empty when the class
    // does not exist on this build -- an absent type is a normal answer, not an error.
    std::vector<void*> ComponentsInChildren(void* gameObject, char const* classFullName,
                                            bool includeInactive = true);

    // The enabled flag, resolved on the component's REAL class: Collider and Cloth declare their
    // own rather than inheriting Behaviour's, and asking the parent class hands an icall the wrong
    // object. A component with no such flag reads as enabled and ignores writes.
    bool BehaviourEnabled(void* component);
    void SetBehaviourEnabled(void* component, bool on);

    // A trigger is an interaction zone, not collision -- noclip leaves those alone.
    bool ColliderIsTrigger(void* collider);

    // EVERY component under this object, whatever its class. The way to find something whose
    // class was renamed: enumerate, then judge each one by the methods it actually has.
    std::vector<void*> AllComponentsInChildren(void* gameObject, bool includeInactive = true);

    // The set_text(string) / get_text() a component really has, resolved on its OWN class chain.
    // TMPro is obfuscated on this build, so "is this a text?" cannot be asked by class name.
    void* TextSetterOf(void* component);

    // World position of a Transform. Writing it is how fly moves the player: VRChat's controller
    // fights a velocity push, but it follows the transform.
    bool GetPosition(void* transform, float out3[3]);
    void SetPosition(void* transform, float const in3[3]);
    void* GetComponentInChildren(void* gameObject, char const* classFullName, bool includeInactive = true);

    // Adds a component by class name and returns it. How a cloned VRChat card is made clickable:
    // its own handlers are obfuscated and get stripped, and a plain UI.Button is what the click
    // router can actually see.
    void* AddComponent(void* gameObject, char const* classFullName);

    // Forces a GridLayoutGroup to a fixed number of columns (constraint = FixedColumnCount).
    void SetGridColumns(void* gridGameObject, int columns);

    // Object.Instantiate(original) -- clones a GameObject. Returns the clone, or null.
    void* Instantiate(void* original);
    // Object.Destroy -- for a component or a GameObject.
    void  Destroy(void* object);

    // Transform.SetParent(parent, worldPositionStays=false): reparent a clone under the menu.
    void  SetParent(void* childTransform, void* parentTransform, bool worldPositionStays = false);
    void  SetSiblingIndex(void* transform, int index);

    // Builds a Sprite from encoded image bytes (PNG/JPG). Same route the C# mod used:
    // Texture2D + ImageConversion.LoadImage + Sprite.Create. Returns null if any step is missing.
    void* SpriteFromBytes(unsigned char const* data, size_t len);

    // Assigns a sprite to whatever image component sits on `gameObject` (VRChat uses its own
    // ImageEx subclass, so the setter is resolved on the component's REAL class).
    bool SetImageSprite(void* gameObject, void* sprite);

    // TextMeshProUGUI.text setter (what gives a cloned button real text with a real font).
    void  SetTmpText(void* tmpComponent, char const* utf8);
    // Finds the first TMP text under a root and sets it; true if one was found.
    bool  SetLabel(void* gameObjectRoot, char const* utf8);
}
