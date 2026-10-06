#include "recovery.hpp"
#include "il2cpp.hpp"
#include "log.hpp"

#include <cstring>
#include <string>
#include <vector>

namespace VRCA::Recovery {

namespace {
    using namespace VRCA;

    std::vector<std::string> Split(char const* list) {
        std::vector<std::string> out;
        if (!list) return out;
        std::string all(list);
        size_t start = 0;
        while (start <= all.size()) {
            size_t end = all.find(';', start);
            if (end == std::string::npos) end = all.size();
            if (end > start) out.emplace_back(all.substr(start, end - start));
            start = end + 1;
        }
        return out;
    }

    // VRC.Player + VRCPlayer, by structure: both hold a VRCPlayerApi field and reference each other,
    // and VRCPlayer alone carries a static field typed as itself. Aliases both; returns false if the
    // shape does not resolve to exactly one mutual pair.
    bool RecoverPlayerPair() {
        void* apiClass = Il2::FindClass("VRC.SDKBase.VRCPlayerApi");
        if (!apiClass) { Log::Warn("player pair: VRCPlayerApi not found."); return false; }

        auto classes = Il2::AssemblyClasses("Assembly-CSharp");
        if (classes.empty()) { Log::Warn("player pair: Assembly-CSharp empty."); return false; }

        struct Info { void* klass; std::vector<void*> fieldClasses; bool staticSelf; };
        std::vector<Info> holders;
        for (void* k : classes) {
            if (!k) continue;
            Info info{ k, {}, false };
            bool holdsApi = false;
            void* iter = nullptr;
            while (void* f = Il2::NextField(k, &iter)) {
                void* fc = Il2::FieldClass(f);
                if (!fc) continue;
                if (Il2::FieldIsStatic(f)) { if (fc == k) info.staticSelf = true; continue; }
                if (fc == apiClass) holdsApi = true;
                info.fieldClasses.push_back(fc);
            }
            if (holdsApi) holders.push_back(std::move(info));
        }

        auto holds = [](Info const& a, void* t) {
            for (void* c : a.fieldClasses) if (c == t) return true;
            return false;
        };
        std::vector<std::pair<size_t, size_t>> pairs;
        for (size_t a = 0; a < holders.size(); ++a)
            for (size_t b = a + 1; b < holders.size(); ++b)
                if (holds(holders[a], holders[b].klass) && holds(holders[b], holders[a].klass))
                    pairs.emplace_back(a, b);

        Log::Writef("Info", "player pair: %zu VRCPlayerApi holder(s), %zu mutual pair(s).",
                    holders.size(), pairs.size());
        if (pairs.size() != 1) {
            Log::Warn(pairs.empty() ? "player pair: no pair -- refused."
                                    : "player pair: multiple pairs (ambiguous) -- refused.");
            return false;
        }
        Info const& x = holders[pairs[0].first];
        Info const& y = holders[pairs[0].second];
        if (x.staticSelf == y.staticSelf) {
            Log::Warn("player pair: the self-referencing static field does not disambiguate -- refused.");
            return false;
        }
        void* vrcPlayer = x.staticSelf ? x.klass : y.klass;
        void* player    = x.staticSelf ? y.klass : x.klass;

        Il2::RegisterAlias("VRC.Player", player);
        Il2::RegisterAlias("VRCPlayer", vrcPlayer);
        Log::Writef("Info", "player pair: VRC.Player='%s' VRCPlayer='%s' (aliases set).",
                    Il2::ClassName(player) ? Il2::ClassName(player) : "?",
                    Il2::ClassName(vrcPlayer) ? Il2::ClassName(vrcPlayer) : "?");
        return true;
    }
}

bool AliasFromFieldType(char const* il2cppName, char const* owner, char const* fieldName) {
    if (Il2::FindClass(il2cppName)) return true;   // already resolves
    void* ownerClass = Il2::FindClass(owner);
    if (!ownerClass || !fieldName) return false;

    // Tolerant match: strip leading '_', <...>k__BackingField decoration, and case.
    auto norm = [](char const* z) {
        std::string o;
        for (; z && *z; ++z) {
            char c = *z;
            if (c == '_' || c == '<' || c == '>') continue;
            if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a');
            o += c;
        }
        if (auto k = o.find("kbackingfield"); k != std::string::npos) o.erase(k);
        return o;
    };
    std::string want = norm(fieldName);
    if (want.empty()) return false;

    void* iter = nullptr;
    while (void* f = Il2::NextField(ownerClass, &iter)) {
        if (Il2::FieldIsStatic(f)) continue;
        if (norm(Il2::FieldName(f)) != want) continue;
        void* fc = Il2::FieldClass(f);
        if (!fc) return false;
        Il2::RegisterAlias(il2cppName, fc);
        Log::Writef("Info", "%s recovered via the field %s.%s -> '%s' (alias set).",
                    il2cppName, owner, fieldName, Il2::ClassName(fc) ? Il2::ClassName(fc) : "?");
        return true;
    }
    return false;
}

void* FindClassByMethodParams(char const* assemblyBare, char const* paramList) {
    auto want = Split(paramList);
    if (want.empty()) return nullptr;
    auto classes = Il2::AssemblyClasses(assemblyBare);
    if (classes.empty()) { Log::Writef("Warning", "signature : assembly '%s' vide.", assemblyBare); return nullptr; }

    std::vector<void*> hits;
    for (void* k : classes) {
        if (!k) continue;
        bool found = false;
        void* it = nullptr;
        while (void* m = Il2::NextMethod(k, &it)) {
            if (Il2::MethodParamCount(m) < static_cast<int>(want.size())) continue;
            bool ok = true;
            for (size_t a = 0; a < want.size(); ++a) {
                void* pc = Il2::MethodParamClass(m, static_cast<int>(a));
                char const* pn = pc ? Il2::ClassName(pc) : nullptr;
                if (!pn || want[a] != pn) { ok = false; break; }
            }
            if (ok) { found = true; break; }
        }
        if (found) hits.push_back(k);
    }

    if (hits.size() == 1) {
        Log::Writef("Info", "signature [%s] in %s: unique class '%s'.",
                    paramList, assemblyBare, Il2::ClassName(hits[0]) ? Il2::ClassName(hits[0]) : "?");
        return hits[0];
    }
    Log::Writef("Warning", "signature [%s] in %s: %s -- refused.",
                paramList, assemblyBare, hits.empty() ? "no class" : "ambigue");
    return nullptr;
}

void RecoverCoreClasses() {
    const bool pair = RecoverPlayerPair();

    if (pair) {
        // These kept their FIELD names while their TYPE was renamed.
        AliasFromFieldType("USpeaker", "VRC.Player", "USpeaker");
        AliasFromFieldType("VRC.Networking.FlatBufferNetworkSerializer", "VRCPlayer", "serializer");
        AliasFromFieldType("VRCPlayerSyncPhysics", "VRCPlayer", "syncPhysics");
    }

    // HighlightsFX (the ESP glow) is renamed, but its SetHighlight-style method keeps the engine
    // parameter types (Renderer, Color, Boolean) obfuscation never touches.
    if (!Il2::FindClass("HighlightsFX")) {
        if (void* fx = FindClassByMethodParams("Assembly-CSharp", "Renderer;Color;Boolean")) {
            Il2::RegisterAlias("HighlightsFX", fx);
            Log::Info("HighlightsFX recovered by its method signature (alias set).");
        }
    }

    // NB: no PlayerNameplate recovery. On this build nameplates are GPU-instanced with no GameObject,
    // so the old "class with the most GameObject fields" fingerprint found the avatar manager by
    // mistake. Tags are drawn in an overlay instead; see the memory note.
}

}
