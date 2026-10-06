#include "quickmenu.hpp"

#include <algorithm>
#include "clicks.hpp"
#include "logo_asset.hpp"
#include "engine.hpp"
#include "module.hpp"
#include "il2cpp.hpp"
#include "log.hpp"
#include "unity.hpp"

#include <cstring>
#include <string>
#include <vector>

namespace VRCA {

namespace {
    constexpr char const* kQmRoot    = "Canvas_QuickMenu(Clone)";
    constexpr char const* kStrip     = "CanvasGroup/Container/Window/Page_Buttons_QM/HorizontalLayoutGroup";
    constexpr char const* kBody      = "CanvasGroup/Container/Window/QMParent/Body";
    constexpr char const* kTabDonor  = "Page_DevTools";
    constexpr char const* kPageDonor = "Menu_DevTools";
    constexpr char const* kOurTab    = "Page_VRChatArchive";
    constexpr char const* kOurPage   = "Menu_VRChatArchive";
    constexpr char const* kLabel     = "Archive";

    // Live handles, kept so the click router can show/hide without re-walking the hierarchy.
    void* g_ourPageGo = nullptr;   // our cloned page (GameObject)
    void* g_ourTabBtn = nullptr;   // our tab's Button component
    void* g_bodyTr    = nullptr;   // the page host transform
    void* g_stripTr   = nullptr;   // the tab strip transform
    void* g_tabGo     = nullptr;   // our tab, kept so the logo can be re-asserted

    // The project's own logo, built once from the bytes embedded in the DLL and reused for the
    // tab and every card. Built lazily: Sprite.Create needs the engine up.
    void* ArchiveSprite() {
        static void* sprite = nullptr;
        static bool tried = false;
        if (!tried) {
            tried = true;
            sprite = Unity::SpriteFromBytes(Assets::kLogoJpg, Assets::kLogoJpgSize);
            Log::Writef(sprite ? "Info" : "Warning", "[QuickMenu] logo Archive : %s",
                        sprite ? "sprite construit" : "FAILED to build the sprite");
        }
        return sprite;
    }

    // A card or a tab shows its picture through a child called "Icon". The exact PATH differs
    // between the two and moves with every VRChat prefab change, so the child is found BY NAME at
    // any depth -- and a miss is said out loud, because an icon that silently stays VRChat's is
    // exactly what "the logo did not change" looks like.
    bool SetIconTo(void* rootGo, void* sprite, char const* what) {
        if (!rootGo || !sprite) return false;
        void* tr = Unity::FindDeep(Unity::Transform(rootGo), "Icon");
        if (!tr) {
            Log::Writef("Warning", "[QuickMenu] %s: no 'Icon' child -- logo not placed.", what);
            return false;
        }
        void* iconGo = Unity::GameObjectOf(tr);

        // STOP THE FIGHT AT THE SOURCE. VRChat's theme component writes its own sprite back onto
        // this image, and writing ours over it four times a second made the two visibly flicker
        // against each other. Removing the theme from the whole TAB is what turned it into a
        // blank square, so only the components on the ICON object itself are switched off --
        // anything there that is not the image, its rect or its renderer is what drives it.
        static bool said = false;
        int quieted = 0;
        for (void* comp : Unity::AllComponentsInChildren(iconGo, true)) {
            if (!Unity::IsAlive(comp) || Unity::GameObjectOf(comp) != iconGo) continue;
            void* k = Il2::ClassOfObject(comp);
            char const* n = k ? Il2::ClassName(k) : nullptr;
            if (!n) continue;
            if (std::strcmp(n, "RectTransform") == 0 || std::strcmp(n, "CanvasRenderer") == 0 ||
                std::strcmp(n, "Image") == 0 || std::strcmp(n, "Transform") == 0)
                continue;
            if (Unity::TextSetterOf(comp)) continue;
            if (!Unity::BehaviourEnabled(comp)) continue;
            Unity::SetBehaviourEnabled(comp, false);
            ++quieted;
        }
        if (quieted && !said) {
            said = true;
            Log::Writef("Info", "[QuickMenu] %s: %d component(s) re-forcing VRChat's icon switched off.",
                        what, quieted);
        }

        bool ok = Unity::SetImageSprite(iconGo, sprite);
        if (!ok) Log::Writef("Warning", "[QuickMenu] %s: 'Icon' found but no Image.", what);
        return ok;
    }

    void* FindQuickMenuRoot() {
        if (void* go = Unity::Find(kQmRoot)) return go;
        if (void* ui = Unity::Find("UserInterface")) {
            if (void* qm = Unity::ChildByName(Unity::Transform(ui), kQmRoot))
                return Unity::GameObjectOf(qm);
        }
        return nullptr;
    }

    // STRIP ONLY WHAT IS OBFUSCATED.
    //
    // A first version removed everything not on a whitelist of Unity types -- and took
    // UIInvisibleGraphic (the clickable surface) and StyleElement (the theme: sprites, colours)
    // with it, which is why the tab rendered as a bare square. The components to remove are
    // exactly VRChat's own renamed controllers, and those are recognisable: an obfuscated class
    // name is high-byte garbage, every legitimate one is plain ASCII.
    int StripObfuscated(void* go) {
        static void* getComponents = Il2::FindMethod("UnityEngine.GameObject", "GetComponents", 1);
        static void* compType = [] {
            void* k = Il2::FindClass("UnityEngine.Component");
            void* cgt = Il2::Export("il2cpp_class_get_type");
            void* tgo = Il2::Export("il2cpp_type_get_object");
            if (!k || !cgt || !tgo) return static_cast<void*>(nullptr);
            using CGT = void*(*)(void*); using TGO = void*(*)(void*);
            void* t = reinterpret_cast<CGT>(cgt)(k);
            return t ? reinterpret_cast<TGO>(tgo)(t) : nullptr;
        }();
        if (!getComponents || !compType) return 0;

        void* a[1] = { compType };
        void* comps = Il2::Invoke(getComponents, go, a);
        int removed = 0, n = Il2::ArrayLength(comps);
        for (int i = 0; i < n; ++i) {
            void* c = Il2::ArrayAt(comps, i);
            if (!c) continue;
            char const* cn = Il2::ClassName(Il2::ClassOfObject(c));
            if (!cn) continue;
            bool obf = false;
            for (char const* p = cn; *p; ++p)
                if (static_cast<unsigned char>(*p) > 0x7F) { obf = true; break; }
            if (!obf) continue;
            Unity::Destroy(c);
            ++removed;
        }
        return removed;
    }

    // Hide every page under Body except ours, then show ours. VRChat's own controller does the same
    // for its pages, so this is the matching move for a page it does not know about.
    void ShowOurPage(bool show) {
        if (!g_ourPageGo || !g_bodyTr) return;
        if (show) {
            int n = Unity::ChildCount(g_bodyTr);
            for (int i = 0; i < n; ++i) {
                void* childTr = Unity::ChildAt(g_bodyTr, i);
                if (!childTr) continue;
                void* go = Unity::GameObjectOf(childTr);
                if (!go || go == g_ourPageGo) continue;
                if (Unity::ActiveSelf(go)) Unity::SetActive(go, false);
            }
        }
        Unity::SetActive(g_ourPageGo, show);
    }

    void OnOurTab(void*, void*) {
        ShowOurPage(true);
        Log::Info("[QuickMenu] Archive tab clicked -- page shown.");
    }

    // Any OTHER button in the tab strip means the user left our page; VRChat will show its own, so
    // ours must step aside or both would be drawn.
    void OnAnyClick(void* button, void*) {
        if (!g_ourPageGo || !g_stripTr || button == g_ourTabBtn) return;
        void* go = Unity::GameObjectOf(button);
        if (!go) return;
        void* tr = Unity::Transform(go);
        if (!tr) return;
        // Only react to buttons that live in the tab strip.
        if (Unity::Parent(tr) != g_stripTr) return;
        if (Unity::ActiveSelf(g_ourPageGo)) Unity::SetActive(g_ourPageGo, false);
    }

    // Each card on the page drives one module. Clicking it toggles that module and relabels the
    // card, so the state is visible without a second widget.
    struct CardBind { Module* mod; void* card; };
    std::vector<CardBind> g_cards;

    // A card's visible text lives at TextLayoutParent/Text_H4. "First TMP in children" is wrong:
    // the first one it finds is the inactive Badge, so the label silently went nowhere.
    // SETTING TEXT WHEN TMPRO ITSELF IS OBFUSCATED.
    //
    // The text component on a card reports an obfuscated class, so asking for "TMPro.TMP_Text" by
    // name can come back empty. The reliable route is to take whatever component is there and call
    // set_text on the class it ACTUALLY is -- walking up to whichever ancestor declares the setter.
    // The first card reports what it found, once, so a silent miss is visible in the log.
    void* TextSetterFor(void* comp) {
        if (!comp) return nullptr;
        for (void* k = Il2::ClassOfObject(comp); k; k = Il2::ClassParent(k))
            if (void* m = Il2::FindMethodIn(k, "set_text", 1)) return m;
        return nullptr;
    }

    // The one text component on an object, whatever its class is called.
    void* AnyTextComponent(void* go) {
        static void* getComponents = Il2::FindMethod("UnityEngine.GameObject", "GetComponents", 1);
        static void* compType = [] {
            void* k = Il2::FindClass("UnityEngine.Component");
            void* cgt = Il2::Export("il2cpp_class_get_type");
            void* tgo = Il2::Export("il2cpp_type_get_object");
            if (!k || !cgt || !tgo) return static_cast<void*>(nullptr);
            using CGT = void*(*)(void*); using TGO = void*(*)(void*);
            void* t = reinterpret_cast<CGT>(cgt)(k);
            return t ? reinterpret_cast<TGO>(tgo)(t) : nullptr;
        }();
        if (!go || !getComponents || !compType) return nullptr;
        void* a[1] = { compType };
        void* arr = Il2::Invoke(getComponents, go, a);
        int n = Il2::ArrayLength(arr);
        for (int i = 0; i < n; ++i) {
            void* c = Il2::ArrayAt(arr, i);
            if (c && TextSetterFor(c)) return c;
        }
        return nullptr;
    }

    bool SetTextAt(void* rootTr, char const* path, std::string const& text, char const* what) {
        void* t = path ? Unity::FindChild(rootTr, path) : rootTr;
        if (!t) { Log::Writef("Warning", "[QuickMenu] %s: '%s' not found.", what, path ? path : "(racine)"); return false; }
        void* go = Unity::GameObjectOf(t);
        void* comp = AnyTextComponent(go);
        void* setter = TextSetterFor(comp);
        if (!comp || !setter) { Log::Writef("Warning", "[QuickMenu] %s: no usable text component.", what); return false; }
        void* str = Il2::NewString(text.c_str());
        void* a[1] = { str };
        Il2::Invoke(setter, comp, a);
        return true;
    }

    void SetCardLabel(void* card, std::string const& text) {
        SetTextAt(Unity::Transform(card), "TextLayoutParent/Text_H4", text, "libelle carte");
    }

    void RelabelCard(CardBind const& b) {
        if (!b.mod || !b.card) return;
        SetCardLabel(b.card, std::string(b.mod->Name()) + (b.mod->Enabled() ? " : ON" : " : OFF"));
    }

    // The client can toggle a module too, and the hotkeys can. A card showing a stale ON/OFF is
    // worse than no card, so the labels are re-read from the modules rather than written once.
    void RefreshCards() {
        for (auto const& b : g_cards) {
            if (!b.mod || !b.card || !Unity::IsAlive(b.card)) continue;
            RelabelCard(b);
        }
    }

    void OnCardClick(void* button, void* user) {
        (void)button;
        auto* b = static_cast<CardBind*>(user);
        if (!b || !b->mod) return;
        b->mod->SetEnabled(!b->mod->Enabled());
        RelabelCard(*b);
        Log::Writef("Info", "[QuickMenu] %s -> %s", std::string(b->mod->Name()).c_str(),
                    b->mod->Enabled() ? "ON" : "OFF");
    }

    // REUSE VRCHAT'S OWN BUTTON, DO NOT BUILD ONE.
    //
    // The page ships three real cards; the first is the template. Cloning it keeps the exact look
    // (background, icon, shadow, the StyleElement theme) that hand-made UI never gets. The donor
    // cards are then removed, so the page is entirely ours while still looking native.
    //
    // Two fixes the first attempt needed: the label is at TextLayoutParent/Text_H4 (not the first
    // TMP), and a card carries NO Button -- its handlers are obfuscated and get stripped -- so a
    // real UI.Button is added to the clone, which is what the click router can see.

    // MAKE THE PAGE SCROLL.
    //
    // VRChat's own dev page ships three cards, so it never needed to: the ScrollRect is there but
    // its content has no height of its own, and a content that measures the viewport cannot
    // scroll by definition. With 34 cards in rows of four that is nine rows, and everything past
    // the fourth was simply unreachable.
    //
    // The fix is the content, not the ScrollRect: a ContentSizeFitter set to PreferredSize makes
    // the layout report the height its children actually need, and the ScrollRect then has
    // something to move. The grid already reports a preferred height because it is constrained
    // to a fixed column count.
    void MakePageScrollable(void* pageTr) {
        if (!pageTr) return;

        // The ScrollRect's CONTENT is the vertical layout the grid lives in.
        void* content = Unity::FindChild(pageTr, "Scrollrect/Viewport/VerticalLayoutGroup");
        if (!content) content = Unity::FindChild(pageTr, "ScrollRect/Viewport/VerticalLayoutGroup");
        if (!content) { Log::Warn("[QuickMenu] Scrollrect content not found -- no scrolling."); return; }
        void* contentGo = Unity::GameObjectOf(content);

        void* fitter = Unity::GetComponent(contentGo, "UnityEngine.UI.ContentSizeFitter");
        if (!Unity::IsAlive(fitter))
            fitter = Unity::AddComponent(contentGo, "UnityEngine.UI.ContentSizeFitter");
        if (!Unity::IsAlive(fitter)) {
            Log::Warn("[QuickMenu] ContentSizeFitter unavailable -- no scrolling.");
            return;
        }
        // Vertical = PreferredSize (2); horizontal stays Unconstrained so the width keeps
        // following the viewport instead of collapsing to the widest row.
        if (void* m = Il2::FindMethod("UnityEngine.UI.ContentSizeFitter", "set_verticalFit", 1)) {
            int v = 2;
            void* a[1] = { &v };
            Il2::Invoke(m, fitter, a);
        }

        // A layout group that CONTROLS its children's height would fight the fitter and flatten
        // the grid; letting the children keep their own height is what the grid expects.
        if (void* vlg = Unity::GetComponent(contentGo, "UnityEngine.UI.VerticalLayoutGroup")) {
            for (char const* setter : { "set_childControlHeight", "set_childForceExpandHeight" }) {
                if (void* m = Il2::FindMethod("UnityEngine.UI.VerticalLayoutGroup", setter, 1)) {
                    bool v = false;
                    void* a[1] = { &v };
                    Il2::Invoke(m, vlg, a);
                }
            }
        }

        // The ScrollRect has to be told its content, and to move vertically only: a page that
        // slides sideways under the thumbstick is worse than one that does not scroll at all.
        void* sr = Unity::FindChild(pageTr, "Scrollrect");
        if (!sr) sr = Unity::FindChild(pageTr, "ScrollRect");
        if (sr) {
            void* srComp = Unity::GetComponent(Unity::GameObjectOf(sr), "UnityEngine.UI.ScrollRect");
            if (Unity::IsAlive(srComp)) {
                void* rect = Unity::GetComponent(contentGo, "UnityEngine.RectTransform");
                if (rect)
                    if (void* m = Il2::FindMethod("UnityEngine.UI.ScrollRect", "set_content", 1)) {
                        void* a[1] = { rect };
                        Il2::Invoke(m, srComp, a);
                    }
                if (void* m = Il2::FindMethod("UnityEngine.UI.ScrollRect", "set_vertical", 1)) {
                    bool v = true; void* a[1] = { &v };
                    Il2::Invoke(m, srComp, a);
                }
                if (void* m = Il2::FindMethod("UnityEngine.UI.ScrollRect", "set_horizontal", 1)) {
                    bool v = false; void* a[1] = { &v };
                    Il2::Invoke(m, srComp, a);
                }
            }
        }
        Log::Info("[QuickMenu] scrolling armed on the page (content sized to its content).");
    }

    int FillPage(void* pageGo) {
        void* pageTr = Unity::Transform(pageGo);
        void* gridTr = Unity::FindChild(pageTr, "Scrollrect/Viewport/VerticalLayoutGroup/Buttons");
        if (!gridTr) gridTr = Unity::FindChild(pageTr, "ScrollRect/Viewport/VerticalLayoutGroup/Buttons");
        if (!gridTr) { Log::Warn("[QuickMenu] 'Buttons' grid not found."); return 0; }
        void* gridGo = Unity::GameObjectOf(gridTr);

        // The page title.
        SetTextAt(pageTr, "Header_DevTools/LeftItemContainer/Text_Title", "VRChat Archive", "page title");

        // Rows of four.
        Unity::SetGridColumns(gridGo, 4);

        // Grab the template BEFORE anything is removed, and remember the donors to delete after.
        std::vector<void*> donors;
        void* templateCard = nullptr;
        int n = Unity::ChildCount(gridTr);
        for (int i = 0; i < n; ++i) {
            void* ct = Unity::ChildAt(gridTr, i);
            if (!ct) continue;
            void* go = Unity::GameObjectOf(ct);
            if (!go) continue;
            if (!templateCard) templateCard = go;
            donors.push_back(go);
        }
        if (!templateCard) { Log::Warn("[QuickMenu] no card to clone in the grid."); return 0; }

        // ONE CARD PER MODULE, not a hand-kept list. A whitelist meant every feature ported after
        // it was written was invisible in game, however well it worked. The familiar ones keep
        // their place at the top; everything else follows in registration order.
        //
        // Only the plumbing is left out: the tab itself, and the heartbeat that proves the pump is
        // alive. Both are on permanently and a card that cannot be switched off is just noise.
        static char const* kFirst[] = { "Fly", "Movement", "Self Hide", "ESP" };
        static char const* kHidden[] = { "QuickMenu", "Heartbeat" };
        std::vector<Module*> pick;
        auto hidden = [&](Module* m) {
            for (char const* h : kHidden) if (std::string(m->Name()) == h) return true;
            return false;
        };
        for (char const* want : kFirst)
            for (Module* m : Engine::Modules())
                if (std::string(m->Name()) == want) { pick.push_back(m); break; }
        for (Module* m : Engine::Modules()) {
            if (hidden(m)) continue;
            if (std::find(pick.begin(), pick.end(), m) != pick.end()) continue;
            pick.push_back(m);
        }

        g_cards.clear();
        g_cards.reserve(pick.size());
        for (Module* mod : pick) {
            void* clone = Unity::Instantiate(templateCard);
            if (!clone) continue;
            Unity::SetName(clone, ("VRCA_Card_" + std::string(mod->Name())).c_str());
            Unity::SetParent(Unity::Transform(clone), gridTr, false);
            StripObfuscated(clone);                 // the donor's own dev command must not fire
            SetIconTo(clone, ArchiveSprite(), "carte");
            Unity::SetActive(clone, true);
            g_cards.push_back({ mod, clone });
        }

        // The originals go once our clones exist (cloning a destroyed object is not possible).
        for (void* d : donors) Unity::Destroy(d);

        // Bind AFTER the vector is final: it must not reallocate while callbacks hold pointers in.
        int wired = 0;
        for (auto& b : g_cards) {
            RelabelCard(b);
            void* btn = Unity::GetComponent(b.card, "UnityEngine.UI.Button");
            bool added = false;
            if (!btn) { btn = Unity::AddComponent(b.card, "UnityEngine.UI.Button"); added = (btn != nullptr); }
            // A card needs a raycast target under the pointer or the Button never receives one;
            // the donor's Background carries VRChat's ImageEx, which is one.
            if (btn && Clicks::On(btn, &OnCardClick, &b)) ++wired;
            Log::Writef(btn ? "Info" : "Warning", "[QuickMenu] carte '%s' : bouton %s",
                        std::string(b.mod->Name()).c_str(),
                        btn ? (added ? "ajoute" : "already present") : "MISSING -- cannot click");
        }
        Log::Writef("Info", "[QuickMenu] %d/%d carte(s) cliquables.", wired, (int)g_cards.size());
        MakePageScrollable(pageTr);     // after the cards: the content must have them to measure
        return static_cast<int>(g_cards.size());
    }
}

QuickMenuModule::QuickMenuModule()
    : Module("QuickMenu", "the VRChat Archive tab in the game's QuickMenu")
{
    m_enabled = true;
}

bool QuickMenuModule::TryInject() {
    void* qmGo = FindQuickMenuRoot();
    if (!qmGo) return false;
    void* qmTr = Unity::Transform(qmGo);
    if (!qmTr) return false;

    void* stripTr = Unity::FindChild(qmTr, kStrip);
    void* bodyTr  = Unity::FindChild(qmTr, kBody);
    if (!stripTr || !bodyTr) {
        Log::Writef("Warning", "[QuickMenu] '%s' found but not %s%s -- paths changed?",
                    kQmRoot, stripTr ? "" : "the tab bar ", bodyTr ? "" : "the page container");
        return false;
    }

    // ADOPT THE DEV TOOLS TAB RATHER THAN CLONE ONE.
    //
    // A cloned tab loses whatever drives it: the components that make it look and behave like a
    // tab are VRChat's own, and stripping them (necessary, or its page controller fights us)
    // leaves a bare square that nothing can click. Page_DevTools ships DISABLED and unused, so
    // taking it over gives a REAL tab -- native visuals, native click, native page switching --
    // and all we do is relabel it and fill the page it already points at.
    void* tabTr  = Unity::ChildByName(stripTr, kTabDonor);
    void* pageTr = Unity::ChildByName(bodyTr, kPageDonor);
    if (!tabTr || !pageTr) {
        Log::Writef("Warning", "[QuickMenu] DevTools tab/page missing: %s%s",
                    tabTr ? "" : "Page_DevTools ", pageTr ? "" : "Menu_DevTools");
        return false;
    }

    void* tabGo  = Unity::GameObjectOf(tabTr);
    void* pageGo = Unity::GameObjectOf(pageTr);

    // The tab keeps every component it has -- that is the whole point. Only its label changes.
    Unity::SetActive(tabGo, true);
    bool labelled = Unity::SetLabel(tabGo, kLabel);
    // WHAT THE TAB IS ACTUALLY MADE OF. Setting the sprite on a child called "Icon" reported
    // success and the wrench stayed on screen, so either that is not the image being drawn or
    // something writes it back. One listing of every Image under the tab settles which.
    {
        Log::Info("[QuickMenu] --- tab images ---");
        for (void* img : Unity::ComponentsInChildren(tabGo, "UnityEngine.UI.Image", true)) {
            if (!Unity::IsAlive(img)) continue;
            void* go = Unity::GameObjectOf(img);
            Log::Writef("Info", "[QuickMenu]   '%s' (actif=%s)", Unity::Name(go).c_str(),
                        Unity::ActiveSelf(go) ? "oui" : "non");
        }
    }
    Log::Writef(SetIconTo(tabGo, ArchiveSprite(), "tab") ? "Info" : "Warning",
                "[QuickMenu] Archive logo on the tab: %s",
                Unity::FindDeep(Unity::Transform(tabGo), "Icon") ? "pose" : "NOT placed");
    g_tabGo = tabGo;

    // The page is ours to fill. Its CARDS keep their look but lose their DevTools handlers, so a
    // click reaches us instead of running a dev command.
    int cards = FillPage(pageGo);

    g_ourPageGo = pageGo;
    g_bodyTr    = bodyTr;
    g_stripTr   = stripTr;
    g_ourTabBtn = nullptr;   // VRChat drives the tab itself; we do not route its click

    Log::Writef("Info", "[QuickMenu] DevTools tab adopted -> '%s' (label %s), page filled with %d button(s).",
                kLabel, labelled ? "pose" : "NOT placed", cards);
    return true;
}

void QuickMenuModule::OnUpdate() {
    double t = Engine::Time();
    if (m_done) {
        // Four times a second, cheap: the cards must show the state the modules are ACTUALLY in,
        // including after the desktop client or a hotkey flipped one behind the menu's back.
        if (t >= m_nextRelabel) {
            m_nextRelabel = t + 0.25;
            RefreshCards();
            // The theme component re-applies VRChat's own sprite whenever it restyles, and
            // removing that component is what turned the tab into a blank square. So the logo is
            // written back instead -- the same "held, not set once" shape the rest of the mod uses.
            static double nextIcon = 0.0;
            if (t >= nextIcon && g_tabGo && Unity::IsAlive(g_tabGo)) {
                nextIcon = t + 5.0;     // a backstop, not a tug-of-war: the driver is switched off
                SetIconTo(g_tabGo, ArchiveSprite(), "tab");
            }
        }
        return;
    }
    if (t < m_nextTry) return;
    m_nextTry = t + 1.0;                 // the QuickMenu is built lazily; poll once a second
    if (TryInject()) { m_done = true; return; }
    if (++m_attempts == 30)
        Log::Warn("[QuickMenu] still not injected after 30 tries -- open the QuickMenu once.");
}

}
