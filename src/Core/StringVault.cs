using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VRChatArchiveMod.Core
{
    // Runtime string decryptor (v2) for the mod's string-encryption pass (tools/ObfTool).
    //
    // Three things make this harder than a plain string-decrypt:
    //   1. NO clean key literal. The 32-byte AES root key is A[i]^B[i] — reconstructed once — so it
    //      never sits in the DLL as a copy-pastable byte[32].
    //   2. PER-CALL SALT. Every rewritten `ldstr` is emitted as `ldc.i4 <salt>; ldstr <cipher>;
    //      call D2(int,string)`, and the per-string AES key is SHA256(root || salt). A generic
    //      "call the decrypt method on each string" script (de4dot-style) fails, because each call
    //      site carries a different salt that the ciphertext depends on.
    //   3. ANTI-DEBUG. If a managed debugger is attached (dnSpy et al.), D2 returns the CIPHERTEXT,
    //      so a reverse engineer watching decrypted values through the debugger reads garbage.
    //      Normal play attaches no managed debugger, so this is a no-op for real users.
    //
    // FAIL-OPEN: any failure returns the input unchanged (never throws inside a type initializer).
    // Do NOT rename this type or D2 — ObfTool binds to them by full name, and A/B must XOR to the
    // exact key ObfTool encrypts with (the ModObfKey in the csproj).
    internal static class StringVault
    {
        private static readonly byte[] A = new byte[32]
        {
            0x50, 0xab, 0x87, 0x32, 0x1a, 0x52, 0x94, 0x5a, 0x8b, 0xaa, 0xee, 0x2c, 0x1f, 0x07, 0x3d, 0xbb,
            0xd8, 0x68, 0x42, 0x20, 0xa7, 0xb3, 0xdf, 0x2a, 0xa8, 0xd6, 0x74, 0x1b, 0x62, 0xf7, 0x11, 0x11
        };
        private static readonly byte[] B = new byte[32]
        {
            0x08, 0xdb, 0xd1, 0xbd, 0x3e, 0x0a, 0x4b, 0xce, 0xa3, 0xe1, 0xc5, 0x58, 0xbf, 0xe1, 0x7d, 0x6e,
            0x41, 0xe3, 0x30, 0x6e, 0x37, 0x11, 0x2c, 0x5e, 0x99, 0x12, 0xa5, 0xa0, 0xdb, 0xde, 0xf1, 0xbb
        };

        private static byte[] _root;
        private static byte[] Root()
        {
            if (_root != null) return _root;
            byte[] k = new byte[32];
            for (int i = 0; i < 32; i++) k[i] = (byte)(A[i] ^ B[i]);
            _root = k;
            return k;
        }

        private static readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();

        // Called by every rewritten string site. Signature and name are load-bearing for ObfTool.
        // Param order is (b64, salt) ON PURPOSE: the ldstr pushes b64 first, so ObfTool only inserts
        // AFTER the ldstr (the salt push + the call). Nothing is inserted BEFORE the ldstr, so a
        // branch that targets the ldstr still lands on a valid stack — inserting before it was what
        // broke every method with a loop/branch onto a string (InvalidProgramException, 3.9.67).
        public static string D2(string b64, int salt)
        {
            if (string.IsNullOrEmpty(b64)) return b64;
            if (System.Diagnostics.Debugger.IsAttached) return b64;   // anti-debug: hand back ciphertext
            return _cache.GetOrAdd(b64, x => Decode(salt, x));
        }

        private static byte[] PerKey(int salt)
        {
            byte[] root = Root();
            byte[] s = BitConverter.GetBytes(salt);            // 4 bytes, little-endian (matches ObfTool)
            byte[] buf = new byte[root.Length + s.Length];
            Buffer.BlockCopy(root, 0, buf, 0, root.Length);
            Buffer.BlockCopy(s, 0, buf, root.Length, s.Length);
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(buf);                    // 32-byte per-string key
            }
        }

        private static string Decode(int salt, string b64)
        {
            try
            {
                byte[] raw = Convert.FromBase64String(b64);
                if (raw.Length < 12 + 16) return b64;
                byte[] iv = new byte[12];
                Array.Copy(raw, 0, iv, 0, 12);
                int ctLen = raw.Length - 12 - 16;
                byte[] ct = new byte[ctLen];
                Array.Copy(raw, 12, ct, 0, ctLen);
                byte[] tag = new byte[16];
                Array.Copy(raw, 12 + ctLen, tag, 0, 16);
                byte[] pt = new byte[ctLen];
                using (AesGcm g = new AesGcm(PerKey(salt)))
                {
                    g.Decrypt(iv, ct, tag, pt);
                }
                return Encoding.UTF8.GetString(pt);
            }
            catch
            {
                return b64;
            }
        }
    }
}
