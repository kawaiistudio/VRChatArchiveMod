using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx.Unity.IL2CPP;

namespace VRChatArchive
{
    // VRCHAT ARCHIVE MOD -- ONE DLL.
    //
    // Everything the mod does lives in the native engine, and the native engine is EMBEDDED in this
    // assembly as a resource. Only one file is ever deployed: BepInEx's chainloader needs a managed
    // entry point (it ignores a plain native DLL in plugins/), so this class is that entry point and
    // nothing else -- it writes the engine out beside itself and hands control over.
    //
    // Two pieces are deliberately NOT in here and never shipped with it: the AssetBundle patch and
    // the Dex patch. Those are the entitled parts; the server delivers them at runtime, to an
    // account it has itself confirmed, and the engine injects them then. Nothing in this file can
    // enable them.
    [BepInEx.BepInPlugin(Guid, "VRChat Archive", "3.0.0")]
    public sealed class ArchiveLoaderPlugin : BasePlugin
    {
        public const string Guid = "org.vrchatarchive.mod.cpp";
        private const string ResourceName = "VRChatArchive.Native.dll";
        private const string EngineFile   = "VRChatArchiveEngine.dll";

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int StartFn(
            [MarshalAs(UnmanagedType.LPStr)] string dataDir,
            [MarshalAs(UnmanagedType.LPStr)] string logDir);

        public override void Load()
        {
            try
            {
                // THE ENGINE DOES NOT GO IN plugins/.
                //
                // Anything ending in .dll inside BepInEx/plugins is listed as a mod by the client's
                // loader, and the extracted native engine is not a mod -- it is the other half of
                // this one. It goes in its own folder beside plugins instead, so the user sees
                // exactly one entry: VRChatArchiveMod.dll.
                string engineDir = Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchive");
                Directory.CreateDirectory(engineDir);
                string enginePath = Path.Combine(engineDir, EngineFile);

                if (!ExtractEngine(enginePath, out string why))
                {
                    Log.LogError($"native engine unavailable: {why}");
                    return;
                }

                IntPtr module = LoadLibraryW(enginePath);
                if (module == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.LogError($"LoadLibrary of the engine failed, Win32 error {err}"
                                 + (err == 193 ? " (the DLL is not 64-bit)." : "."));
                    return;
                }

                IntPtr start = GetProcAddress(module, "VRCA_Start");
                if (start == IntPtr.Zero) { Log.LogError("VRCA_Start is missing from the engine."); return; }

                string logDir  = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput");
                string dataDir = Path.GetFullPath(Path.Combine(BepInEx.Paths.BepInExRootPath, ".."));

                int ok = Marshal.GetDelegateForFunctionPointer<StartFn>(start)(dataDir, logDir);
                Log.LogInfo(ok != 0
                    ? "VRChat Archive loaded. Log: BepInEx/LogOutput/VRChatArchive.log."
                    : "VRChat Archive: the engine refused to start (see VRChatArchive.log).");
            }
            catch (Exception e)
            {
                Log.LogError($"VRChat Archive : {e}");
            }
        }

        /// <summary>
        /// Writes the embedded engine next to this assembly, skipping the write when the file on
        /// disk already matches (same bytes) so an update replaces it but a restart does not fight
        /// a copy the game still has mapped. A locked file that already matches is fine to reuse.
        /// </summary>
        private bool ExtractEngine(string target, out string why)
        {
            why = "";
            using Stream src = typeof(ArchiveLoaderPlugin).Assembly.GetManifestResourceStream(ResourceName);
            if (src == null)
            {
                // Fall back to a loose engine beside the plugin: that is how a developer build runs
                // before the resource is embedded.
                if (File.Exists(target)) return true;
                why = $"resource '{ResourceName}' is missing and there is no {EngineFile} beside the plugin";
                return false;
            }

            using var ms = new MemoryStream();
            src.CopyTo(ms);
            byte[] bytes = ms.ToArray();

            if (File.Exists(target) && SameBytes(target, bytes)) return true;

            try { File.WriteAllBytes(target, bytes); return true; }
            catch (Exception e)
            {
                // Already loaded by a previous session in this process: reuse it rather than fail.
                if (File.Exists(target)) return true;
                why = e.Message;
                return false;
            }
        }

        private static bool SameBytes(string path, byte[] bytes)
        {
            try
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                return Convert.ToBase64String(sha.ComputeHash(fs))
                    == Convert.ToBase64String(sha.ComputeHash(bytes));
            }
            catch { return false; }
        }
    }
}
