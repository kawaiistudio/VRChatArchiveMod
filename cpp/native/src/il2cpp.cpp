#include "il2cpp.hpp"
#include "log.hpp"
#include "obf_map.25686233.hpp"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstring>
#include <mutex>
#include <unordered_map>

namespace VRCA::Il2 {

namespace {

    HMODULE g_ga = nullptr;

    // The slice of the il2cpp C API this engine uses. Resolved once from GameAssembly's exports.
    struct Api {
        void*  (*domain_get)() = nullptr;
        void** (*domain_get_assemblies)(void*, size_t*) = nullptr;
        void*  (*assembly_get_image)(void*) = nullptr;
        char const* (*image_get_name)(void*) = nullptr;
        size_t (*image_get_class_count)(void*) = nullptr;
        void*  (*image_get_class)(void*, size_t) = nullptr;

        void*  (*class_from_il2cpp_type)(void*) = nullptr;
        void*  (*class_get_type)(void*) = nullptr;
        void*  (*class_get_parent)(void*) = nullptr;
        char const* (*class_get_name)(void*) = nullptr;
        char const* (*class_get_namespace)(void*) = nullptr;
        void*  (*class_get_fields)(void*, void**) = nullptr;
        void*  (*class_get_field_from_name)(void*, char const*) = nullptr;
        void*  (*class_get_methods)(void*, void**) = nullptr;
        void*  (*class_get_method_from_name)(void*, char const*, int) = nullptr;

        char const* (*field_get_name)(void*) = nullptr;
        size_t (*field_get_offset)(void*) = nullptr;
        void*  (*field_get_type)(void*) = nullptr;
        int    (*field_get_flags)(void*) = nullptr;

        char const* (*method_get_name)(void*) = nullptr;
        void*  (*method_get_param)(void*, uint32_t) = nullptr;
        uint32_t (*method_get_param_count)(void*) = nullptr;
        void*  (*method_get_return_type)(void*) = nullptr;

        void*  (*type_get_object)(void*) = nullptr;
        char const* (*type_get_name)(void*) = nullptr;

        void*  (*runtime_invoke)(void*, void*, void**, void**) = nullptr;
        void*  (*string_new)(char const*) = nullptr;
        void*  (*thread_attach)(void*) = nullptr;
    };
    Api  g_api;
    bool g_ready = false;

    std::unordered_map<std::string, void*> g_exportCache;

    // Standard name first (a non-obfuscated build resolves directly), then the validated remap, then
    // the short list of exports this Unity version simply does not ship.
    void* Resolve(char const* name) {
        if (!g_ga || !name) return nullptr;
        if (auto it = g_exportCache.find(name); it != g_exportCache.end()) return it->second;

        void* addr = reinterpret_cast<void*>(GetProcAddress(g_ga, name));
        if (!addr) {
            for (auto const& e : VRCA::Il2CppObf::kMap) {
                if (std::strcmp(e.standard, name) != 0) continue;
                addr = reinterpret_cast<void*>(GetProcAddress(g_ga, e.obfuscated));
                if (!addr)
                    Log::Writef("Error", "export '%s' mapped to '%s' but absent -- the table does not "
                                         "match this build.", name, e.obfuscated);
                break;
            }
        }
        if (!addr) {
            static char const* kGone[] = { "il2cpp_class_get_bitmap", "il2cpp_class_get_bitmap_size" };
            bool gone = false;
            for (char const* g : kGone) if (std::strcmp(g, name) == 0) gone = true;
            Log::Writef(gone ? "Info" : "Warning",
                        gone ? "export '%s': absent from this Unity version (normal)."
                             : "export '%s' not found (neither standard nor in the table).", name);
        }
        g_exportCache[name] = addr;
        return addr;
    }

    template <typename T> T Sym(char const* name) { return reinterpret_cast<T>(Resolve(name)); }

    // --- class index ------------------------------------------------------------------------------
    std::mutex g_indexMutex;
    bool       g_indexBuilt = false;
    std::unordered_map<std::string, void*> g_byFull;
    std::unordered_map<std::string, void*> g_byShort;
    std::unordered_map<std::string, void*> g_aliases;
    std::unordered_map<std::string, std::vector<void*>> g_byAssembly;

    std::string JoinName(char const* ns, char const* name) {
        if (ns && *ns) return std::string(ns) + "." + name;
        return name ? std::string(name) : std::string();
    }

    void BuildIndexLocked() {
        if (g_indexBuilt) return;
        g_indexBuilt = true;
        if (!g_api.domain_get || !g_api.domain_get_assemblies || !g_api.assembly_get_image ||
            !g_api.image_get_class_count || !g_api.image_get_class || !g_api.class_get_name)
            return;
        size_t n = 0;
        void** asm_ = g_api.domain_get_assemblies(g_api.domain_get(), &n);
        if (!asm_) return;
        for (size_t a = 0; a < n; ++a) {
            void* image = g_api.assembly_get_image(asm_[a]);
            if (!image) continue;
            char const* imageName = g_api.image_get_name ? g_api.image_get_name(image) : nullptr;
            std::string bare = imageName ? imageName : "";
            if (auto dot = bare.rfind(".dll"); dot != std::string::npos) bare.erase(dot);
            size_t count = g_api.image_get_class_count(image);
            auto& bucket = g_byAssembly[bare];
            bucket.reserve(bucket.size() + count);
            for (size_t c = 0; c < count; ++c) {
                void* k = g_api.image_get_class(image, c);
                if (!k) continue;
                bucket.push_back(k);
                char const* name = g_api.class_get_name(k);
                if (!name) continue;
                char const* ns = g_api.class_get_namespace ? g_api.class_get_namespace(k) : nullptr;
                g_byFull.emplace(JoinName(ns, name), k);
                g_byShort.emplace(name, k);
            }
        }
    }
}

bool Init() {
    g_ga = GetModuleHandleA("GameAssembly.dll");
    if (!g_ga) { Log::Warn("GameAssembly.dll not loaded yet."); return false; }

    g_api.domain_get             = Sym<void*(*)()>("il2cpp_domain_get");
    g_api.domain_get_assemblies  = Sym<void**(*)(void*, size_t*)>("il2cpp_domain_get_assemblies");
    g_api.assembly_get_image     = Sym<void*(*)(void*)>("il2cpp_assembly_get_image");
    g_api.image_get_name         = Sym<char const*(*)(void*)>("il2cpp_image_get_name");
    g_api.image_get_class_count  = Sym<size_t(*)(void*)>("il2cpp_image_get_class_count");
    g_api.image_get_class        = Sym<void*(*)(void*, size_t)>("il2cpp_image_get_class");

    g_api.class_from_il2cpp_type = Sym<void*(*)(void*)>("il2cpp_class_from_il2cpp_type");
    g_api.class_get_type         = Sym<void*(*)(void*)>("il2cpp_class_get_type");
    g_api.class_get_parent       = Sym<void*(*)(void*)>("il2cpp_class_get_parent");
    g_api.class_get_name         = Sym<char const*(*)(void*)>("il2cpp_class_get_name");
    g_api.class_get_namespace    = Sym<char const*(*)(void*)>("il2cpp_class_get_namespace");
    g_api.class_get_fields       = Sym<void*(*)(void*, void**)>("il2cpp_class_get_fields");
    g_api.class_get_field_from_name = Sym<void*(*)(void*, char const*)>("il2cpp_class_get_field_from_name");
    g_api.class_get_methods      = Sym<void*(*)(void*, void**)>("il2cpp_class_get_methods");
    g_api.class_get_method_from_name = Sym<void*(*)(void*, char const*, int)>("il2cpp_class_get_method_from_name");

    g_api.field_get_name   = Sym<char const*(*)(void*)>("il2cpp_field_get_name");
    g_api.field_get_offset = Sym<size_t(*)(void*)>("il2cpp_field_get_offset");
    g_api.field_get_type   = Sym<void*(*)(void*)>("il2cpp_field_get_type");
    g_api.field_get_flags  = Sym<int(*)(void*)>("il2cpp_field_get_flags");

    g_api.method_get_name        = Sym<char const*(*)(void*)>("il2cpp_method_get_name");
    g_api.method_get_param       = Sym<void*(*)(void*, uint32_t)>("il2cpp_method_get_param");
    g_api.method_get_param_count = Sym<uint32_t(*)(void*)>("il2cpp_method_get_param_count");
    g_api.method_get_return_type = Sym<void*(*)(void*)>("il2cpp_method_get_return_type");

    g_api.type_get_object = Sym<void*(*)(void*)>("il2cpp_type_get_object");
    g_api.type_get_name   = Sym<char const*(*)(void*)>("il2cpp_type_get_name");

    g_api.runtime_invoke = Sym<void*(*)(void*, void*, void**, void**)>("il2cpp_runtime_invoke");
    g_api.string_new     = Sym<void*(*)(char const*)>("il2cpp_string_new");
    g_api.thread_attach  = Sym<void*(*)(void*)>("il2cpp_thread_attach");

    // The irreducible minimum: without these the metadata cannot be walked at all.
    if (!g_api.domain_get || !g_api.class_get_name || !g_api.class_get_method_from_name ||
        !g_api.runtime_invoke) {
        Log::Error("essential il2cpp exports missing -- the remap table does not match this build.");
        return false;
    }
    g_ready = true;
    AttachThread();
    Log::Info("il2cpp engine ready (exports resolved).");
    return true;
}

bool Ready() { return g_ready; }

void AttachThread() {
    if (g_api.thread_attach && g_api.domain_get) g_api.thread_attach(g_api.domain_get());
}

void* Export(char const* standardName) { return Resolve(standardName); }

void* FindClass(char const* fullName) {
    if (!fullName || !*fullName) return nullptr;
    std::lock_guard lock(g_indexMutex);
    if (auto it = g_aliases.find(fullName); it != g_aliases.end()) return it->second;
    BuildIndexLocked();
    if (auto it = g_byFull.find(fullName); it != g_byFull.end()) return it->second;
    if (auto it = g_byShort.find(fullName); it != g_byShort.end()) return it->second;

    // A namespace in the request is a CONSTRAINT: never fall back to a short name in another
    // namespace (that is how SteamVR's Player gets mistaken for VRC.Player).
    std::string s(fullName);
    auto cut = s.find_last_of("./+");
    if (cut != std::string::npos) {
        auto tail = s.substr(cut + 1);
        if (auto it = g_byShort.find(tail); it != g_byShort.end()) {
            if (s[cut] == '.') {
                char const* ns = g_api.class_get_namespace ? g_api.class_get_namespace(it->second) : nullptr;
                if (!ns || std::strlen(ns) != cut || s.compare(0, cut, ns) != 0) return nullptr;
            }
            return it->second;
        }
    }
    return nullptr;
}

void RegisterAlias(char const* name, void* klass) {
    if (!name || !*name || !klass) return;
    std::lock_guard lock(g_indexMutex);
    g_aliases[name] = klass;
}

std::vector<void*> AssemblyClasses(char const* assemblyBare) {
    if (!assemblyBare) return {};
    std::lock_guard lock(g_indexMutex);
    BuildIndexLocked();
    auto it = g_byAssembly.find(assemblyBare);
    return (it == g_byAssembly.end()) ? std::vector<void*>{} : it->second;
}

void* FindMethodIn(void* klass, char const* methodName, int argc) {
    if (!klass || !methodName || !g_api.class_get_method_from_name) return nullptr;
    return g_api.class_get_method_from_name(klass, methodName, argc);
}

void* FindMethod(char const* className, char const* methodName, int argc) {
    return FindMethodIn(FindClass(className), methodName, argc);
}

void* Invoke(void* method, void* obj, void** args) {
    if (!method || !g_api.runtime_invoke) return nullptr;
    void* exc = nullptr;
    void* r = g_api.runtime_invoke(method, obj, args, &exc);
    return r;   // an il2cpp-side exception surfaces in `exc`; callers that care check the result
}

void* MethodPointer(void* method) {
    return method ? *reinterpret_cast<void**>(method) : nullptr;   // MethodInfo::methodPointer @ 0
}

void* FindField(void* klass, char const* fieldName) {
    if (!klass || !fieldName || !g_api.class_get_field_from_name) return nullptr;
    return g_api.class_get_field_from_name(klass, fieldName);
}

int FieldOffsetOf(void* field) {
    return (field && g_api.field_get_offset) ? static_cast<int>(g_api.field_get_offset(field)) : -1;
}

int FieldOffset(void* klass, char const* fieldName) {
    return FieldOffsetOf(FindField(klass, fieldName));
}

void* FieldClass(void* field) {
    if (!field || !g_api.field_get_type || !g_api.class_from_il2cpp_type) return nullptr;
    void* t = g_api.field_get_type(field);
    return t ? g_api.class_from_il2cpp_type(t) : nullptr;
}

bool FieldIsStatic(void* field) {
    return field && g_api.field_get_flags && (g_api.field_get_flags(field) & 0x10 /* STATIC */);
}

char const* FieldName(void* field) {
    return (field && g_api.field_get_name) ? g_api.field_get_name(field) : nullptr;
}

void* NextField(void* klass, void** iter) {
    return (klass && g_api.class_get_fields) ? g_api.class_get_fields(klass, iter) : nullptr;
}

void* NextMethod(void* klass, void** iter) {
    return (klass && g_api.class_get_methods) ? g_api.class_get_methods(klass, iter) : nullptr;
}

char const* ClassName(void* klass) {
    return (klass && g_api.class_get_name) ? g_api.class_get_name(klass) : nullptr;
}
char const* ClassNamespace(void* klass) {
    return (klass && g_api.class_get_namespace) ? g_api.class_get_namespace(klass) : nullptr;
}
void* ClassParent(void* klass) {
    return (klass && g_api.class_get_parent) ? g_api.class_get_parent(klass) : nullptr;
}
int ClassInstanceSize(void* klass) {
    // No stable export across versions; Il2CppClass::instance_size sits at +0xF8 on this build and
    // was proven correct measuring Nullable<EncryptionKey> (40 bytes).
    return klass ? *reinterpret_cast<int32_t*>(static_cast<char*>(klass) + 0xF8) : 0;
}
void* ClassOfObject(void* obj) {
    return obj ? *reinterpret_cast<void**>(obj) : nullptr;   // Il2CppObject::klass @ 0
}

char const* MethodName(void* method) {
    return (method && g_api.method_get_name) ? g_api.method_get_name(method) : nullptr;
}
int MethodParamCount(void* method) {
    return (method && g_api.method_get_param_count) ? static_cast<int>(g_api.method_get_param_count(method)) : 0;
}
void* MethodReturnClass(void* method) {
    if (!method || !g_api.method_get_return_type || !g_api.class_from_il2cpp_type) return nullptr;
    void* t = g_api.method_get_return_type(method);
    return t ? g_api.class_from_il2cpp_type(t) : nullptr;
}

void* MethodParamClass(void* method, int index) {
    if (!method || !g_api.method_get_param || !g_api.class_from_il2cpp_type) return nullptr;
    void* t = g_api.method_get_param(method, static_cast<uint32_t>(index));
    return t ? g_api.class_from_il2cpp_type(t) : nullptr;
}

void* NewString(char const* utf8) {
    return (utf8 && g_api.string_new) ? g_api.string_new(utf8) : nullptr;
}

std::string ReadString(void* s) {
    if (!s) return {};
    int len = *reinterpret_cast<int32_t*>(static_cast<char*>(s) + 0x10);
    if (len <= 0 || len > (1 << 20)) return {};
    auto const* w = reinterpret_cast<wchar_t const*>(static_cast<char*>(s) + 0x14);
    int size = WideCharToMultiByte(CP_UTF8, 0, w, len, nullptr, 0, nullptr, nullptr);
    if (size <= 0) return {};
    std::string out(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, len, out.data(), size, nullptr, nullptr);
    return out;
}

int ArrayLength(void* arr) {
    if (!arr) return 0;
    auto n = *reinterpret_cast<uint64_t*>(static_cast<char*>(arr) + 0x18);
    return n > (1u << 22) ? (1 << 22) : static_cast<int>(n);
}
void* ArrayAt(void* arr, int i) {
    return arr ? *reinterpret_cast<void**>(static_cast<char*>(arr) + 0x20 + size_t(i) * 8) : nullptr;
}

bool ClassIsValueType(void* klass) {
    static void* fn = Export("il2cpp_class_is_valuetype");
    if (!fn || !klass) return false;
    using F = bool(*)(void*);
    return reinterpret_cast<F>(fn)(klass);
}

bool MethodIsStatic(void* method) {
    static void* fn = Export("il2cpp_method_is_instance");
    if (!method) return false;
    if (fn) { using F = bool(*)(void*); return !reinterpret_cast<F>(fn)(method); }
    // Without the export, the flags word carries STATIC (0x0010) at the same place every runtime.
    return (*reinterpret_cast<unsigned short*>(static_cast<char*>(method) + 0x2E) & 0x0010) != 0;
}

void* FindClassWithField(char const* assemblyBare, char const* fieldName) {
    if (!fieldName) return nullptr;
    void* hit = nullptr;
    int hits = 0;
    for (void* k : AssemblyClasses(assemblyBare)) {
        void* it = nullptr;
        while (void* f = NextField(k, &it)) {
            char const* fn = FieldName(f);
            if (!fn || std::strcmp(fn, fieldName) != 0) continue;
            hit = k;
            ++hits;
            break;
        }
        if (hits > 1) break;
    }
    return hits == 1 ? hit : nullptr;
}

}
