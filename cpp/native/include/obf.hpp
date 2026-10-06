#pragma once
#include <string>
#include <cstddef>

// COMPILE-TIME STRING HIDING.
//
// A dump of this DLL used to read like a map of what it does: the il2cpp type and method names the
// patches target, and the tokens that gate the paid steps, all sat in the binary in plain text, so
// `strings VRChatArchiveNative.dll | grep` found every patch in seconds. OBF("x") XORs the bytes at
// COMPILE TIME with a per-call-site key and keeps only the ciphertext in the image; reveal() runs at
// the point of use and nowhere else, so the plaintext never exists statically. This raises the cost
// of finding a patch from a grep to tracing the code -- the whole reason the mod moved to C++.
//
// Scope is deliberate: the RE-sensitive constants (patch targets, entitlement tokens, the Dex tool
// and folder names), NOT every log line. A log string tells a reader nothing about where a hook is.
//
// Correctness: OBF returns a std::string built fresh each call, so `OBF("x").c_str()` is valid for
// the duration of the full-expression it is used in -- exactly how every il2cpp lookup here takes
// its name. Never store the c_str() past the statement.
namespace VRCA::Obf {

    // A byte key that varies with position AND a per-site seed, so two equal strings do not encrypt
    // to the same bytes and a frequency attack on the ciphertext gains nothing.
    constexpr unsigned char Key(std::size_t i, unsigned seed) {
        unsigned x = seed + static_cast<unsigned>(i) * 2654435761u;
        x ^= x >> 15; x *= 0x2C1B3C6Du; x ^= x >> 12;
        return static_cast<unsigned char>(x ^ (i * 31u + 0x5B));
    }

    template <std::size_t N>
    struct Hidden {
        char data[N];
        unsigned seed;
        consteval Hidden(const char (&s)[N], unsigned sd) : data{}, seed(sd) {
            for (std::size_t i = 0; i < N; ++i)
                data[i] = static_cast<char>(static_cast<unsigned char>(s[i]) ^ Key(i, sd));
        }
        // NOT constexpr on purpose: evaluating it at compile time would put the plaintext back in
        // the binary. It runs at runtime, reading the ciphertext in `data`.
        std::string reveal() const {
            std::string out;
            if (N == 0) return out;
            out.resize(N - 1);                   // drop the trailing NUL
            for (std::size_t i = 0; i + 1 < N; ++i)
                out[i] = static_cast<char>(static_cast<unsigned char>(data[i]) ^ Key(i, seed));
            return out;
        }
    };
}

// A seed unique to each source line, folded so it is not simply the line number in the binary.
#define VA_OBF_SEED (static_cast<unsigned>((__LINE__) * 2654435761u) ^ 0x9E3779B9u)

// OBF("literal") -> std::string, decrypted at runtime. The constexpr Hidden holds only ciphertext.
#define OBF(str) ([] { constexpr ::VRCA::Obf::Hidden<sizeof(str)> _h(str, VA_OBF_SEED); return _h.reveal(); }())
