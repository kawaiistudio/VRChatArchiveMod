#include "wings.hpp"
#include "bridge.hpp"
#include "clicks.hpp"
#include "config.hpp"
#include "engine.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "panel_assets.hpp"
#include "player.hpp"
#include "unity.hpp"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// A FAITHFUL PORT OF THE C# PanelSkin, value for value.
//
// This is not a redesign: every colour, offset, sprite and font tweak below is taken straight from
// Core/PanelSkin.cs. An earlier native attempt invented numbers (zebra rows, guessed sizes) and
// came out wrong; nothing here is invented. Where the C# uses a UnityEngine call, the same call is
// made natively through the il2cpp layer.
namespace VRCA {

namespace {
    // ---------------------------------------------------------------- palette (PanelSkin)
    struct Rgba { float r, g, b, a; };
    constexpr Rgba RGB(int hex, float a = 1.f) {
        return { ((hex >> 16) & 0xFF) / 255.f, ((hex >> 8) & 0xFF) / 255.f, (hex & 0xFF) / 255.f, a };
    }
    const Rgba kBody   = RGB(0x0B0B14, 0.90f);
    const Rgba kHead   = RGB(0x161323, 0.95f);
    const Rgba kPink   = RGB(0xFF6AD5);
    const Rgba kViolet = RGB(0x8143E6);
    const Rgba kHair   = { 1.f, 1.f, 1.f, 0.06f };
    const Rgba kRule   = { 1.f, 1.f, 1.f, 0.10f };

    constexpr char const* kHexText = "F2F5FC";
    constexpr char const* kHexDim  = "A9BAD4";
    constexpr char const* kHexPink = "FF6AD5";

    // ---------------------------------------------------------------- geometry (PanelSkin)
    constexpr float kWidth   = 480.f;
    constexpr float kHeight  = 900.f;
    constexpr int   kCorner  = 22;
    constexpr int   kTex     = 64;
    constexpr float kRadius  = 14.f;
    constexpr float kHeaderH = 46.f;
    constexpr float kBleed   = 18.f;
    constexpr float kRowH    = 32.f;
    constexpr float kColHeadH = 26.f;
    constexpr float kPadX    = 12.f;
    constexpr float kColId   = 50.f;
    constexpr float kColPos  = 104.f;
    constexpr float kColBadge = 126.f;
    constexpr float kColGap  = 6.f;
    constexpr float kWingWidth = 420.f;
    constexpr float kGap     = 16.f;
    constexpr float kTopY    = -146.f;
    constexpr float kVeil    = 0.86f;
    constexpr float kSizeTitle = 22.f;
    constexpr float kSizeCount = 19.f;
    constexpr float kSizeRow = 19.f;
    constexpr float kSizeHead = 15.f;
    constexpr float kRimW    = 2.5f;
    constexpr float kHalo    = 0.50f;
    constexpr int   kMaxRows = 24;

    // TMP TextAlignmentOptions: TopLeft = 257, TopRight = 260. Cells are rect-positioned, so
    // left/right is all that is needed.
    constexpr int kAlignLeft  = 257;
    constexpr int kAlignRight = 260;

    WingsModule::Selection g_selection;

    // ---------------------------------------------------------------- RectTransform setters
    void SetF2(char const* cls, char const* setter, void* rect, float x, float y) {
        void* m = Il2::FindMethod(cls, setter, 1);
        if (!rect || !m) return;
        float v[2] = { x, y }; void* a[1] = { v };
        Il2::Invoke(m, rect, a);
    }
    void RMin(void* r, float x, float y) { SetF2("UnityEngine.RectTransform", "set_anchorMin", r, x, y); }
    void RMax(void* r, float x, float y) { SetF2("UnityEngine.RectTransform", "set_anchorMax", r, x, y); }
    void RPiv(void* r, float x, float y) { SetF2("UnityEngine.RectTransform", "set_pivot", r, x, y); }
    void RSize(void* r, float x, float y){ SetF2("UnityEngine.RectTransform", "set_sizeDelta", r, x, y); }
    void RPos(void* r, float x, float y) { SetF2("UnityEngine.RectTransform", "set_anchoredPosition", r, x, y); }
    void ROffMin(void* r, float x, float y){ SetF2("UnityEngine.RectTransform", "set_offsetMin", r, x, y); }
    void ROffMax(void* r, float x, float y){ SetF2("UnityEngine.RectTransform", "set_offsetMax", r, x, y); }

    void Stretch(void* r, float outset) {
        RMin(r, 0.f, 0.f); RMax(r, 1.f, 1.f);
        ROffMin(r, outset, outset); ROffMax(r, -outset, -outset);
    }

    void ResetXform(void* tr) {
        if (void* m = Il2::FindMethod("UnityEngine.Transform", "set_localScale", 1)) {
            float one[3] = { 1.f, 1.f, 1.f }; void* b[1] = { one }; Il2::Invoke(m, tr, b);
        }
        if (void* m = Il2::FindMethod("UnityEngine.Transform", "set_localRotation", 1)) {
            float id[4] = { 0.f, 0.f, 0.f, 1.f }; void* b[1] = { id }; Il2::Invoke(m, tr, b);
        }
    }

    // new GameObject("name") + RectTransform under parent, scale/rotation reset. The menu's
    // hierarchy carries a negative X scale, so an inherited one draws mirrored -- exactly what
    // PanelSkin.Rect() resets on every object.
    void* Rect(char const* name, void* parentTr) {
        static void* newObj = Il2::Export("il2cpp_object_new");
        static void* ctor   = Il2::FindMethod("UnityEngine.GameObject", ".ctor", 1);
        static void* newStr = Il2::Export("il2cpp_string_new");
        void* goClass = Il2::FindClass("UnityEngine.GameObject");
        if (!newObj || !ctor || !newStr || !goClass) return nullptr;
        using NO = void*(*)(void*); using NS = void*(*)(char const*);
        void* go = reinterpret_cast<NO>(newObj)(goClass);
        void* nm = reinterpret_cast<NS>(newStr)(name);
        void* a[1] = { nm };
        Il2::Invoke(ctor, go, a);
        void* rect = Unity::AddComponent(go, "UnityEngine.RectTransform");
        if (!Unity::IsAlive(rect)) return nullptr;
        Unity::SetParent(rect, parentTr, false);
        ResetXform(rect);
        return go;
    }

    void SetImgColor(void* img, Rgba c) {
        if (!img) return;
        for (void* k = Il2::ClassOfObject(img); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, "set_color", 1)) {
                float v[4] = { c.r, c.g, c.b, c.a }; void* a[1] = { v };
                Il2::Invoke(m, img, a); return;
            }
    }
    void SetRaycast(void* img, bool on) {
        if (!img) return;
        for (void* k = Il2::ClassOfObject(img); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, "set_raycastTarget", 1)) {
                bool v = on; void* a[1] = { &v }; Il2::Invoke(m, img, a); return;
            }
    }
    void* Img(void* go, void* sprite, Rgba c) {
        void* img = Unity::AddComponent(go, "UnityEngine.UI.Image");
        if (sprite) Unity::SetImageSprite(go, sprite);
        SetImgColor(img, c);
        SetRaycast(img, false);
        return img;
    }
    void SetSliced(void* img) {
        if (!img) return;
        if (void* m = Il2::FindMethod("UnityEngine.UI.Image", "set_type", 1)) {
            int sliced = 1; void* a[1] = { &sliced }; Il2::Invoke(m, img, a);
        }
    }

    // ---------------------------------------------------------------- generated sprites
    float Clamp01(float v) { return v < 0.f ? 0.f : (v > 1.f ? 1.f : v); }
    float RoundDist(int x, int y, int w, int h, float radius, float inset) {
        float dx = (float)((x < w - 1 - x) ? x : (w - 1 - x)) - inset;
        float dy = (float)((y < h - 1 - y) ? y : (h - 1 - y)) - inset;
        if (dx >= radius || dy >= radius) return dx < dy ? dx : dy;
        float ox = radius - dx, oy = radius - dy;
        return radius - std::sqrt(ox * ox + oy * oy);
    }
    void* MakeTexture(int w, int h, std::vector<unsigned char> const& rgba) {
        static void* ctor   = Il2::FindMethod("UnityEngine.Texture2D", ".ctor", 4);
        static void* load   = Il2::FindMethod("UnityEngine.Texture2D", "LoadRawTextureData", 1);
        static void* apply  = Il2::FindMethod("UnityEngine.Texture2D", "Apply", 2);
        static void* newObj = Il2::Export("il2cpp_object_new");
        static void* arrNew = Il2::Export("il2cpp_array_new");
        void* texClass  = Il2::FindClass("UnityEngine.Texture2D");
        void* byteClass = Il2::FindClass("System.Byte");
        if (!ctor || !load || !apply || !newObj || !arrNew || !texClass || !byteClass) return nullptr;
        using NO = void*(*)(void*); using AN = void*(*)(void*, unsigned long long);
        void* tex = reinterpret_cast<NO>(newObj)(texClass);
        int iw = w, ih = h, fmt = 4; bool mips = false;
        void* ca[4] = { &iw, &ih, &fmt, &mips };
        Il2::Invoke(ctor, tex, ca);
        void* arr = reinterpret_cast<AN>(arrNew)(byteClass, rgba.size());
        if (!arr) return nullptr;
        std::memcpy(static_cast<char*>(arr) + 0x20, rgba.data(), rgba.size());
        void* la[1] = { arr };
        Il2::Invoke(load, tex, la);
        bool a1 = false, a2 = false; void* aa[2] = { &a1, &a2 };
        Il2::Invoke(apply, tex, aa);
        return tex;
    }
    void* MakeSprite(void* tex, int w, int h, float border) {
        static void* create = Il2::FindMethod("UnityEngine.Sprite", "Create", 7);
        if (!tex || !create) return nullptr;
        float rect[4] = { 0.f, 0.f, (float)w, (float)h };
        float pivot[2] = { 0.5f, 0.5f };
        float ppu = 100.f; unsigned int extrude = 0; int meshType = 1;
        float bord[4] = { border, border, border, border };
        void* a[7] = { tex, rect, pivot, &ppu, &extrude, &meshType, bord };
        return Il2::Invoke(create, nullptr, a);
    }
    void* RoundSprite() {
        static void* c = nullptr;
        if (c) return c;
        std::vector<unsigned char> px((size_t)kTex * kTex * 4);
        for (int y = 0; y < kTex; ++y) for (int x = 0; x < kTex; ++x) {
            float d = RoundDist(x, y, kTex, kTex, (float)kCorner, 0.f);
            float a = Clamp01(d + 0.5f);
            size_t o = ((size_t)y * kTex + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = (unsigned char)(a * 255.f + 0.5f);
        }
        c = MakeSprite(MakeTexture(kTex, kTex, px), kTex, kTex, (float)kCorner);
        return c;
    }
    void* DotSprite() {
        static void* c = nullptr;
        if (c) return c;
        const int N = 32;
        std::vector<unsigned char> px((size_t)N * N * 4);
        float r = N * 0.5f - 0.5f;
        for (int y = 0; y < N; ++y) for (int x = 0; x < N; ++x) {
            float dx = x - r, dy = y - r;
            float a = Clamp01(r - std::sqrt(dx * dx + dy * dy) + 0.5f);
            size_t o = ((size_t)y * N + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = (unsigned char)(a * 255.f + 0.5f);
        }
        c = MakeSprite(MakeTexture(N, N, px), N, N, 0.f);
        return c;
    }
    void* GradientSprite() {
        static void* c = nullptr;
        if (c) return c;
        const int W = 64;
        std::vector<unsigned char> px((size_t)W * 2 * 4);
        for (int x = 0; x < W; ++x) {
            float t = x / (float)(W - 1);
            float rr = kPink.r + (kViolet.r - kPink.r) * t;
            float gg = kPink.g + (kViolet.g - kPink.g) * t;
            float bb = kPink.b + (kViolet.b - kPink.b) * t;
            for (int row = 0; row < 2; ++row) {
                size_t o = ((size_t)row * W + x) * 4;
                px[o] = (unsigned char)(rr * 255.f);
                px[o + 1] = (unsigned char)(gg * 255.f);
                px[o + 2] = (unsigned char)(bb * 255.f);
                px[o + 3] = 255;
            }
        }
        c = MakeSprite(MakeTexture(W, 2, px), W, 2, 0.f);
        return c;
    }
    void* BorderSprite(int w, int h) {
        int bw = w + (int)(kBleed * 2.f), bh = h + (int)(kBleed * 2.f);
        std::vector<unsigned char> px((size_t)bw * bh * 4, 0);
        for (int y = 0; y < bh; ++y) for (int x = 0; x < bw; ++x) {
            float d = RoundDist(x, y, bw, bh, kRadius, kBleed);
            float a;
            if (d < 0.f) { float k = 1.f + d / kBleed; if (k < 0.f) k = 0.f; a = k * k * kHalo; }
            else if (d < kRimW) a = 1.f;
            else { a = 1.f - (d - kRimW) / 2.f; if (a < 0.f) a = 0.f; }
            if (a <= 0.002f) continue;
            float k2 = Clamp01((x / (float)bw + (1.f - y / (float)bh)) * 0.5f);
            float rr = kViolet.r + (kPink.r - kViolet.r) * k2;
            float gg = kViolet.g + (kPink.g - kViolet.g) * k2;
            float bb = kViolet.b + (kPink.b - kViolet.b) * k2;
            size_t o = ((size_t)y * bw + x) * 4;
            px[o] = (unsigned char)(rr * 255.f);
            px[o + 1] = (unsigned char)(gg * 255.f);
            px[o + 2] = (unsigned char)(bb * 255.f);
            px[o + 3] = (unsigned char)(Clamp01(a) * 255.f + 0.5f);
        }
        return MakeSprite(MakeTexture(bw, bh, px), bw, bh, 0.f);
    }

    // ---------------------------------------------------------------- font + outline material
    void* g_font = nullptr;
    void* g_outlineMat = nullptr;
    bool  g_matTried = false;

    void StealFont(void* menuRoot) {
        if (g_font) return;
        for (void* comp : Unity::AllComponentsInChildren(menuRoot, true)) {
            if (!Unity::IsAlive(comp) || !Unity::TextSetterOf(comp)) continue;
            for (void* k = Il2::ClassOfObject(comp); k; k = Il2::ClassParent(k))
                if (void* m = Il2::FindMethodIn(k, "get_font", 0)) {
                    g_font = Il2::Invoke(m, comp, nullptr);
                    if (g_font) return;
                }
        }
    }

    // A COPY of the font material with a black outline and a soft dark underlay -- this is what
    // makes the text readable over the wallpaper. A copy, never the font's own shared material,
    // which every text in the menu uses.
    void* OutlineMat() {
        if (g_matTried) return g_outlineMat;
        g_matTried = true;
        if (!g_font) return nullptr;
        void* src = nullptr;
        for (void* k = Il2::ClassOfObject(g_font); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, "get_material", 0)) { src = Il2::Invoke(m, g_font, nullptr); break; }
        if (!src) return nullptr;
        static void* ctor = Il2::FindMethod("UnityEngine.Material", ".ctor", 1);
        static void* newObj = Il2::Export("il2cpp_object_new");
        void* matClass = Il2::FindClass("UnityEngine.Material");
        if (!ctor || !newObj || !matClass) return nullptr;
        using NO = void*(*)(void*);
        void* mat = reinterpret_cast<NO>(newObj)(matClass);
        void* ca[1] = { src };
        Il2::Invoke(ctor, mat, ca);

        static void* newStr = Il2::Export("il2cpp_string_new");
        using NS = void*(*)(char const*);
        auto enableKw = [&](char const* kw) {
            void* m = Il2::FindMethod("UnityEngine.Material", "EnableKeyword", 1);
            if (!m) return;
            void* a[1] = { reinterpret_cast<NS>(newStr)(kw) };
            Il2::Invoke(m, mat, a);
        };
        auto setF = [&](char const* prop, float v) {
            void* m = Il2::FindMethod("UnityEngine.Material", "SetFloat", 2);
            if (!m) return;
            void* a[2] = { reinterpret_cast<NS>(newStr)(prop), &v };
            Il2::Invoke(m, mat, a);
        };
        auto setC = [&](char const* prop, float r, float g, float b, float al) {
            void* m = Il2::FindMethod("UnityEngine.Material", "SetColor", 2);
            if (!m) return;
            float c[4] = { r, g, b, al };
            void* a[2] = { reinterpret_cast<NS>(newStr)(prop), c };
            Il2::Invoke(m, mat, a);
        };
        enableKw("OUTLINE_ON");
        setF("_OutlineWidth", 0.14f);
        setC("_OutlineColor", 0.f, 0.f, 0.f, 1.f);
        enableKw("UNDERLAY_ON");
        setC("_UnderlayColor", 0.f, 0.f, 0.f, 1.f);
        setF("_UnderlaySoftness", 0.20f);
        setF("_UnderlayDilate", 0.50f);
        setF("_UnderlayOffsetX", 0.f);
        setF("_UnderlayOffsetY", 0.f);
        g_outlineMat = mat;
        Log::Info("[Wings] outline material created (crisp text over the image).");
        return mat;
    }

    // A TMP label, by CLONING the donor for its font asset, then styled exactly as PanelSkin.Text
    // styles it: size, no wrap, outline material, left/right alignment.
    void* MakeText(void* donor, void* parentTr, char const* name, float w, float h,
                   float x, float y, float size, int align) {
        if (!donor) return nullptr;
        void* clone = Unity::Instantiate(donor);
        if (!Unity::IsAlive(clone)) return nullptr;
        Unity::SetName(clone, name);
        void* rect = Unity::GetComponent(clone, "UnityEngine.RectTransform");
        if (!Unity::IsAlive(rect)) { Unity::Destroy(clone); return nullptr; }
        Unity::SetParent(rect, parentTr, false);
        RMin(rect, 0.f, 0.5f); RMax(rect, 0.f, 0.5f); RPiv(rect, 0.f, 0.5f);
        RSize(rect, w, h); RPos(rect, x, y);
        ResetXform(rect);
        for (void* comp : Unity::AllComponentsInChildren(clone, true)) {
            if (!Unity::IsAlive(comp) || !Unity::TextSetterOf(comp)) continue;
            void* mat = OutlineMat();
            for (void* k = Il2::ClassOfObject(comp); k; k = Il2::ClassParent(k)) {
                if (mat) if (void* m = Il2::FindMethodIn(k, "set_fontSharedMaterial", 1)) { void* a[1] = { mat }; Il2::Invoke(m, comp, a); }
                if (void* m = Il2::FindMethodIn(k, "set_fontSize", 1)) { float v = size; void* a[1] = { &v }; Il2::Invoke(m, comp, a); }
                if (void* m = Il2::FindMethodIn(k, "set_enableWordWrapping", 1)) { bool v = false; void* a[1] = { &v }; Il2::Invoke(m, comp, a); }
                if (void* m = Il2::FindMethodIn(k, "set_enableAutoSizing", 1)) { bool v = false; void* a[1] = { &v }; Il2::Invoke(m, comp, a); }
                if (void* m = Il2::FindMethodIn(k, "set_alignment", 1)) { int v = align; void* a[1] = { &v }; Il2::Invoke(m, comp, a); }
                if (void* m = Il2::FindMethodIn(k, "set_richText", 1)) { bool v = true; void* a[1] = { &v }; Il2::Invoke(m, comp, a); }
            }
            break;
        }
        Unity::SetActive(clone, true);
        return clone;
    }

    void WriteText(void* textGo, std::string const& v) {
        if (!textGo) return;
        for (void* comp : Unity::AllComponentsInChildren(textGo, true))
            if (Unity::IsAlive(comp) && Unity::TextSetterOf(comp)) { Unity::SetTmpText(comp, v.c_str()); return; }
    }

    std::string Tag(char const* hex, std::string const& s) {
        return std::string("<color=#") + hex + ">" + s + "</color>";
    }

    void* QuickMenuRoot() {
        if (void* go = Unity::Find("Canvas_QuickMenu(Clone)")) return go;
        if (void* ui = Unity::Find("UserInterface"))
            if (void* qm = Unity::ChildByName(Unity::Transform(ui), "Canvas_QuickMenu(Clone)"))
                return Unity::GameObjectOf(qm);
        return nullptr;
    }

    struct RowBind { WingsModule* mod; std::string userId, name; };
    std::vector<RowBind*> g_binds;

    void OnRowClick(void* button, void* user) {
        (void)button;
        auto* b = static_cast<RowBind*>(user);
        if (!b) return;
        g_selection.userId = b->userId;
        g_selection.name   = b->name;
        ++g_selection.seq;
        Log::Writef("Info", "[Wings] '%s' selected -> client's PLAYERS page.", b->name.c_str());
    }
}

WingsModule::Selection WingsModule::Selected() { return g_selection; }

WingsModule::WingsModule()
    : Module("Wings", "the players panel beside the QuickMenu's left wing")
{
    Config::Bool("Wings", "ShowPositions", "show player coordinates", &m_showPositions);
    m_enabled = true;
}

void WingsModule::Drop() {
    if (Unity::IsAlive(m_panel)) Unity::Destroy(m_panel);
    m_panel = m_rows = m_wing = nullptr;
    m_countTx = nullptr;
    m_headCells = Cells{};
    m_pool.clear();
    m_cells.clear();
    for (RowBind* b : g_binds) delete b;
    g_binds.clear();
    m_signature = -1;
}

void WingsModule::OnDisable() { Drop(); Log::Info("[Wings] panel removed."); }

void WingsModule::MakeHeadCells(void* host) {
    MakeCellSet(host, kSizeHead, true, nullptr);
    WriteText(m_headCells.t[0], Tag(kHexDim, "<i>ID</i>"));
    WriteText(m_headCells.t[1], Tag(kHexDim, "<i>NAME</i>"));
    WriteText(m_headCells.t[2], Tag(kHexDim, "<b>X Y Z</b>"));
    WriteText(m_headCells.t[3], Tag(kHexDim, "<b>PLATFORM</b>"));
}

// Id left-pinned, Name flexible, Pos and Badge right-pinned -- exactly the anchoring PanelSkin.Cells
// uses, so resizing needs no arithmetic.
void WingsModule::MakeCellSet(void* host, float size, bool heading, Cells* out) {
    (void)heading;
    Cells cells{};
    cells.t[0] = MakeText(m_fontDonor, host, "Id", kColId, kRowH, kPadX, 0.f, size - 1.f, kAlignLeft);
    void* nameW = MakeText(m_fontDonor, host, "Name", 10.f, kRowH, 0.f, 0.f, size, kAlignLeft);
    cells.t[1] = nameW;
    if (nameW) {
        void* r = Unity::GetComponent(nameW, "UnityEngine.RectTransform");
        RMin(r, 0.f, 0.f); RMax(r, 1.f, 1.f); RPiv(r, 0.5f, 0.5f);
        ROffMin(r, kPadX + kColId + kColGap, 0.f);
        ROffMax(r, -(kPadX + kColBadge + kColGap + kColPos + kColGap), 0.f);
    }
    void* posW = MakeText(m_fontDonor, host, "Pos", kColPos, kRowH, 0.f, 0.f, size - 2.f, kAlignRight);
    cells.t[2] = posW;
    if (posW) {
        void* r = Unity::GetComponent(posW, "UnityEngine.RectTransform");
        RMin(r, 1.f, 0.f); RMax(r, 1.f, 1.f); RPiv(r, 1.f, 0.5f);
        RPos(r, -(kPadX + kColBadge + kColGap), 0.f); RSize(r, kColPos, 0.f);
    }
    void* badgeW = MakeText(m_fontDonor, host, "Badge", kColBadge, kRowH, 0.f, 0.f, size - 2.f, kAlignRight);
    cells.t[3] = badgeW;
    if (badgeW) {
        void* r = Unity::GetComponent(badgeW, "UnityEngine.RectTransform");
        RMin(r, 1.f, 0.f); RMax(r, 1.f, 1.f); RPiv(r, 1.f, 0.5f);
        RPos(r, -kPadX, 0.f); RSize(r, kColBadge, 0.f);
    }
    if (out) *out = cells; else m_headCells = cells;
}

bool WingsModule::Build() {
    void* qm = QuickMenuRoot();
    if (!qm) return false;
    void* window = Unity::FindChild(Unity::Transform(qm), "CanvasGroup/Container/Window");
    if (!window) return false;
    m_wing = Unity::FindChild(Unity::Transform(qm), "CanvasGroup/Container/Window/Wing_Left");

    m_fontDonor = nullptr;
    for (void* comp : Unity::AllComponentsInChildren(qm, true))
        if (Unity::IsAlive(comp) && Unity::TextSetterOf(comp)) { m_fontDonor = Unity::GameObjectOf(comp); break; }
    StealFont(qm);
    if (!m_fontDonor) { Log::Warn("[Wings] no text to borrow -- panel not built."); return false; }

    void* root = Rect("VRCA_PlayersPanel", window);
    if (!root) return false;
    m_panel = root;
    void* rootR = Unity::GetComponent(root, "UnityEngine.RectTransform");
    RMin(rootR, 0.f, 1.f); RMax(rootR, 0.f, 1.f); RPiv(rootR, 1.f, 1.f);
    RPos(rootR, -(kWingWidth + kGap), kTopY);
    RSize(rootR, kWidth, kHeight);
    void* rootTr = Unity::Transform(root);

    // Body: rounded sliced sprite + mask, so the wallpaper takes the rounded corners.
    void* body = Rect("VA_Body", rootTr);
    SetSliced(Img(body, RoundSprite(), kBody));
    Stretch(Unity::GetComponent(body, "UnityEngine.RectTransform"), 0.f);
    if (void* mask = Unity::AddComponent(body, "UnityEngine.UI.Mask"))
        if (void* m = Il2::FindMethod("UnityEngine.UI.Mask", "set_showMaskGraphic", 1)) {
            bool v = true; void* a[1] = { &v }; Il2::Invoke(m, mask, a);
        }
    void* bodyTr = Unity::Transform(body);

    if (void* wall = Unity::SpriteFromBytes(PanelAssets::kPanelPlayersJpg, PanelAssets::kPanelPlayersJpgSize)) {
        void* w = Rect("VA_Wall", bodyTr);
        Img(w, wall, Rgba{ 1.f, 1.f, 1.f, 1.f });
        Stretch(Unity::GetComponent(w, "UnityEngine.RectTransform"), 0.f);
        void* veil = Rect("VA_Veil", bodyTr);
        Img(veil, nullptr, Rgba{ 0.02f, 0.02f, 0.05f, kVeil });
        Stretch(Unity::GetComponent(veil, "UnityEngine.RectTransform"), 0.f);
    }

    // Header slab, accent bar, dot, title, count.
    void* head = Rect("VA_Header", rootTr);
    SetSliced(Img(head, RoundSprite(), kHead));
    void* hr = Unity::GetComponent(head, "UnityEngine.RectTransform");
    RMin(hr, 0.f, 1.f); RMax(hr, 1.f, 1.f); RPiv(hr, 0.5f, 1.f);
    ROffMin(hr, 0.f, -kHeaderH); ROffMax(hr, 0.f, 0.f);
    void* headTr = Unity::Transform(head);

    void* accent = Rect("VA_Accent", headTr);
    Img(accent, GradientSprite(), Rgba{ 1.f, 1.f, 1.f, 1.f });
    void* ar = Unity::GetComponent(accent, "UnityEngine.RectTransform");
    RMin(ar, 0.f, 1.f); RMax(ar, 1.f, 1.f); RPiv(ar, 0.5f, 1.f);
    ROffMin(ar, 7.f, -3.f); ROffMax(ar, -7.f, 0.f);

    void* dot = Rect("VA_Dot", headTr);
    Img(dot, DotSprite(), kPink);
    void* dr = Unity::GetComponent(dot, "UnityEngine.RectTransform");
    RMin(dr, 0.f, 0.5f); RMax(dr, 0.f, 0.5f); RPiv(dr, 0.f, 0.5f);
    RPos(dr, kPadX, -1.f); RSize(dr, 9.f, 9.f);

    if (void* title = MakeText(m_fontDonor, headTr, "VA_Title",
                               kWidth - (kPadX + 18.f) - (kPadX + 60.f), kHeaderH,
                               kPadX + 18.f, 0.f, kSizeTitle, kAlignLeft))
        WriteText(title, Tag(kHexText, "<b>JOUEURS</b>"));
    m_countTx = MakeText(m_fontDonor, headTr, "VA_Count", 90.f, kHeaderH,
                         kWidth - kPadX - 90.f, 0.f, kSizeCount, kAlignRight);
    if (m_countTx) WriteText(m_countTx, Tag(kHexDim, "0"));

    void* hair = Rect("VA_Hair", rootTr);
    Img(hair, nullptr, kHair);
    void* har = Unity::GetComponent(hair, "UnityEngine.RectTransform");
    RMin(har, 0.f, 1.f); RMax(har, 1.f, 1.f); RPiv(har, 0.5f, 1.f);
    ROffMin(har, 0.f, -kHeaderH - 1.f); ROffMax(har, 0.f, -kHeaderH);

    void* colHead = Rect("VA_ColHead", rootTr);
    void* chr = Unity::GetComponent(colHead, "UnityEngine.RectTransform");
    RMin(chr, 0.f, 1.f); RMax(chr, 1.f, 1.f); RPiv(chr, 0.5f, 1.f);
    ROffMin(chr, 0.f, -(kHeaderH + kColHeadH)); ROffMax(chr, 0.f, -kHeaderH);
    void* chTr = Unity::Transform(colHead);
    void* rule = Rect("VA_ColRule", chTr);
    Img(rule, nullptr, kRule);
    void* rlr = Unity::GetComponent(rule, "UnityEngine.RectTransform");
    RMin(rlr, 0.f, 0.f); RMax(rlr, 1.f, 0.f); RPiv(rlr, 0.5f, 0.f);
    ROffMin(rlr, kPadX * 0.5f, 0.f); ROffMax(rlr, -kPadX * 0.5f, 1.f);
    MakeHeadCells(chTr);

    void* rows = Rect("VA_Rows", rootTr);
    void* rr = Unity::GetComponent(rows, "UnityEngine.RectTransform");
    RMin(rr, 0.f, 0.f); RMax(rr, 1.f, 1.f);
    ROffMin(rr, 0.f, 0.f); ROffMax(rr, 0.f, -(kHeaderH + kColHeadH));
    m_rows = Unity::Transform(rows);

    // Frame last, drawn over everything; its texture spills Bleed past the panel on every side.
    void* frame = Rect("VA_Frame", rootTr);
    Img(frame, BorderSprite((int)kWidth, (int)kHeight), Rgba{ 1.f, 1.f, 1.f, 1.f });
    Stretch(Unity::GetComponent(frame, "UnityEngine.RectTransform"), -kBleed);

    Log::Info("[Wings] panel built (faithful port of PanelSkin).");
    return true;
}

void WingsModule::Fill() {
    auto all = Player::All();
    int sig = (int)all.size() * 31;
    for (auto const& p : all) sig = sig * 31 + (int)p.userId.size() + p.id;
    if (sig == m_signature) return;
    m_signature = sig;

    if (m_countTx) WriteText(m_countTx, Tag(kHexDim, std::to_string(all.size()) + " player(s)"));

    size_t want = all.size() < kMaxRows ? all.size() : kMaxRows;

    while (m_pool.size() < want) {
        size_t i = m_pool.size();
        void* row = Rect("VA_Row", m_rows);
        if (!row) break;
        void* rr = Unity::GetComponent(row, "UnityEngine.RectTransform");
        RMin(rr, 0.f, 1.f); RMax(rr, 1.f, 1.f); RPiv(rr, 0.5f, 1.f);
        ROffMin(rr, 0.f, -kRowH * (float)(i + 1));
        ROffMax(rr, 0.f, -kRowH * (float)i);
        void* rowTr = Unity::Transform(row);

        // The click target is the one and only raycast graphic in the panel.
        void* hit = Rect("VA_Hit", rowTr);
        SetRaycast(Img(hit, nullptr, Rgba{ 1.f, 1.f, 1.f, 0.001f }), true);
        Stretch(Unity::GetComponent(hit, "UnityEngine.RectTransform"), 0.f);
        auto* bind = new RowBind{ this, "", "" };
        g_binds.push_back(bind);
        if (void* btn = Unity::AddComponent(hit, "UnityEngine.UI.Button"))
            Clicks::On(btn, &OnRowClick, bind);

        Cells cells{};
        MakeCellSet(rowTr, kSizeRow, false, &cells);
        m_cells.push_back(cells);
        m_pool.push_back(row);
    }

    for (size_t i = 0; i < m_pool.size(); ++i) {
        void* row = m_pool[i];
        if (!Unity::IsAlive(row)) continue;
        bool used = i < want;
        Unity::SetActive(row, used);
        if (!used || i >= m_cells.size()) continue;

        Player::Info const& p = all[i];
        if (i < g_binds.size()) { g_binds[i]->userId = p.userId; g_binds[i]->name = p.name; }

        char id[16];
        std::snprintf(id, sizeof id, "[%d]", p.id);

        std::string name = Tag(p.isLocal ? kHexPink : kHexText, "<b>" + p.name + "</b>");
        if (p.isLocal)       name += "  " + Tag("7FE6FF", "<size=70%>you</size>");
        else if (p.isMaster) name += "  " + Tag("FFC08A", "<size=70%>master</size>");

        std::string pos;
        if (m_showPositions && p.hasPos) {
            char b[48];
            std::snprintf(b, sizeof b, "%.1f %.1f %.1f", (double)p.pos.x, (double)p.pos.y, (double)p.pos.z);
            pos = Tag(kHexDim, b);
        }
        std::string plat = Tag(kHexDim, (p.platform.empty() ? "--" : p.platform) + (p.inVR ? "  VR" : ""));

        WriteText(m_cells[i].t[0], Tag(kHexDim, id));
        WriteText(m_cells[i].t[1], name);
        WriteText(m_cells[i].t[2], pos);
        WriteText(m_cells[i].t[3], plat);
    }
}

void WingsModule::OnUpdate() {
    double now = Engine::Time();
    if (!Unity::IsAlive(m_panel)) {
        m_panel = nullptr;
        if (now < m_nextTry) return;
        m_nextTry = now + 2.0;
        if (m_fails >= 12) return;
        if (!Build()) { ++m_fails; return; }
        m_fails = 0;
    }
    if (Unity::IsAlive(m_wing))
        Unity::SetActive(m_panel, Unity::ActiveSelf(Unity::GameObjectOf(m_wing)));
    if (now < m_nextFill) return;
    m_nextFill = now + 1.0;
    Fill();
}

}
