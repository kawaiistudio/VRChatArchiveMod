#include "uidump.hpp"
#include "bridge.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdio>
#include <string>
#include <vector>

namespace VRCA {

namespace {
    FILE* g_out   = nullptr;
    int   g_nodes = 0;

    void* SystemTypeOf(char const* fullName) {
        void* k = Il2::FindClass(fullName);
        if (!k) return nullptr;
        static void* cgt = Il2::Export("il2cpp_class_get_type");
        static void* tgo = Il2::Export("il2cpp_type_get_object");
        if (!cgt || !tgo) return nullptr;
        using CGT = void*(*)(void*); using TGO = void*(*)(void*);
        void* t = reinterpret_cast<CGT>(cgt)(k);
        return t ? reinterpret_cast<TGO>(tgo)(t) : nullptr;
    }

    // Every component on a node, by il2cpp class name -- including VRChat's obfuscated ones, which
    // is exactly what tells us which controller to strip off a clone.
    std::string Components(void* go, std::string& text) {
        static void* getComponents = Il2::FindMethod("UnityEngine.GameObject", "GetComponents", 1);
        static void* compType = SystemTypeOf("UnityEngine.Component");
        static void* tmpGetText = Il2::FindMethod("TMPro.TMP_Text", "get_text", 0);
        if (!getComponents || !compType) return {};

        void* a[1] = { compType };
        void* arr = Il2::Invoke(getComponents, go, a);
        int n = Il2::ArrayLength(arr);
        std::string out;
        for (int i = 0; i < n; ++i) {
            void* c = Il2::ArrayAt(arr, i);
            if (!c) continue;
            char const* cn = Il2::ClassName(Il2::ClassOfObject(c));
            if (!cn) continue;
            if (!out.empty()) out += ",";
            // Obfuscated names are high-byte garbage; show them as a marker plus their length so
            // the tree stays readable while still saying "there is a renamed component here".
            bool obf = false;
            for (char const* p = cn; *p; ++p) if (static_cast<unsigned char>(*p) > 0x7F) { obf = true; break; }
            out += obf ? ("<obf:" + std::to_string(std::string(cn).size()) + ">") : std::string(cn);

            if (text.empty() && tmpGetText &&
                (std::string(cn) == "TextMeshProUGUI" || std::string(cn) == "TMP_Text")) {
                text = Il2::ReadString(Il2::Invoke(tmpGetText, c, nullptr));
            }
        }
        return out;
    }

    void Walk(void* go, int depth) {
        if (!go || g_nodes >= 120000 || depth > 60) return;
        ++g_nodes;

        std::string name = Unity::Name(go);
        std::string text;
        std::string comps = Components(go, text);
        bool active = Unity::ActiveSelf(go);

        std::fprintf(g_out, "%*s%s%s", depth * 2, "", name.empty() ? "(unnamed)" : name.c_str(),
                     active ? "" : " [inactive]");
        if (!comps.empty()) std::fprintf(g_out, "  <%s>", comps.c_str());
        if (!text.empty())  std::fprintf(g_out, "  \"%s\"", text.c_str());
        std::fprintf(g_out, "\n");

        void* tr = Unity::Transform(go);
        if (!tr) return;
        int n = Unity::ChildCount(tr);
        for (int i = 0; i < n; ++i) {
            void* c = Unity::ChildAt(tr, i);
            if (c) Walk(Unity::GameObjectOf(c), depth + 1);
        }
    }
}

UiDumpModule::UiDumpModule()
    : Module("UI Dump", "writes VRChat's full menu tree to a file") {
    m_enabled = true;   // read-only walk; being on costs nothing once the one-shots have run
    // The client's "dump the menu tree" button.
    Actions::Register("dumpUi", [](std::string const&, void* u) {
        static int n = 0;
        char tag[32];
        std::snprintf(tag, sizeof(tag), "client_%d", ++n);
        static_cast<UiDumpModule*>(u)->Dump(tag);
    }, this);
}

int UiDumpModule::Dump(char const* tag) {
    char path[256];
    std::snprintf(path, sizeof(path), "vrca_ui_%s.txt", tag ? tag : "dump");
    if (fopen_s(&g_out, path, "w") != 0 || !g_out) {
        Log::Writef("Warning", "[UIDump] could not write %s", path);
        return 0;
    }
    g_nodes = 0;
    std::fprintf(g_out, "=== arbre UI VRChat (via Resources/Canvas) ===\n");

    // FindObjectsOfTypeAll(Canvas) hands back every Canvas including inactive ones; each one's
    // transform root is the top of a menu tree. Scene roots are unusable here (the scene API boxes
    // a struct and returns nothing on this build).
    static void* findAll = Il2::FindMethod("UnityEngine.Resources", "FindObjectsOfTypeAll", 1);
    void* canvasType = SystemTypeOf("UnityEngine.Canvas");
    if (!findAll || !canvasType) {
        std::fprintf(g_out, "Resources.FindObjectsOfTypeAll or Canvas not found.\n");
        std::fclose(g_out); g_out = nullptr;
        return 0;
    }

    void* a[1] = { canvasType };
    void* arr = Il2::Invoke(findAll, nullptr, a);
    int count = Il2::ArrayLength(arr);
    std::fprintf(g_out, "%d Canvas found\n", count);

    static void* getRoot = Il2::FindMethod("UnityEngine.Transform", "get_root", 0);
    std::vector<void*> seen;
    for (int i = 0; i < count; ++i) {
        void* canvas = Il2::ArrayAt(arr, i);
        if (!canvas) continue;
        void* go = Unity::GameObjectOf(canvas);
        if (!go) continue;
        void* tr = Unity::Transform(go);
        void* root = (tr && getRoot) ? Il2::Invoke(getRoot, tr, nullptr) : nullptr;
        void* rootGo = root ? Unity::GameObjectOf(root) : go;
        if (!rootGo) continue;

        bool dup = false;
        for (void* s : seen) if (s == rootGo) { dup = true; break; }
        if (dup) continue;
        seen.push_back(rootGo);

        std::fprintf(g_out, "\n--- racine : %s ---\n", Unity::Name(rootGo).c_str());
        Walk(rootGo, 0);
    }

    std::fprintf(g_out, "\n=== %d noeud(s) ===\n", g_nodes);
    std::fclose(g_out);
    g_out = nullptr;
    Log::Writef("Info", "[UIDump] %d noeuds -> %s", g_nodes, path);
    return g_nodes;
}

void UiDumpModule::OnUpdate() {
    // F9 DUMPS NOW. The automatic passes only see what is built at that instant, and a menu the
    // player has never opened does not exist yet -- so the useful dump is the one taken while they
    // are standing in the menu they care about. Edge-triggered: held keys must not spam files.
    {
        static bool wasDown = false;
        bool down = (GetAsyncKeyState(VK_F9) & 0x8000) != 0;
        if (down && !wasDown) {
            static int n = 0;
            char tag[32];
            std::snprintf(tag, sizeof(tag), "f9_%d", ++n);
            Dump(tag);
        }
        wasDown = down;
    }

    if (m_left <= 0) return;
    double t = Engine::Time();
    if (m_next == 0.0) { m_next = t + 15.0; return; }   // wait for a world
    if (t < m_next) return;
    --m_left;
    m_next = t + 45.0;                                   // a second pass once menus were opened
    Dump(m_left == 1 ? "1" : "2");
}

}
