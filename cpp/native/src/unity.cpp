#include "unity.hpp"

#include <vector>
#include "il2cpp.hpp"
#include "log.hpp"

#include <cstring>

namespace VRCA::Unity {

namespace {
    using namespace VRCA;

    // Resolve-once method handles. Each is looked up by name the first time it is needed; a null
    // handle just means that call becomes a no-op (the feature degrades, nothing crashes).
    struct M {
        void* goFind = nullptr;
        void* goGetTransform = nullptr, *compGetGameObject = nullptr;
        void* goGetActive = nullptr, *goSetActive = nullptr;
        void* goGetComponent = nullptr, *goGetComponentInChildren = nullptr;
        void* objGetName = nullptr, *objSetName = nullptr;
        void* objInstantiate = nullptr, *objDestroy = nullptr;
        void* trChildCount = nullptr, *trGetChild = nullptr, *trGetParent = nullptr, *trGetRoot = nullptr;
        void* trSetParent = nullptr, *trFind = nullptr, *trSetSibling = nullptr;
        void* tmpSetText = nullptr;
        bool  ready = false;
    };
    M g;

    void Ensure() {
        if (g.ready) return;
        g.ready = true;
        g.goFind              = Il2::FindMethod("UnityEngine.GameObject", "Find", 1);
        g.goGetTransform      = Il2::FindMethod("UnityEngine.GameObject", "get_transform", 0);
        g.compGetGameObject   = Il2::FindMethod("UnityEngine.Component", "get_gameObject", 0);
        g.goGetActive         = Il2::FindMethod("UnityEngine.GameObject", "get_activeSelf", 0);
        g.goSetActive         = Il2::FindMethod("UnityEngine.GameObject", "SetActive", 1);
        g.goGetComponent      = Il2::FindMethod("UnityEngine.GameObject", "GetComponent", 1);
        g.goGetComponentInChildren = Il2::FindMethod("UnityEngine.GameObject", "GetComponentInChildren", 2);
        g.objGetName          = Il2::FindMethod("UnityEngine.Object", "get_name", 0);
        g.objSetName          = Il2::FindMethod("UnityEngine.Object", "set_name", 1);
        g.objInstantiate      = Il2::FindMethod("UnityEngine.Object", "Instantiate", 1);
        g.objDestroy          = Il2::FindMethod("UnityEngine.Object", "Destroy", 1);
        g.trChildCount        = Il2::FindMethod("UnityEngine.Transform", "get_childCount", 0);
        g.trGetChild          = Il2::FindMethod("UnityEngine.Transform", "GetChild", 1);
        g.trGetParent         = Il2::FindMethod("UnityEngine.Transform", "get_parent", 0);
        g.trGetRoot           = Il2::FindMethod("UnityEngine.Transform", "get_root", 0);
        g.trSetParent         = Il2::FindMethod("UnityEngine.Transform", "SetParent", 2);
        g.trFind              = Il2::FindMethod("UnityEngine.Transform", "Find", 1);
        g.trSetSibling        = Il2::FindMethod("UnityEngine.Transform", "SetSiblingIndex", 1);
        g.tmpSetText          = Il2::FindMethod("TMPro.TMP_Text", "set_text", 1);
        if (!g.tmpSetText) g.tmpSetText = Il2::FindMethod("TMPro.TextMeshProUGUI", "set_text", 1);
    }

    // System.Type object for a class full name, for the reflection-taking GetComponent overloads.
    void* SystemType(char const* fullName) {
        void* k = Il2::FindClass(fullName);
        if (!k) return nullptr;
        static void* classGetType = Il2::Export("il2cpp_class_get_type");
        static void* typeGetObject = Il2::Export("il2cpp_type_get_object");
        if (!classGetType || !typeGetObject) return nullptr;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* t = reinterpret_cast<CGT>(classGetType)(k);
        return t ? reinterpret_cast<TGO>(typeGetObject)(t) : nullptr;
    }
}

bool IsAlive(void* o) {
    // UnityEngine.Object::m_CachedPtr sits at 0x10 (after the il2cpp object header); zero means the
    // native side is gone, which is exactly what C#'s == null reports.
    return o && *reinterpret_cast<void**>(static_cast<char*>(o) + 0x10) != nullptr;
}

// Returns nullptr for Unity's fake null, so ordinary null checks work on everything we hand out.
static void* Live(void* o) { return IsAlive(o) ? o : nullptr; }

void* Find(char const* path) {
    Ensure();
    if (!g.goFind || !path) return nullptr;
    void* s = Il2::NewString(path);
    void* a[1] = { s };
    return Live(Il2::Invoke(g.goFind, nullptr, a));
}

void* Transform(void* go) {
    Ensure();
    return (go && g.goGetTransform) ? Live(Il2::Invoke(g.goGetTransform, go, nullptr)) : nullptr;
}
void* GameObjectOf(void* comp) {
    Ensure();
    return (comp && g.compGetGameObject) ? Live(Il2::Invoke(g.compGetGameObject, comp, nullptr)) : nullptr;
}

int ChildCount(void* tr) {
    Ensure();
    if (!tr || !g.trChildCount) return 0;
    void* r = Il2::Invoke(g.trChildCount, tr, nullptr);
    return r ? *reinterpret_cast<int*>(static_cast<char*>(r) + 0x10) : 0;   // boxed int
}
void* ChildAt(void* tr, int i) {
    Ensure();
    if (!tr || !g.trGetChild) return nullptr;
    int idx = i; void* a[1] = { &idx };
    return Live(Il2::Invoke(g.trGetChild, tr, a));
}
void* Parent(void* tr) {
    Ensure();
    return (tr && g.trGetParent) ? Live(Il2::Invoke(g.trGetParent, tr, nullptr)) : nullptr;
}
void* Root(void* tr) {
    Ensure();
    return (tr && g.trGetRoot) ? Live(Il2::Invoke(g.trGetRoot, tr, nullptr)) : nullptr;
}

std::string Name(void* obj) {
    Ensure();
    if (!obj || !g.objGetName) return {};
    return Il2::ReadString(Il2::Invoke(g.objGetName, obj, nullptr));
}
void SetName(void* obj, char const* name) {
    Ensure();
    if (!obj || !g.objSetName || !name) return;
    void* s = Il2::NewString(name); void* a[1] = { s };
    Il2::Invoke(g.objSetName, obj, a);
}

void* ChildByName(void* tr, char const* name) {
    if (!tr || !name) return nullptr;
    int n = ChildCount(tr);
    for (int i = 0; i < n; ++i) {
        void* c = ChildAt(tr, i);
        if (!c) continue;
        void* cgo = GameObjectOf(c);
        if (cgo && Name(cgo) == name) return c;   // returns the child's TRANSFORM
    }
    return nullptr;
}

void* FindChild(void* rootTr, char const* path) {
    if (!rootTr || !path) return nullptr;
    std::string p(path);
    void* cur = rootTr;
    size_t start = 0;
    while (cur && start <= p.size()) {
        size_t slash = p.find('/', start);
        std::string seg = p.substr(start, slash == std::string::npos ? std::string::npos : slash - start);
        if (!seg.empty()) cur = ChildByName(cur, seg.c_str());
        if (slash == std::string::npos) break;
        start = slash + 1;
    }
    return cur;
}

bool ActiveSelf(void* go) {
    Ensure();
    if (!go || !g.goGetActive) return false;
    void* r = Il2::Invoke(g.goGetActive, go, nullptr);
    return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10);
}
void SetActive(void* go, bool active) {
    Ensure();
    if (!go || !g.goSetActive) return;
    bool v = active; void* a[1] = { &v };
    Il2::Invoke(g.goSetActive, go, a);
}

void* GetComponent(void* go, char const* classFullName) {
    Ensure();
    if (!go || !g.goGetComponent) return nullptr;
    void* type = SystemType(classFullName);
    if (!type) return nullptr;
    void* a[1] = { type };
    return Live(Il2::Invoke(g.goGetComponent, go, a));
}
void* GetComponentInChildren(void* go, char const* classFullName, bool includeInactive) {
    Ensure();
    if (!go || !g.goGetComponentInChildren) return nullptr;
    void* type = SystemType(classFullName);
    if (!type) return nullptr;
    bool inc = includeInactive; void* a[2] = { type, &inc };
    return Live(Il2::Invoke(g.goGetComponentInChildren, go, a));
}

void* AddComponent(void* go, char const* classFullName) {
    Ensure();
    // Unity exposes AddComponent(Type) publicly, but IL2CPP builds often only keep the internal
    // Internal_AddComponentWithType -- so try both and say which worked, once.
    static void* add = [] {
        void* m = Il2::FindMethod("UnityEngine.GameObject", "AddComponent", 1);
        if (!m) m = Il2::FindMethod("UnityEngine.GameObject", "Internal_AddComponentWithType", 1);
        VRCA::Log::Writef(m ? "Info" : "Warning", "[Unity] AddComponent : %s",
                          m ? "methode resolue" : "NO AddComponent method found");
        return m;
    }();
    if (!go || !add) return nullptr;
    void* type = SystemType(classFullName);
    if (!type) {
        VRCA::Log::Writef("Warning", "[Unity] AddComponent : type '%s' non resolu.", classFullName);
        return nullptr;
    }
    void* a[1] = { type };
    void* comp = Live(Il2::Invoke(add, go, a));
    if (!comp) VRCA::Log::Writef("Warning", "[Unity] AddComponent('%s') a rendu null.", classFullName);
    return comp;
}

void SetGridColumns(void* gridGo, int columns) {
    Ensure();
    void* grid = GetComponent(gridGo, "UnityEngine.UI.GridLayoutGroup");
    if (!grid) return;
    static void* setConstraint = Il2::FindMethod("UnityEngine.UI.GridLayoutGroup", "set_constraint", 1);
    static void* setCount      = Il2::FindMethod("UnityEngine.UI.GridLayoutGroup", "set_constraintCount", 1);
    if (setConstraint) { int c = 1; void* a[1] = { &c }; Il2::Invoke(setConstraint, grid, a); } // FixedColumnCount
    if (setCount)      { int n = columns; void* a[1] = { &n }; Il2::Invoke(setCount, grid, a); }
}

void* Instantiate(void* original) {
    Ensure();
    if (!original || !g.objInstantiate) return nullptr;
    void* a[1] = { original };
    return Live(Il2::Invoke(g.objInstantiate, nullptr, a));
}
void Destroy(void* object) {
    Ensure();
    if (!object || !g.objDestroy) return;
    void* a[1] = { object };
    Il2::Invoke(g.objDestroy, nullptr, a);
}

void SetParent(void* childTr, void* parentTr, bool worldPositionStays) {
    Ensure();
    if (!childTr || !g.trSetParent) return;
    bool w = worldPositionStays; void* a[2] = { parentTr, &w };
    Il2::Invoke(g.trSetParent, childTr, a);
}
void SetSiblingIndex(void* tr, int index) {
    Ensure();
    if (!tr || !g.trSetSibling) return;
    int i = index; void* a[1] = { &i };
    Il2::Invoke(g.trSetSibling, tr, a);
}

void* SpriteFromBytes(unsigned char const* data, size_t len) {
    Ensure();
    if (!data || !len) return nullptr;
    static void* texCtor   = Il2::FindMethod("UnityEngine.Texture2D", ".ctor", 2);
    static void* loadImage = Il2::FindMethod("UnityEngine.ImageConversion", "LoadImage", 2);
    static void* spriteCreate = Il2::FindMethod("UnityEngine.Sprite", "Create", 3);
    static void* texW = Il2::FindMethod("UnityEngine.Texture", "get_width", 0);
    static void* texH = Il2::FindMethod("UnityEngine.Texture", "get_height", 0);
    static void* objNew = Il2::Export("il2cpp_object_new");
    static void* arrNew = Il2::Export("il2cpp_array_new");
    if (!texCtor || !loadImage || !spriteCreate || !objNew || !arrNew) return nullptr;

    void* texClass = Il2::FindClass("UnityEngine.Texture2D");
    void* byteClass = Il2::FindClass("System.Byte");
    if (!texClass || !byteClass) return nullptr;

    using ObjNew = void*(*)(void*);
    using ArrNew = void*(*)(void*, size_t);
    void* tex = reinterpret_cast<ObjNew>(objNew)(texClass);
    if (!tex) return nullptr;
    int w = 2, h = 2;
    void* ctorArgs[2] = { &w, &h };
    Il2::Invoke(texCtor, tex, ctorArgs);     // size is replaced by LoadImage

    void* arr = reinterpret_cast<ArrNew>(arrNew)(byteClass, len);
    if (!arr) return nullptr;
    std::memcpy(static_cast<char*>(arr) + 0x20, data, len);   // Il2CppArray payload

    void* liArgs[2] = { tex, arr };
    Il2::Invoke(loadImage, nullptr, liArgs);                  // static(Texture2D, byte[])

    float rw = 2.f, rh = 2.f;
    if (texW) if (void* r = Il2::Invoke(texW, tex, nullptr)) rw = static_cast<float>(*reinterpret_cast<int*>(static_cast<char*>(r) + 0x10));
    if (texH) if (void* r = Il2::Invoke(texH, tex, nullptr)) rh = static_cast<float>(*reinterpret_cast<int*>(static_cast<char*>(r) + 0x10));

    float rect[4]  = { 0.f, 0.f, rw, rh };
    float pivot[2] = { 0.5f, 0.5f };
    void* spArgs[3] = { tex, rect, pivot };
    return Il2::Invoke(spriteCreate, nullptr, spArgs);
}

bool SetImageSprite(void* go, void* sprite) {
    Ensure();
    if (!go || !sprite) return false;
    // VRChat's image component is its own Image subclass, so resolve set_sprite on the class the
    // component ACTUALLY is, walking up to whichever ancestor declares it.
    static void* getComponents = Il2::FindMethod("UnityEngine.GameObject", "GetComponents", 1);
    void* compType = SystemType("UnityEngine.Component");
    if (!getComponents || !compType) return false;
    void* a[1] = { compType };
    void* comps = Il2::Invoke(getComponents, go, a);
    int n = Il2::ArrayLength(comps);
    for (int i = 0; i < n; ++i) {
        void* comp = Il2::ArrayAt(comps, i);
        if (!comp) continue;
        for (void* k = Il2::ClassOfObject(comp); k; k = Il2::ClassParent(k)) {
            if (void* setter = Il2::FindMethodIn(k, "set_sprite", 1)) {
                void* args[1] = { sprite };
                Il2::Invoke(setter, comp, args);
                return true;
            }
        }
    }
    return false;
}

// THE SETTER COMES FROM THE COMPONENT, NOT FROM A NAME. TMPro is obfuscated on this build, so a
// handle resolved once from "TMPro.TMP_Text" either does not exist or belongs to the wrong class,
// and handing an icall the wrong object is not a harmless no-op.
void SetTmpText(void* tmp, char const* utf8) {
    if (!tmp || !utf8) return;
    void* setter = TextSetterOf(tmp);
    if (!setter) return;
    void* s = Il2::NewString(utf8);
    void* a[1] = { s };
    Il2::Invoke(setter, tmp, a);
}
bool SetLabel(void* root, char const* utf8) {
    // Any component under here that can take text IS the label, whatever its class is called.
    for (void* comp : AllComponentsInChildren(root, true)) {
        if (!IsAlive(comp) || !TextSetterOf(comp)) continue;
        SetTmpText(comp, utf8);
        return true;
    }
    return false;
}

std::vector<void*> ComponentsInChildren(void* gameObject, char const* classFullName,
                                        bool includeInactive) {
    std::vector<void*> out;
    static void* getComps = Il2::FindMethod("UnityEngine.GameObject", "GetComponentsInChildren", 2);
    if (!gameObject || !getComps) return out;
    void* k = Il2::FindClass(classFullName);
    if (!k) return out;
    static void* cgt = Il2::Export("il2cpp_class_get_type");
    static void* tgo = Il2::Export("il2cpp_type_get_object");
    if (!cgt || !tgo) return out;
    using CGT = void*(*)(void*); using TGO = void*(*)(void*);
    void* ty = reinterpret_cast<CGT>(cgt)(k);
    void* sysType = ty ? reinterpret_cast<TGO>(tgo)(ty) : nullptr;
    if (!sysType) return out;

    bool inc = includeInactive;
    void* a[2] = { sysType, &inc };
    void* arr = Il2::Invoke(getComps, gameObject, a);
    int n = Il2::ArrayLength(arr);
    out.reserve(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i)
        if (void* c = Il2::ArrayAt(arr, i)) out.push_back(c);
    return out;
}

bool GetPosition(void* transform, float out3[3]) {
    static void* get = Il2::FindMethod("UnityEngine.Transform", "get_position", 0);
    if (!transform || !get) return false;
    void* boxed = Il2::Invoke(get, transform, nullptr);
    if (!boxed) return false;
    auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
    out3[0] = f[0]; out3[1] = f[1]; out3[2] = f[2];
    return true;
}

void SetPosition(void* transform, float const in3[3]) {
    static void* set = Il2::FindMethod("UnityEngine.Transform", "set_position", 1);
    if (!transform || !set) return;
    float v[3] = { in3[0], in3[1], in3[2] };   // Vector3 is a by-value struct: pass its address
    void* a[1] = { v };
    Il2::Invoke(set, transform, a);
}

bool BehaviourEnabled(void* component) {
    if (!component) return false;
    for (void* k = Il2::ClassOfObject(component); k; k = Il2::ClassParent(k))
        if (void* m = Il2::FindMethodIn(k, "get_enabled", 0)) {
            void* r = Il2::Invoke(m, component, nullptr);
            return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10) != 0;
        }
    return true;
}

void SetBehaviourEnabled(void* component, bool on) {
    if (!component) return;
    for (void* k = Il2::ClassOfObject(component); k; k = Il2::ClassParent(k))
        if (void* m = Il2::FindMethodIn(k, "set_enabled", 1)) {
            bool v = on;
            void* a[1] = { &v };
            Il2::Invoke(m, component, a);
            return;
        }
}

bool ColliderIsTrigger(void* collider) {
    if (!collider) return false;
    for (void* k = Il2::ClassOfObject(collider); k; k = Il2::ClassParent(k))
        if (void* m = Il2::FindMethodIn(k, "get_isTrigger", 0)) {
            void* r = Il2::Invoke(m, collider, nullptr);
            return r && *reinterpret_cast<unsigned char*>(static_cast<char*>(r) + 0x10) != 0;
        }
    return false;
}

std::vector<void*> AllComponentsInChildren(void* gameObject, bool includeInactive) {
    return ComponentsInChildren(gameObject, "UnityEngine.Component", includeInactive);
}

void* TextSetterOf(void* component) {
    if (!component) return nullptr;
    for (void* k = Il2::ClassOfObject(component); k; k = Il2::ClassParent(k))
        if (void* m = Il2::FindMethodIn(k, "set_text", 1)) return m;
    return nullptr;
}

void* FindDeep(void* rootTransform, char const* name) {
    if (!rootTransform || !name) return nullptr;
    // Breadth-first: a card's icon is a near child, and a depth-first walk would wander into a
    // deep sub-tree before finding it.
    std::vector<void*> level{ rootTransform };
    for (int depth = 0; depth < 8 && !level.empty(); ++depth) {
        std::vector<void*> next;
        for (void* t : level) {
            int n = ChildCount(t);
            for (int i = 0; i < n; ++i) {
                void* ch = ChildAt(t, i);
                if (!IsAlive(ch)) continue;
                if (Name(GameObjectOf(ch)) == name) return ch;
                next.push_back(ch);
            }
        }
        level.swap(next);
    }
    return nullptr;
}

}
