#include "screenui.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#include <vector>
#include <mutex>

namespace VRCA::ScreenUI {

namespace {
    constexpr char const* kHudPath = "UnscaledUI/HudContent";

    void* g_host  = nullptr;   // the HUD canvas transform we parent under
    void* g_donor = nullptr;   // a real TMP text GameObject to clone
    bool  g_ready = false;
    bool  g_warned = false;

    struct Slot {
        void* go   = nullptr;   // the cloned GameObject
        void* tmp  = nullptr;   // its TMP component
        void* rect = nullptr;   // its RectTransform
        bool  used = false;
    };
    std::vector<Slot> g_pool;

    struct M {
        void* setAnchored = nullptr, *setAnchorMin = nullptr, *setAnchorMax = nullptr, *setPivot = nullptr;
        void* setColor = nullptr;
        void* camMain = nullptr, *w2s = nullptr;
        void* screenW = nullptr, *screenH = nullptr;
        bool  ready = false;
    };
    M m;

    void EnsureMethods() {
        if (m.ready) return;
        m.ready = true;
        m.setAnchored  = Il2::FindMethod("UnityEngine.RectTransform", "set_anchoredPosition", 1);
        m.setAnchorMin = Il2::FindMethod("UnityEngine.RectTransform", "set_anchorMin", 1);
        m.setAnchorMax = Il2::FindMethod("UnityEngine.RectTransform", "set_anchorMax", 1);
        m.setPivot     = Il2::FindMethod("UnityEngine.RectTransform", "set_pivot", 1);
        m.setColor     = Il2::FindMethod("TMPro.TMP_Text", "set_color", 1);
        if (!m.setColor) m.setColor = Il2::FindMethod("UnityEngine.UI.Graphic", "set_color", 1);
        m.camMain = Il2::FindMethod("UnityEngine.Camera", "get_main", 0);
        m.w2s     = Il2::FindMethod("UnityEngine.Camera", "WorldToScreenPoint", 1);
        m.screenW = Il2::FindMethod("UnityEngine.Screen", "get_width", 0);
        m.screenH = Il2::FindMethod("UnityEngine.Screen", "get_height", 0);
    }

    // Finds the always-on HUD canvas (the one carrying the reticle) and any TMP under it to clone.
    bool Discover() {
        void* ui = Unity::Find("UserInterface");
        if (!ui) return false;
        void* hudTr = Unity::FindChild(Unity::Transform(ui), kHudPath);
        if (!hudTr) return false;

        void* hudGo = Unity::GameObjectOf(hudTr);
        void* canvasTr = Unity::ChildByName(hudTr, "Canvas");
        g_host = canvasTr ? canvasTr : hudTr;

        // ANY TEXT WILL DO -- but it cannot be asked for by name.
        //
        // TMPro is OBFUSCATED on this build, so GetComponentInChildren("TMPro.TextMeshProUGUI")
        // finds nothing and the whole on-screen layer stayed dark. A text is identified the way
        // every other renamed member is: by what it CAN DO. A component whose own class chain
        // declares set_text(string) is a text, whatever the build decided to call it.
        //
        // We only want its font and material, and a clone keeps both -- text built from scratch
        // has no font asset assigned and renders invisible, which is the trap this avoids. So the
        // donor does NOT have to live under the HUD: the HUD is searched first because its text is
        // sized for the screen, but any text in the interface will clone just as well, and the
        // clone is reparented under the HUD either way.
        auto findText = [](void* root) -> void* {
            if (!root) return nullptr;
            for (void* comp : Unity::AllComponentsInChildren(root, true)) {
                if (!Unity::IsAlive(comp)) continue;
                if (!Unity::TextSetterOf(comp)) continue;
                return comp;
            }
            return nullptr;
        };

        void* tmp = findText(hudGo);
        char const* where = "HUD";
        if (!tmp) { tmp = findText(ui); where = "UserInterface"; }
        if (!tmp) {
            // Said once per attempt bracket, with the size of what was searched: "found nothing"
            // and "searched nothing" are different failures and must not read the same.
            size_t n = Unity::AllComponentsInChildren(hudGo, true).size();
            if (!g_warned) {
                g_warned = true;
                Log::Writef("Warning",
                            "[ScreenUI] no text component to clone (%zu component(s) under the HUD).",
                            n);
            }
            return false;
        }
        g_donor = Unity::GameObjectOf(tmp);
        Log::Writef("Info", "[ScreenUI] donor text: '%s' (class '%s', found under %s).",
                    Unity::Name(g_donor).c_str(),
                    Il2::ClassName(Il2::ClassOfObject(tmp)), where);
        return g_host && g_donor;
    }

    void SetVec2(void* method, void* rect, float x, float y) {
        if (!method || !rect) return;
        float v[2] = { x, y };
        void* a[1] = { v };
        Il2::Invoke(method, rect, a);
    }

    Slot* Get(Label h) {
        if (h < 0 || h >= static_cast<Label>(g_pool.size())) return nullptr;
        return &g_pool[static_cast<size_t>(h)];
    }
}

bool Ready() {
    if (g_ready) return true;
    EnsureMethods();
    g_ready = Discover();
    if (g_ready) Log::Info("[ScreenUI] HUD found, cloneable donor text -- clone drawing ready.");
    return g_ready;
}

Label AcquireLabel() {
    if (!Ready()) return kNoLabel;
    for (size_t i = 0; i < g_pool.size(); ++i)
        if (!g_pool[i].used) { g_pool[i].used = true; Unity::SetActive(g_pool[i].go, true); return static_cast<Label>(i); }

    void* clone = Unity::Instantiate(g_donor);
    if (!clone) return kNoLabel;
    Unity::SetName(clone, "VRCA_Label");
    Unity::SetParent(Unity::Transform(clone), g_host, false);

    Slot s;
    s.go   = clone;
    // The clone's text component, found the same way the donor was: by shape, not by name.
    for (void* comp : Unity::AllComponentsInChildren(clone, true)) {
        if (!Unity::IsAlive(comp) || !Unity::TextSetterOf(comp)) continue;
        s.tmp = comp;
        break;
    }
    s.rect = Unity::GetComponent(clone, "UnityEngine.RectTransform");
    s.used = true;

    // Anchor bottom-left and pivot centre, so anchoredPosition IS the screen pixel we compute.
    SetVec2(m.setAnchorMin, s.rect, 0.f, 0.f);
    SetVec2(m.setAnchorMax, s.rect, 0.f, 0.f);
    SetVec2(m.setPivot,     s.rect, 0.5f, 0.5f);
    Unity::SetActive(clone, true);

    g_pool.push_back(s);
    return static_cast<Label>(g_pool.size() - 1);
}

void ReleaseLabel(Label h) {
    if (Slot* s = Get(h)) { s->used = false; Unity::SetActive(s->go, false); }
}
void ReleaseAll() {
    for (auto& s : g_pool) { s.used = false; Unity::SetActive(s.go, false); }
}

void SetText(Label h, std::string const& utf8) {
    if (Slot* s = Get(h)) Unity::SetTmpText(s->tmp, utf8.c_str());
}

void SetColor(Label h, float r, float g, float b, float a) {
    Slot* s = Get(h);
    if (!s || !s->tmp) return;
    // Resolved on the component's OWN class chain: the cached TMP_Text/Graphic handle belongs to
    // a class this build renamed, and handing an icall the wrong object is not a no-op.
    void* setter = nullptr;
    for (void* k = Il2::ClassOfObject(s->tmp); k; k = Il2::ClassParent(k))
        if ((setter = Il2::FindMethodIn(k, "set_color", 1)) != nullptr) break;
    if (!setter) return;
    float c[4] = { r, g, b, a };
    void* args[1] = { c };
    Il2::Invoke(setter, s->tmp, args);
}

void SetScreenPos(Label h, float x, float y) {
    if (Slot* s = Get(h)) SetVec2(m.setAnchored, s->rect, x, y);
}

void SetVisible(Label h, bool on) {
    if (Slot* s = Get(h)) Unity::SetActive(s->go, on);
}

Player::Vec3 WorldToScreen(Player::Vec3 world, bool& onScreen) {
    EnsureMethods();
    Player::Vec3 out;
    onScreen = false;
    if (!m.camMain || !m.w2s) return out;
    void* cam = Il2::Invoke(m.camMain, nullptr, nullptr);
    if (!cam) return out;
    float w[3] = { world.x, world.y, world.z };
    void* a[1] = { w };
    void* boxed = Il2::Invoke(m.w2s, cam, a);
    if (!boxed) return out;
    auto const* f = reinterpret_cast<float const*>(static_cast<char*>(boxed) + 0x10);
    out = { f[0], f[1], f[2] };
    onScreen = out.z > 0.001f;   // z <= 0 means behind the camera: drawing it would mirror it
    return out;
}

void ScreenSize(float& w, float& h) {
    EnsureMethods();
    w = h = 0.f;
    if (m.screenW) if (void* r = Il2::Invoke(m.screenW, nullptr, nullptr))
        w = static_cast<float>(*reinterpret_cast<int*>(static_cast<char*>(r) + 0x10));
    if (m.screenH) if (void* r = Il2::Invoke(m.screenH, nullptr, nullptr))
        h = static_cast<float>(*reinterpret_cast<int*>(static_cast<char*>(r) + 0x10));
}

// --------------------------------------------------------------------------------- the toast
namespace {
    constexpr double kToastLife = 3.0;      // seconds

    std::mutex  g_toastLock;
    std::string g_toastMsg;
    bool        g_toastFresh = false;
    double      g_toastShownAt = 0.0;
    Label       g_toastLabel = kNoLabel;
}

std::string LastToast() {
    std::lock_guard lock(g_toastLock);
    return g_toastMsg;
}

void Toast(std::string const& utf8) {
    if (utf8.empty()) return;
    std::lock_guard lock(g_toastLock);
    g_toastMsg = utf8;
    g_toastFresh = true;
}

void PumpToast() {
    std::string msg;
    bool fresh;
    {
        std::lock_guard lock(g_toastLock);
        msg = g_toastMsg;
        fresh = g_toastFresh;
        g_toastFresh = false;
    }
    if (msg.empty()) {
        if (g_toastLabel != kNoLabel) { ReleaseLabel(g_toastLabel); g_toastLabel = kNoLabel; }
        return;
    }
    if (!Ready()) return;

    double now = Engine::Time();
    if (fresh) g_toastShownAt = now;
    if (now - g_toastShownAt > kToastLife) {
        // Expired -- unless a newer Toast() landed since we copied the string out.
        std::lock_guard lock(g_toastLock);
        if (!g_toastFresh) g_toastMsg.clear();
        if (g_toastLabel != kNoLabel) { ReleaseLabel(g_toastLabel); g_toastLabel = kNoLabel; }
        return;
    }

    if (g_toastLabel == kNoLabel) g_toastLabel = AcquireLabel();
    if (g_toastLabel == kNoLabel) return;

    float w = 1920.f, h = 1080.f;
    ScreenSize(w, h);
    SetText(g_toastLabel, msg);
    SetColor(g_toastLabel, 1.f, 0.78f, 0.90f, 1.f);     // the mod's pink, so it reads as ours
    SetScreenPos(g_toastLabel, w * 0.5f, h * 0.88f);
    SetVisible(g_toastLabel, true);
}

}
