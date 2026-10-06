using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	// OTHER MODS LOAD BEFORE OUR REPAIRS, AND DIE ON THE BUILD WE REPAIR.
	//
	// BepInEx loads plugins in GUID order, so "com.sinai.unityexplorer" runs before
	// "org.vrchatarchive.mod". At that moment none of this mod's il2cpp repairs exist yet: the token
	// shift is unfixed, so UnityEngine.GameObject's constructor is still bound to the wrong method.
	// UniverseLib does the ordinary thing --
	//
	//     var go = new GameObject("UniverseLib_Behaviour");
	//     GameObject.DontDestroyOnLoad(go);
	//
	// -- gets an object whose il2cpp pointer is null, and il2cpp throws NullReferenceException inside
	// DontDestroyOnLoad. BepInEx catches it, logs "Error loading [UnityExplorer 4.13.6]", and the
	// plugin is dead for the session. Thirty seconds later this mod arms TokenShiftFix and the very
	// same call would have worked.
	//
	// Nothing here patches anyone else's DLL. A plugin whose Load() threw is simply asked to Load()
	// again, once, after the repairs are in place -- which is the only thing it was ever missing.
	//
	// WHICH plugins failed is read from BepInEx's own log rather than guessed: the chainloader does
	// not expose "this one threw", but it writes the line. That keeps the retry honest -- a plugin
	// that loaded fine is never touched, so nothing can be double-initialised.
	public class PluginRetryModule : IModule
	{
		public override string Name => "PluginRetry";

		public static string Status = "idle";
		private bool _done;

		public override void OnUiReady()
		{
			if (_done) return;
			_done = true;
			try
			{
				if (ModConfig.RetryFailedPlugins != null && !ModConfig.RetryFailedPlugins.Value)
				{
					Status = "off";
					return;
				}
				var failed = FailedFromLog();
				if (failed.Count == 0) { Status = "no failed plugin"; return; }

				int ok = 0;
				foreach (string name in failed)
				{
					if (Retry(name)) ok++;
				}
				Status = ok + "/" + failed.Count + " replugged";
			}
			catch (Exception e)
			{
				Status = "failed: " + e.Message;
				VRChatArchiveModPlugin.Logger.LogWarning("[PluginRetry] " + Unwrap.Describe(e));
			}
		}

		// The names BepInEx reported as failing THIS launch. The log is rewritten per launch, so
		// anything in it belongs to this session.
		private static List<string> FailedFromLog()
		{
			var outp = new List<string>();
			try
			{
				string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
				if (!File.Exists(path)) return outp;

				// Shared read: BepInEx is writing to this file right now.
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				using var sr = new StreamReader(fs);
				string line;
				while ((line = sr.ReadLine()) != null)
				{
					int i = line.IndexOf("Error loading [", StringComparison.Ordinal);
					if (i < 0) continue;
					int s = i + "Error loading [".Length;
					int e = line.IndexOf(']', s);
					if (e <= s) continue;
					string full = line.Substring(s, e - s).Trim();     // "UnityExplorer 4.13.6"
					if (full.Length == 0 || outp.Contains(full)) continue;
					outp.Add(full);
				}
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[PluginRetry] lecture du log impossible : " + e.Message);
			}
			return outp;
		}

		// Ask the chainloader for the plugin behind that name and call Load() again. Everything is
		// reflected: BepInEx's plugin records are not part of the API this mod compiles against, and a
		// shape change must degrade to "cannot retry", never to a crash.
		private static bool Retry(string reported)
		{
			try
			{
				// "UnityExplorer 4.13.6" -> "UnityExplorer"
				string wanted = reported;
				int sp = wanted.LastIndexOf(' ');
				if (sp > 0) wanted = wanted.Substring(0, sp);

				object chain = null;
				try
				{
					Type t = Type.GetType("BepInEx.Unity.IL2CPP.IL2CPPChainloader, BepInEx.Unity.IL2CPP");
					chain = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
				}
				catch { }
				if (chain == null) { Warn(reported, "chainloader introuvable"); return false; }

				var plugins = chain.GetType().GetProperty("Plugins")?.GetValue(chain) as System.Collections.IDictionary;
				if (plugins == null) { Warn(reported, "liste de plugins illisible"); return false; }

				foreach (System.Collections.DictionaryEntry kv in plugins)
				{
					object info = kv.Value;
					if (info == null) continue;

					// PluginInfo.Metadata.Name is what the error line printed.
					object meta = info.GetType().GetProperty("Metadata")?.GetValue(info);
					string nm = meta?.GetType().GetProperty("Name")?.GetValue(meta) as string;
					if (nm == null || !string.Equals(nm, wanted, StringComparison.OrdinalIgnoreCase)) continue;

					object inst = info.GetType().GetProperty("Instance")?.GetValue(info);
					if (inst == null) { Warn(reported, "aucune instance a relancer"); return false; }

					MethodInfo load = inst.GetType().GetMethod("Load",
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
					if (load == null) { Warn(reported, "pas de Load() sur l'instance"); return false; }

					VRChatArchiveModPlugin.Logger.LogInfo("[PluginRetry] " + reported
						+ " avait echoue avant nos correctifs il2cpp — nouvelle tentative de Load()…");
					load.Invoke(inst, null);
					VRChatArchiveModPlugin.Logger.LogInfo("[PluginRetry] " + reported + " charge a la seconde tentative.");
					return true;
				}
				Warn(reported, "introuvable dans la liste du chainloader");
				return false;
			}
			catch (Exception e)
			{
				// Its Load threw again -- report it and move on. This is someone else's plugin; it does
				// not get to take the mod, or the game, down with it.
				Warn(reported, "a de nouveau echoue : " + Unwrap.Describe(e));
				return false;
			}
		}

		private static void Warn(string who, string why)
		{
			VRChatArchiveModPlugin.Logger.LogWarning("[PluginRetry] " + who + " non relance — " + why + ".");
		}
	}
}
